using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MicBridge
{
    enum LinkState { Idle, Connecting, WaitingApproval, Connected }

    sealed class PhoneInfo
    {
        public long Id;
        public string Name;
        public IPAddress Address;
        public bool Busy;
        public long SeenAt;

        public override string ToString() { return Name + "  (" + Address + ")"; }
    }

    /// <summary>
    /// One background thread that finds phones, keeps the connection alive and moves incoming audio
    /// into the jitter buffer. The PC always talks first, so replies come back through Windows
    /// Firewall without needing an inbound rule.
    /// </summary>
    sealed class Engine
    {
        const int DiscoverEveryMs = 1500, ConnectEveryMs = 700, KeepAliveEveryMs = 1000,
                  AudioTimeoutMs = 3000, PhoneExpiryMs = 6000, MaxConcealedPackets = 4;
        const int SioUdpConnReset = unchecked((int)0x9800000C);

        public readonly JitterBuffer Buffer = new JitterBuffer();
        readonly WasapiOut _output;
        readonly long _pcId;
        readonly byte[] _pcName;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly byte[] _rx = new byte[2048];
        readonly byte[] _tx = new byte[96];

        // ---- shared with the UI thread (guarded by _sync) ----
        readonly object _sync = new object();
        readonly List<PhoneInfo> _phones = new List<PhoneInfo>();
        int _phonesVersion;
        LinkState _state;
        string _note = "";
        string _peerName = "";
        IPAddress _peerAddress;
        long _peerId;
        bool _wantConnect, _wantDisconnect, _deviceChanged, _bufferChanged;
        IPAddress _wantAddress;
        string _wantName;
        long _wantId;
        string _deviceId = "";
        int _bufferMs = 60;
        bool _autoConnect;
        long _autoPhoneId;
        bool _autoHold;
        IPAddress _probe;

        // ---- network thread only ----
        Thread _thread;
        volatile bool _quit;
        Socket _socket;
        bool _bound;
        EndPoint _from = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint _target;
        long _targetId;
        readonly List<IPEndPoint> _broadcasts = new List<IPEndPoint>();
        long _broadcastsAt = -100000, _nextDiscover, _nextConnect, _nextKeepAlive, _lastAudio;
        uint _session, _expected;
        bool _haveSeq;
        int _rate;
        readonly Random _random = new Random();

        volatile int _lost;
        volatile bool _phoneMuted;
        volatile int _sampleRate;

        public Engine(long pcId, string pcName)
        {
            _pcId = pcId;
            byte[] name = Encoding.UTF8.GetBytes(pcName ?? "PC");
            if (name.Length > 60) Array.Resize(ref name, 60);
            _pcName = name;
            _output = new WasapiOut(Buffer);
        }

        // ------------------------------------------------------------------ UI-facing API

        public int LostPackets { get { return _lost; } }
        public bool PhoneMuted { get { return _phoneMuted; } }
        public int SampleRate { get { return _sampleRate; } }
        public string OutputError { get { return _output.Error; } }

        public void Start()
        {
            _thread = new Thread(Run, 256 * 1024);
            _thread.Name = "MicBridge network";
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        public void Stop()
        {
            _quit = true;
            if (_thread != null) _thread.Join(3000);
        }

        public void Connect(IPAddress address, string name, long phoneId)
        {
            lock (_sync)
            {
                _wantAddress = address;
                _wantName = name;
                _wantId = phoneId;
                _wantConnect = true;
                _wantDisconnect = false;
                _autoHold = false;
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                _wantDisconnect = true;
                _wantConnect = false;
                _autoHold = true;                // the user asked to stop: don't jump straight back in
            }
        }

        public void SetDevice(string deviceId)
        {
            lock (_sync)
            {
                if (_deviceId == deviceId) return;
                _deviceId = deviceId ?? "";
                _deviceChanged = true;
            }
        }

        public void SetBufferMs(int ms)
        {
            lock (_sync)
            {
                if (_bufferMs == ms) return;
                _bufferMs = ms;
                _bufferChanged = true;
            }
        }

        public void SetAutoConnect(bool enabled, long phoneId)
        {
            lock (_sync) { _autoConnect = enabled; _autoPhoneId = phoneId; }
        }

        /// <summary>An address that is asked directly on every search (helps when broadcasts are blocked).</summary>
        public void SetProbeAddress(IPAddress address)
        {
            lock (_sync) { _probe = address; }
        }

        public void GetStatus(out LinkState state, out string peerName, out IPAddress peerAddress, out long peerId, out string note)
        {
            lock (_sync)
            {
                state = _state;
                peerName = _peerName;
                peerAddress = _peerAddress;
                peerId = _peerId;
                note = _note;
            }
        }

        /// <summary>Copies the list of discovered phones if it changed since <paramref name="version"/>.</summary>
        public bool CopyPhones(List<PhoneInfo> destination, ref int version)
        {
            lock (_sync)
            {
                if (version == _phonesVersion) return false;
                version = _phonesVersion;
                destination.Clear();
                destination.AddRange(_phones);
                return true;
            }
        }

        // ------------------------------------------------------------------ network thread

        void Run()
        {
            while (!_quit)
            {
                try
                {
                    if (_socket == null) OpenSocket();
                    Tick(_clock.ElapsedMilliseconds);
                    if (!_bound) { Thread.Sleep(250); continue; }
                    if (_state == LinkState.Connected) ReceiveAudio(); else ReceiveControl();
                }
                catch (SocketException) { CloseSocket(); Sleep(500); }
                catch (ObjectDisposedException) { CloseSocket(); Sleep(500); }
            }

            if (_state != LinkState.Idle && _target != null) { SendSession(Proto.Bye, _target); SendSession(Proto.Bye, _target); }
            _output.Stop();
            CloseSocket();
        }

        void Sleep(int ms)
        {
            if (!_quit) Thread.Sleep(ms);
        }

        void OpenSocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.EnableBroadcast = true;
            s.ReceiveBufferSize = 128 * 1024;
            s.ReceiveTimeout = 200;
            // Without this, an ICMP "port unreachable" from a phone that just closed the app would surface as an error.
            try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch (SocketException) { }
            _socket = s;
            _bound = false;                      // becomes true after the first datagram goes out
        }

        void CloseSocket()
        {
            if (_socket != null)
            {
                try { _socket.Close(); } catch (SocketException) { }
                _socket = null;
            }
            _bound = false;
        }

        void Tick(long now)
        {
            bool connect = false, disconnect = false, deviceChanged, bufferChanged, auto;
            IPAddress address = null, probe;
            string name = null, deviceId;
            long id = 0, autoId;
            int bufferMs;
            lock (_sync)
            {
                if (_wantDisconnect) { disconnect = true; _wantDisconnect = false; }
                if (_wantConnect) { connect = true; address = _wantAddress; name = _wantName; id = _wantId; _wantConnect = false; }
                deviceChanged = _deviceChanged; _deviceChanged = false;
                bufferChanged = _bufferChanged; _bufferChanged = false;
                deviceId = _deviceId;
                bufferMs = _bufferMs;
                auto = _autoConnect && !_autoHold;
                autoId = _autoPhoneId;
                probe = _probe;
            }

            if (disconnect || connect)
            {
                if (_state != LinkState.Idle) Leave("", true);
            }
            if (connect) BeginConnect(address, name, id, now);

            if (_state == LinkState.Connected)
            {
                if (bufferChanged) Buffer.SetBufferMs(bufferMs);
                if (deviceChanged) StartOutput(deviceId);
                if (now >= _nextKeepAlive)
                {
                    SendSession(Proto.KeepAlive, _target);
                    _nextKeepAlive = now + KeepAliveEveryMs;
                }
                if (now - _lastAudio > AudioTimeoutMs)
                {
                    // The stream dried up (Wi-Fi dropped, phone went away): keep trying to get it back.
                    _output.Stop();
                    _nextConnect = now;
                    SetState(LinkState.Connecting, "Connection lost - reconnecting...");
                }
            }
            else
            {
                // Keep searching while not streaming; it also lets us follow a phone whose address changed.
                if (now >= _nextDiscover)
                {
                    SendDiscover(now, probe);
                    ExpirePhones(now);
                    _nextDiscover = now + DiscoverEveryMs;
                }
                if (Waiting())
                {
                    if (now >= _nextConnect)
                    {
                        SendConnect();
                        _nextConnect = now + ConnectEveryMs;
                    }
                }
                else if (auto && autoId != 0)
                {
                    PhoneInfo found = null;
                    lock (_sync)
                    {
                        for (int i = 0; i < _phones.Count; i++)
                            if (_phones[i].Id == autoId && !_phones[i].Busy) { found = _phones[i]; break; }
                    }
                    if (found != null) BeginConnect(found.Address, found.Name, found.Id, now);
                }
            }
        }

        void BeginConnect(IPAddress address, string name, long id, long now)
        {
            _target = new IPEndPoint(address, Proto.Port);
            _targetId = id;
            _session = (uint)_random.Next(1, int.MaxValue);
            _nextConnect = now;
            lock (_sync)
            {
                _peerName = name ?? address.ToString();
                _peerAddress = address;
                _peerId = id;
                _state = LinkState.Connecting;
                _note = "";
            }
        }

        /// <summary>Drops back to idle, optionally telling the phone to stop sending.</summary>
        void Leave(string note, bool sayBye)
        {
            if (sayBye && _target != null)
            {
                SendSession(Proto.Bye, _target);
                SendSession(Proto.Bye, _target);
            }
            _output.Stop();
            _sampleRate = 0;
            _phoneMuted = false;
            _nextDiscover = 0;
            SetState(LinkState.Idle, note);
        }

        void SetState(LinkState state, string note)
        {
            lock (_sync) { _state = state; _note = note; }
        }

        // ------------------------------------------------------------------ receiving

        void ReceiveControl()
        {
            if (!_socket.Poll(100000, SelectMode.SelectRead)) return;
            int n = _socket.ReceiveFrom(_rx, 0, _rx.Length, SocketFlags.None, ref _from);
            if (!IsOurs(n)) return;
            long now = _clock.ElapsedMilliseconds;
            switch (_rx[3])
            {
                case Proto.Announce: OnAnnounce(n, ((IPEndPoint)_from).Address, now); break;
                case Proto.Accept: OnAccept(n, now); break;
                case Proto.Pending:
                    if (n >= 8 && Waiting() && U32(4) == _session) SetState(LinkState.WaitingApproval, "");
                    break;
                case Proto.Reject:
                    if (n >= 9 && Waiting() && U32(4) == _session)
                    {
                        string why = _rx[8] == Proto.RejectBusy ? "The phone is already streaming to another PC"
                                   : _rx[8] == Proto.RejectMic ? "The phone could not open its microphone"
                                   : "The phone declined the connection";
                        lock (_sync) { _autoHold = true; }
                        Leave(why, false);
                    }
                    break;
            }
        }

        void ReceiveAudio()
        {
            SocketError error;
            int n = _socket.Receive(_rx, 0, _rx.Length, SocketFlags.None, out error);
            if (error == SocketError.TimedOut || error == SocketError.WouldBlock || error == SocketError.ConnectionReset) return;
            if (error != SocketError.Success) throw new SocketException((int)error);
            if (!IsOurs(n) || n < 8 || U32(4) != _session) return;

            if (_rx[3] == Proto.Audio)
            {
                int samples = (n - Proto.AudioHeader) / 2;
                if (samples <= 0) return;
                uint seq = U32(8);
                if (!_haveSeq) { _expected = seq; _haveSeq = true; }
                int gap = (int)(seq - _expected);
                if (gap < 0)
                {
                    if (gap > -500) return;      // duplicate or late packet: its slot was already filled
                    gap = 0;                     // far behind: the phone restarted its counter
                }
                if (gap > 0)
                {
                    _lost += gap;
                    if (gap <= MaxConcealedPackets) Buffer.WriteGap(gap * samples);
                }
                Buffer.Write(_rx, Proto.AudioHeader, samples);
                _expected = seq + 1;
                _phoneMuted = (_rx[12] & Proto.FlagMuted) != 0;
                _lastAudio = _clock.ElapsedMilliseconds;
            }
            else if (_rx[3] == Proto.Bye)
            {
                Leave("The phone stopped the stream", false);
            }
            else if (_rx[3] == Proto.Reject && n >= 9)
            {
                lock (_sync) { _autoHold = true; }
                Leave("The phone could not open its microphone", false);
            }
        }

        bool IsOurs(int n)
        {
            return n >= 4 && _rx[0] == Proto.Magic0 && _rx[1] == Proto.Magic1 && _rx[2] == Proto.Version;
        }

        bool Waiting()
        {
            return _state == LinkState.Connecting || _state == LinkState.WaitingApproval;
        }

        uint U32(int offset)
        {
            return (uint)(_rx[offset] | _rx[offset + 1] << 8 | _rx[offset + 2] << 16 | _rx[offset + 3] << 24);
        }

        void OnAnnounce(int n, IPAddress from, long now)
        {
            if (n < 14) return;
            int nameLength = _rx[13];
            if (14 + nameLength > n) return;
            long id = BitConverter.ToInt64(_rx, 5);
            bool busy = _rx[4] != 0;
            if (Waiting() && _targetId != 0 && id == _targetId && !from.Equals(_target.Address))
            {
                // Same phone, new address (router handed out a different one): follow it.
                _target = new IPEndPoint(from, Proto.Port);
                lock (_sync) { _peerAddress = from; }
            }
            lock (_sync)
            {
                PhoneInfo phone = null;
                for (int i = 0; i < _phones.Count; i++)
                    if (_phones[i].Id == id) { phone = _phones[i]; break; }
                if (phone != null && phone.Busy == busy && phone.Address.Equals(from) && phone.Name.Length > 0)
                {
                    phone.SeenAt = now;          // nothing new: no allocation, no UI refresh
                    return;
                }
                string name = Encoding.UTF8.GetString(_rx, 14, nameLength);
                if (name.Length == 0) name = "Phone";
                var updated = new PhoneInfo { Id = id, Name = name, Address = from, Busy = busy, SeenAt = now };
                if (phone != null) _phones[_phones.IndexOf(phone)] = updated; else _phones.Add(updated);
                _phonesVersion++;
            }
        }

        void ExpirePhones(long now)
        {
            lock (_sync)
            {
                for (int i = _phones.Count - 1; i >= 0; i--)
                {
                    if (now - _phones[i].SeenAt > PhoneExpiryMs)
                    {
                        _phones.RemoveAt(i);
                        _phonesVersion++;
                    }
                }
            }
        }

        void OnAccept(int n, long now)
        {
            if (n < 22 || !Waiting() || U32(4) != _session) return;
            int rate = (int)U32(8);
            int channels = _rx[12];
            long phoneId = BitConverter.ToInt64(_rx, 13);
            int nameLength = _rx[21];
            if (rate < 8000 || rate > 48000 || channels != 1 || 22 + nameLength > n) return;
            string name = Encoding.UTF8.GetString(_rx, 22, nameLength);

            string deviceId;
            int bufferMs;
            _targetId = phoneId;
            lock (_sync)
            {
                deviceId = _deviceId;
                bufferMs = _bufferMs;
                _deviceChanged = false;
                _bufferChanged = false;
                if (name.Length > 0) _peerName = name;
                _peerId = phoneId;
                _state = LinkState.Connected;
                _note = "";
            }
            _rate = rate;
            _sampleRate = rate;
            _haveSeq = false;
            _lost = 0;
            _lastAudio = now;
            _nextKeepAlive = now;
            Buffer.Configure(rate, bufferMs);
            StartOutput(deviceId);
        }

        void StartOutput(string deviceId)
        {
            // An empty id means the user has not chosen where the audio should go yet.
            if (deviceId.Length > 0) _output.Start(deviceId, _rate); else _output.Stop();
        }

        // ------------------------------------------------------------------ sending

        int Header(byte type)
        {
            _tx[0] = Proto.Magic0; _tx[1] = Proto.Magic1; _tx[2] = Proto.Version; _tx[3] = type;
            return 4;
        }

        void PutSession()
        {
            _tx[4] = (byte)_session; _tx[5] = (byte)(_session >> 8); _tx[6] = (byte)(_session >> 16); _tx[7] = (byte)(_session >> 24);
        }

        void SendSession(byte type, IPEndPoint to)
        {
            Header(type);
            PutSession();
            Send(8, to);
        }

        void SendConnect()
        {
            Header(Proto.Connect);
            PutSession();
            for (int i = 0; i < 8; i++) _tx[8 + i] = (byte)(_pcId >> (8 * i));
            _tx[16] = (byte)_pcName.Length;
            Array.Copy(_pcName, 0, _tx, 17, _pcName.Length);
            Send(17 + _pcName.Length, _target);
        }

        void SendDiscover(long now, IPAddress probe)
        {
            if (now - _broadcastsAt > 10000)
            {
                RefreshBroadcasts();
                _broadcastsAt = now;
            }
            Header(Proto.Discover);
            if (probe != null) Send(4, new IPEndPoint(probe, Proto.Port));
            for (int i = 0; i < _broadcasts.Count; i++) Send(4, _broadcasts[i]);
        }

        bool Send(int length, IPEndPoint to)
        {
            try
            {
                _socket.SendTo(_tx, 0, length, SocketFlags.None, to);
                _bound = true;
                return true;
            }
            catch (SocketException) { return false; }    // no route on this adapter right now
        }

        /// <summary>One broadcast address per active network adapter, so the phone is found whether this PC is on Wi-Fi or wired to the same router.</summary>
        void RefreshBroadcasts()
        {
            _broadcasts.Clear();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask == null) continue;
                        byte[] a = info.Address.GetAddressBytes(), m = info.IPv4Mask.GetAddressBytes();
                        if (m[0] == 0) continue;
                        for (int i = 0; i < 4; i++) a[i] |= (byte)~m[i];
                        var endPoint = new IPEndPoint(new IPAddress(a), Proto.Port);
                        if (!_broadcasts.Contains(endPoint) && _broadcasts.Count < 8) _broadcasts.Add(endPoint);
                    }
                }
            }
            catch (NetworkInformationException) { }
            _broadcasts.Add(new IPEndPoint(IPAddress.Broadcast, Proto.Port));
        }
    }
}
