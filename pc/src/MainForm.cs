using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MicBridge
{
    /// <summary>The one window. Laid out like the phone app: status card, big button, then the options.</summary>
    sealed class MainForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly int[] BufferChoices = { 20, 40, 60, 100, 200 };
        static readonly string[] BufferNames =
        {
            "Fastest  (20 ms)", "Fast  (40 ms)", "Balanced  (60 ms)", "Stable  (100 ms)", "Very stable  (200 ms)"
        };

        readonly Settings _settings;
        readonly Engine _engine;
        readonly List<PhoneInfo> _phones = new List<PhoneInfo>();
        int _phonesVersion = -1;
        bool _phonesDirty;
        string _prefill = "";

        StatusDot _dot;
        Label _status, _detail, _volumeText, _phoneFixed;
        LinkLabel _hint;
        DarkComboBox _phone, _device, _bufferBox;
        RoundButton _connect;
        Slider _volume;
        LevelMeter _meter;
        ToggleSwitch _auto, _startup;
        NotifyIcon _tray;
        Timer _timer;

        bool _startHidden, _loading, _trimmed, _shownProblem;
        LinkState _shownState = (LinkState)(-1);
        string _shownStatus = "", _shownDetail = "", _shownTray = "";
        Color _shownDot = Color.Empty;
        int _ticks;
        float _level;
        readonly int _startedAt = Environment.TickCount;

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            _settings = Settings.Load();
            _engine = new Engine(_settings.PcId, Environment.MachineName);

            BuildUi();
            LoadIntoUi();

            _engine.Buffer.Gain = _settings.Volume / 100f;
            _engine.SetBufferMs(_settings.BufferMs);
            _engine.SetAutoConnect(_settings.AutoConnect, _settings.LastPhoneId);
            IPAddress lastAddress;
            if (TryParseIPv4(_settings.LastIp, out lastAddress)) _engine.SetProbeAddress(lastAddress);
            _engine.Start();

            _timer = new Timer();
            _timer.Interval = 100;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        // ------------------------------------------------------------------ building the window

        void BuildUi()
        {
            SuspendLayout();
            Text = "MicBridge";
            Font = SystemFonts.MessageBoxFont;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadAppIcon(SystemInformation.IconSize);

            FontFamily face = Font.FontFamily;
            var titleFont = new Font(face, 18f, FontStyle.Bold);
            var statusFont = new Font(face, 13f, FontStyle.Bold);
            var buttonFont = new Font(face, 11.5f, FontStyle.Bold);
            var fieldFont = new Font(face, 10f);
            var small = new Font(face, 8.25f);

            // Header, as on the phone
            var title = new Label { Text = "MicBridge", Font = titleFont, AutoSize = true, Location = new Point(15, 12) };
            var tagline = new Label
            {
                Text = "Your phone as a wireless microphone, over Wi-Fi", AutoSize = true, Location = new Point(19, 49), ForeColor = Theme.Text2
            };

            // Status card: state, a line of detail, the level bar
            var statusCard = new Card { Bounds = new Rectangle(20, 82, 400, 114) };
            _dot = new StatusDot { Bounds = new Rectangle(18, 23, 10, 10) };
            _status = new Label { Font = statusFont, AutoEllipsis = true, Bounds = new Rectangle(34, 13, 350, 28), Text = "Not connected" };
            _detail = new Label { Bounds = new Rectangle(16, 46, 368, 36), ForeColor = Theme.Text2 };
            _meter = new LevelMeter { Bounds = new Rectangle(18, 90, 364, 8) };
            statusCard.Controls.Add(_dot);
            statusCard.Controls.Add(_status);
            statusCard.Controls.Add(_detail);
            statusCard.Controls.Add(_meter);

            // The one big button (Start/Stop on the phone, Connect/Disconnect here)
            _connect = new RoundButton { Bounds = new Rectangle(20, 210, 400, 48), Font = buttonFont, Text = "Connect", TabIndex = 1 };

            // Phone card
            var phoneCard = new Card { Bounds = new Rectangle(20, 272, 400, 80), TabIndex = 0 };
            _phone = new DarkComboBox { Location = new Point(18, 36), Width = 364, DropDownStyle = ComboBoxStyle.DropDown, Font = fieldFont };
            _phoneFixed = new Label
            {
                Visible = false, AutoEllipsis = true, BackColor = Theme.Field, Font = fieldFont,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(3, 0, 0, 0)
            };
            phoneCard.Controls.Add(Caption("Phone (Wi-Fi)", 12, small));
            phoneCard.Controls.Add(_phone);
            phoneCard.Controls.Add(_phoneFixed);

            // Options card
            var optionsCard = new Card { Bounds = new Rectangle(20, 366, 400, 308), TabIndex = 2 };
            _device = new DarkComboBox { Location = new Point(18, 36), Width = 364, DropDownStyle = ComboBoxStyle.DropDownList, Font = fieldFont };
            _hint = new LinkLabel
            {
                Bounds = new Rectangle(16, 70, 368, 42), Font = small, ForeColor = Theme.Text2, LinkColor = Theme.Link,
                ActiveLinkColor = Color.White, VisitedLinkColor = Theme.Link, LinkArea = new LinkArea(0, 0), TabStop = false
            };
            _hint.LinkClicked += delegate { OpenCablePage(); };
            _bufferBox = new DarkComboBox { Location = new Point(18, 140), Width = 364, DropDownStyle = ComboBoxStyle.DropDownList, Font = fieldFont };
            _volumeText = new Label { Bounds = new Rectangle(312, 180, 72, 16), TextAlign = ContentAlignment.TopRight, Font = small, Text = "100%" };
            _volume = new Slider { Bounds = new Rectangle(18, 200, 364, 24), Minimum = 0, Maximum = 300, SmallChange = 5, LargeChange = 25 };
            var autoText = new Label { Text = "Reconnect to this phone automatically", AutoSize = true, Location = new Point(16, 240), Cursor = Cursors.Hand };
            _auto = new ToggleSwitch { Bounds = new Rectangle(340, 236, 42, 24) };
            var startupText = new Label { Text = "Start with Windows (hidden in the tray)", AutoSize = true, Location = new Point(16, 274), Cursor = Cursors.Hand };
            _startup = new ToggleSwitch { Bounds = new Rectangle(340, 270, 42, 24) };
            optionsCard.Controls.Add(Caption("Send to", 12, small));
            optionsCard.Controls.Add(_device);
            optionsCard.Controls.Add(_hint);
            optionsCard.Controls.Add(Caption("Buffer", 116, small));
            optionsCard.Controls.Add(_bufferBox);
            optionsCard.Controls.Add(Caption("Volume", 180, small));
            optionsCard.Controls.Add(_volumeText);
            optionsCard.Controls.Add(_volume);
            optionsCard.Controls.Add(autoText);
            optionsCard.Controls.Add(_auto);
            optionsCard.Controls.Add(startupText);
            optionsCard.Controls.Add(_startup);

            Controls.Add(title);
            Controls.Add(tagline);
            Controls.Add(statusCard);
            Controls.Add(_connect);
            Controls.Add(phoneCard);
            Controls.Add(optionsCard);

            AcceptButton = _connect;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 694);
            ResumeLayout(false);
            PerformLayout();

            // Row height of the drop-down lists follows their font, so it is right at any display scaling.
            _phone.ItemHeight = _device.ItemHeight = _bufferBox.ItemHeight = fieldFont.Height + 4;

            autoText.Click += delegate { _auto.Checked = !_auto.Checked; };
            startupText.Click += delegate { _startup.Checked = !_startup.Checked; };
            _connect.Click += delegate { OnConnectClick(); };
            _phone.DropDown += delegate { RefreshPhones(true); };
            _device.DropDown += delegate { ReloadDevices(); };
            _device.SelectedIndexChanged += delegate { OnDeviceChosen(); };
            _bufferBox.SelectedIndexChanged += delegate { OnBufferChosen(); };
            _volume.ValueChanged += delegate { OnVolumeChanged(); };
            _auto.CheckedChanged += delegate { OnAutoChanged(); };
            _startup.CheckedChanged += delegate { if (!_loading) SetStartup(_startup.Checked); };

            _tray = new NotifyIcon();
            _tray.Icon = LoadAppIcon(SystemInformation.SmallIconSize);
            _tray.Text = "MicBridge";
            _tray.ContextMenu = new ContextMenu(new[]
            {
                new MenuItem("Open MicBridge", delegate { RestoreFromTray(); }),
                new MenuItem("-"),
                new MenuItem("Exit", delegate { Close(); })
            });
            _tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            _tray.Visible = true;
        }

        /// <summary>The small grey heading above a field inside a card.</summary>
        static Label Caption(string text, int y, Font font)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(16, y), Font = font, ForeColor = Theme.Text2 };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar in the page colour (Windows 10 2004+ / Windows 11); silently skipped on older systems.
            try
            {
                int on = 1, colour = Theme.Bg.R | Theme.Bg.G << 8 | Theme.Bg.B << 16;
                Native.DwmSetWindowAttribute(Handle, 20, ref on, 4);
                Native.DwmSetWindowAttribute(Handle, 35, ref colour, 4);
                Native.SetWindowTheme(Handle, "DarkMode_Explorer", null);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height)
            {
                // Small or heavily scaled screen: scroll rather than run off the bottom.
                AutoScroll = true;
                Width += SystemInformation.VerticalScrollBarWidth;
                Height = area.Height;
                Top = area.Top;
            }
        }

        static Icon LoadAppIcon(Size size)
        {
            // The icon is compiled into the .exe as Win32 resource 32512; load the size that fits.
            IntPtr handle = Native.LoadImage(Native.GetModuleHandle(null), (IntPtr)32512, 1, size.Width, size.Height, 0);
            return handle != IntPtr.Zero ? Icon.FromHandle(handle) : SystemIcons.Application;
        }

        void LoadIntoUi()
        {
            _loading = true;
            for (int i = 0; i < BufferChoices.Length; i++) _bufferBox.Items.Add(BufferNames[i]);
            int pick = 2;
            for (int i = 0; i < BufferChoices.Length; i++) if (BufferChoices[i] == _settings.BufferMs) pick = i;
            _bufferBox.SelectedIndex = pick;
            _settings.BufferMs = BufferChoices[pick];

            _volume.Value = _settings.Volume;
            _volumeText.Text = _settings.Volume.ToString(CultureInfo.InvariantCulture) + "%";
            _auto.Checked = _settings.AutoConnect;
            _startup.Checked = IsStartupEnabled();

            if (_settings.LastIp.Length > 0)
            {
                _prefill = _settings.LastPhoneName.Length > 0 ? _settings.LastPhoneName + "  (" + _settings.LastIp + ")" : _settings.LastIp;
                _phone.Text = _prefill;
            }
            _loading = false;
            ReloadDevices();
        }

        // ------------------------------------------------------------------ output device

        void ReloadDevices()
        {
            List<AudioDevice> devices = AudioDevice.Playback();
            string wanted = _settings.DeviceId;
            int pick = -1, cable = -1;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].Id == wanted) pick = i;
                if (cable < 0 && devices[i].IsVirtualCable) cable = i;
            }
            if (pick < 0) pick = cable;
            if (pick < 0)
            {
                // Never start playing a live microphone through real speakers without being asked.
                devices.Insert(0, new AudioDevice { Id = "", Name = "(choose where to send the audio)" });
                pick = 0;
            }

            _loading = true;
            _device.BeginUpdate();
            _device.Items.Clear();
            for (int i = 0; i < devices.Count; i++) _device.Items.Add(devices[i]);
            _device.SelectedIndex = pick;
            _device.EndUpdate();
            _loading = false;
            OnDeviceChosen();
        }

        void OnDeviceChosen()
        {
            if (_loading) return;
            var device = _device.SelectedItem as AudioDevice;
            if (device == null) return;
            _settings.DeviceId = device.Id;
            _engine.SetDevice(device.Id);

            const string link = "Get the free VB-CABLE driver";      // a program despite its name: nothing gets plugged in
            if (device.Id.Length == 0)
            {
                // Windows only shows a microphone if a driver provides one; a "virtual cable" is that driver.
                SetHint(Theme.Danger, "No virtual microphone driver found. " + link + ", install it, then open this list again. It is software only: the phone stays wireless, no cable is plugged in.", link);
            }
            else if (device.IsVirtualCable)
            {
                SetHint(Theme.Text2, "In Discord, Zoom, OBS and other apps choose this microphone: " + MatchingMicrophone(device), null);
            }
            else if (device.IsAudioRelayDevice)
            {
                SetHint(Theme.Danger, "This AudioRelay device does not pass sound on to a microphone. " + link + " (software only, no real cable) and pick it here instead.", link);
            }
            else
            {
                SetHint(Theme.Text2, "You will hear the phone on this device. To use it as a microphone in other apps, pick a virtual microphone driver such as VB-CABLE instead.", null);
            }
        }

        void SetHint(Color color, string text, string link)
        {
            _hint.ForeColor = color;
            _hint.Text = text;
            int at = link == null ? -1 : text.IndexOf(link, StringComparison.Ordinal);
            _hint.LinkArea = at >= 0 ? new LinkArea(at, link.Length) : new LinkArea(0, 0);
        }

        /// <summary>The recording device that carries whatever is played into the given virtual cable.</summary>
        static string MatchingMicrophone(AudioDevice playback)
        {
            string name = playback.Name.ToLowerInvariant();
            string key = name.Contains("cable input") ? "cable output"
                       : name.Contains("voicemeeter") ? "voicemeeter"
                       : null;
            AudioDevice fallback = null;
            foreach (AudioDevice mic in AudioDevice.Recording())
            {
                if (key != null && mic.Name.ToLowerInvariant().Contains(key)) return mic.Name;
                if (fallback == null && mic.IsVirtualCable) fallback = mic;
            }
            return fallback != null ? fallback.Name : "the virtual microphone that belongs to this cable";
        }

        static void OpenCablePage()
        {
            try { System.Diagnostics.Process.Start("https://vb-audio.com/Cable/"); }
            catch (System.ComponentModel.Win32Exception) { }     // no default browser
            catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------------ user actions

        void OnConnectClick()
        {
            LinkState state;
            string peer, note;
            IPAddress address;
            long id;
            _engine.GetStatus(out state, out peer, out address, out id, out note);
            if (state != LinkState.Idle)
            {
                _engine.Disconnect();
                return;
            }

            string name;
            var phone = _phone.SelectedItem as PhoneInfo;
            if (phone != null && _phone.Text == phone.ToString())
            {
                address = phone.Address;
                name = phone.Name;
                id = phone.Id;
            }
            else
            {
                // Typed by hand, or a remembered phone shown as "Name  (address)".
                string text = _phone.Text.Trim();
                name = "";
                int open = text.LastIndexOf('('), close = text.LastIndexOf(')');
                if (open >= 0 && close > open)
                {
                    name = text.Substring(0, open).Trim();
                    text = text.Substring(open + 1, close - open - 1).Trim();
                }
                if (!TryParseIPv4(text, out address))
                {
                    MessageBox.Show(this,
                        "Open MicBridge on your phone and tap Start. The phone then appears in this list (it connects over Wi-Fi, no cable).\n\n" +
                        "If it does not, type the address shown in the phone app here, for example 192.168.1.23.",
                        "MicBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (name.Length == 0) name = text;
                id = 0;
                _settings.LastIp = text;
                _engine.SetProbeAddress(address);
            }
            _engine.Connect(address, name, id);
        }

        void OnBufferChosen()
        {
            if (_loading || _bufferBox.SelectedIndex < 0) return;
            _settings.BufferMs = BufferChoices[_bufferBox.SelectedIndex];
            _engine.SetBufferMs(_settings.BufferMs);
        }

        void OnVolumeChanged()
        {
            if (_loading) return;
            _settings.Volume = _volume.Value;
            _engine.Buffer.Gain = _volume.Value / 100f;
            _volumeText.Text = _volume.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }

        void OnAutoChanged()
        {
            if (_loading) return;
            _settings.AutoConnect = _auto.Checked;
            _engine.SetAutoConnect(_auto.Checked, _settings.LastPhoneId);
        }

        static bool TryParseIPv4(string text, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(text) || text.Split('.').Length != 4) return false;
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetwork;
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    string value = key == null ? null : key.GetValue("MicBridge") as string;
                    return value != null && value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (SecurityException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static void SetStartup(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue("MicBridge", "\"" + Application.ExecutablePath + "\" --minimized");
                    else key.DeleteValue("MicBridge", false);
                }
            }
            catch (SecurityException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ periodic refresh

        void OnTick(object sender, EventArgs e)
        {
            LinkState state;
            string peer, note;
            IPAddress address;
            long id;
            _engine.GetStatus(out state, out peer, out address, out id, out note);
            string outputError = state == LinkState.Connected ? _engine.OutputError : null;
            if (state == LinkState.Connected && _settings.DeviceId.Length == 0)
                outputError = "Connected, but nothing is chosen under \"Send to\" yet, so no app can hear the phone.";
            bool visible = Visible;

            if (state != _shownState)
            {
                _shownState = state;
                bool idle = state == LinkState.Idle;
                _connect.Text = idle ? "Connect" : state == LinkState.Connected ? "Disconnect" : "Cancel";
                _connect.FillColor = idle ? Theme.Accent : Theme.Danger;
                // As before, the phone cannot be changed while a connection is up: it is shown as plain text meanwhile.
                if (!idle)
                {
                    _phoneFixed.Bounds = _phone.Bounds;
                    _phoneFixed.Text = address != null ? peer + "  (" + address + ")" : peer;
                }
                _phoneFixed.Visible = !idle;
                _phone.Visible = idle;
                if (state == LinkState.Connected && address != null)
                {
                    // Remember this phone so it can be found and reconnected next time.
                    _settings.LastPhoneId = id;
                    _settings.LastPhoneName = peer;
                    _settings.LastIp = address.ToString();
                    _settings.Save();
                    _engine.SetAutoConnect(_settings.AutoConnect, id);
                    _engine.SetProbeAddress(address);
                }
                if (state != LinkState.Connected) { _level = 0; _meter.Value = 0; }
            }

            string status;
            Color dot;
            switch (state)
            {
                case LinkState.Connected: status = "Connected to " + peer; dot = outputError == null ? Theme.Level : Theme.Danger; break;
                case LinkState.Connecting: status = "Connecting to " + peer + "..."; dot = Theme.Amber; break;
                case LinkState.WaitingApproval: status = "Waiting for the phone"; dot = Theme.Amber; break;
                default: status = "Not connected"; dot = Theme.Idle; break;
            }
            if (status != _shownStatus) { _shownStatus = status; _status.Text = status; }
            if (dot != _shownDot) { _shownDot = dot; _dot.Color = dot; }

            string trayText = "MicBridge - " + status;
            if (trayText.Length > 63) trayText = trayText.Substring(0, 63);
            if (trayText != _shownTray) { _shownTray = trayText; _tray.Text = trayText; }

            if (visible)
            {
                if (state == LinkState.Connected)
                {
                    // Peak level on a 60 dB scale, falling back smoothly.
                    int peak = _engine.Buffer.TakePeak();
                    float now = peak <= 32 ? 0f : (float)(1.0 + Math.Log10(peak / 32768.0) / 3.0);
                    _level = now > _level ? now : Math.Max(now, _level - 0.06f);
                    _meter.Value = _level;
                }

                if (_ticks++ % 5 == 0)
                {
                    string detail;
                    switch (state)
                    {
                        case LinkState.Connected:
                            detail = outputError != null ? outputError
                                   : address + "    " + (_engine.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz    "
                                     + "buffer " + _engine.Buffer.FillMs + " ms    lost " + _engine.LostPackets
                                     + (_engine.PhoneMuted ? "    muted on phone" : "");
                            break;
                        case LinkState.Connecting:
                            detail = note.Length > 0 ? note : "Make sure MicBridge is started on the phone and the phone is on the same Wi-Fi/router as this PC.";
                            break;
                        case LinkState.WaitingApproval:
                            detail = "Tap Allow on your phone to let this PC use its microphone.";
                            break;
                        default:
                            detail = note.Length > 0 ? note
                                   : _phones.Count > 0 ? "Phone found. Click Connect."
                                   : "Looking for your phone over Wi-Fi... Open MicBridge on the phone and tap Start.";
                            break;
                    }
                    if (detail != _shownDetail) { _shownDetail = detail; _detail.Text = detail; }
                    bool problem = outputError != null;      // shown in red, like errors on the phone
                    if (problem != _shownProblem) { _shownProblem = problem; _detail.ForeColor = problem ? Theme.Danger : Theme.Text2; }
                }
                RefreshPhones(false);
            }

            if (!_trimmed && Environment.TickCount - _startedAt > 4000)
            {
                _trimmed = true;                 // start-up work is over: give the memory it needed back
                Native.TrimMemory();
            }
        }

        void RefreshPhones(bool immediately)
        {
            if (_engine.CopyPhones(_phones, ref _phonesVersion)) _phonesDirty = true;
            if (!_phonesDirty) return;
            bool typing = _phone.Focused && _phone.SelectedIndex < 0 && _phone.Text.Length > 0 && _phone.Text != _prefill;
            if (!immediately && (_phone.DroppedDown || typing)) return;
            _phonesDirty = false;

            var selected = _phone.SelectedItem as PhoneInfo;
            string typed = _phone.Text;
            bool untouched = typed.Trim().Length == 0 || typed == _prefill;
            int pick = -1;
            _phone.BeginUpdate();
            _phone.Items.Clear();
            for (int i = 0; i < _phones.Count; i++)
            {
                _phone.Items.Add(_phones[i]);
                if (selected != null && _phones[i].Id == selected.Id) pick = i;
            }
            if (pick < 0 && selected == null && untouched && _phones.Count > 0)
            {
                pick = 0;
                for (int i = 0; i < _phones.Count; i++) if (_phones[i].Id == _settings.LastPhoneId) pick = i;
            }
            _phone.EndUpdate();
            if (pick >= 0) _phone.SelectedIndex = pick; else _phone.Text = typed;
        }

        // ------------------------------------------------------------------ tray behaviour

        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden)
            {
                _startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                if (_timer != null) _timer.Interval = 1000;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && Visible) HideToTray();
        }

        void HideToTray()
        {
            Hide();
            _timer.Interval = 1000;              // nothing to animate while hidden
            if (!_settings.TrayTipShown)
            {
                _settings.TrayTipShown = true;
                _tray.ShowBalloonTip(4000, "MicBridge is still running", "Click this icon to open it again.", ToolTipIcon.None);
            }
            Native.TrimMemory();
        }

        void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _timer.Interval = 100;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage && Program.ShowMessage != 0)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _timer.Stop();
            _tray.Visible = false;
            _settings.Save();
            _engine.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_tray != null) _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
