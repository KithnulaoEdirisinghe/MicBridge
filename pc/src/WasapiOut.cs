using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MicBridge
{
    /// <summary>
    /// Plays the jitter buffer into one Windows playback device (normally a virtual audio cable)
    /// using event-driven shared-mode WASAPI. All COM objects live and die on the audio thread.
    /// </summary>
    sealed unsafe class WasapiOut
    {
        const int ClsCtxAll = 23;
        const uint EventCallback = 0x00040000, NoPersist = 0x00080000,
                   AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000,
                   PreventLoopbackCapture = 0x01000000;   // AUDCLNT_STREAMFLAGS_PREVENT_LOOPBACK_CAPTURE
        const long BufferDuration = 200000;      // 20 ms, in 100 ns units

        readonly JitterBuffer _buffer;
        Thread _thread;
        AutoResetEvent _wake;
        volatile bool _stop;
        volatile string _error;
        string _deviceId;
        int _rate;

        public WasapiOut(JitterBuffer buffer) { _buffer = buffer; }

        /// <summary>Why audio is not reaching the device right now, or null when all is well.</summary>
        public string Error { get { return _error; } }

        public void Start(string deviceId, int sampleRate)
        {
            Stop();
            _deviceId = deviceId;
            _rate = sampleRate;
            _stop = false;
            _error = null;
            _wake = new AutoResetEvent(false);
            _thread = new Thread(Run, 256 * 1024);
            _thread.Name = "MicBridge audio";
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.Highest;
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            Thread thread = _thread;
            if (thread == null) return;
            _stop = true;
            _wake.Set();
            if (thread.Join(3000)) _wake.Dispose();
            _thread = null;
            _wake = null;
            _error = null;
        }

        void Run()
        {
            AutoResetEvent wake = _wake;
            int taskIndex = 0;
            IntPtr mmcss = Native.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            try
            {
                while (!_stop)
                {
                    string error = RunDevice(wake);
                    if (_stop) break;
                    _error = error ?? "The output device stopped";
                    wake.WaitOne(1000);          // try again shortly (device unplugged, format changed...)
                }
            }
            finally
            {
                if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
            }
        }

        string RunDevice(AutoResetEvent wake)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioRenderClient render = null;
            bool started = false;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                int hr = enumerator.GetDevice(_deviceId, out device);
                if (hr < 0 || device == null) return "The selected output device is not available";

                // Ask for mono first and let Windows convert to whatever the device runs at.
                //
                // "hidden" keeps this stream out of application-audio recordings. Without it, a Discord or OBS
                // stream that records "what the PC is playing" picks the phone up from here as well, and viewers
                // hear the voice twice (once as the microphone, once in the stream sound). The virtual cable
                // itself still receives the audio. Windows versions that do not know the flag refuse it, so each
                // format is tried again without it.
                int channels = 1;
                bool hidden = true;
                for (;;)
                {
                    Guid clientId = typeof(IAudioClient).GUID;
                    object instance;
                    hr = device.Activate(ref clientId, ClsCtxAll, IntPtr.Zero, out instance);
                    if (hr < 0 || instance == null) return "Cannot open the output device" + Code(hr);
                    client = (IAudioClient)instance;

                    var format = new WAVEFORMATEX();
                    format.wFormatTag = 1;
                    format.nChannels = (ushort)channels;
                    format.nSamplesPerSec = (uint)_rate;
                    format.wBitsPerSample = 16;
                    format.nBlockAlign = (ushort)(channels * 2);
                    format.nAvgBytesPerSec = (uint)(_rate * channels * 2);
                    uint flags = EventCallback | NoPersist | AutoConvertPcm | SrcDefaultQuality | (hidden ? PreventLoopbackCapture : 0);
                    hr = client.Initialize(0, flags, BufferDuration, 0, ref format, IntPtr.Zero);
                    if (hr >= 0) break;
                    Marshal.ReleaseComObject(client);
                    client = null;
                    if (hidden) { hidden = false; continue; }
                    if (channels == 2) return "The output device rejected the audio format" + Code(hr);
                    channels = 2;
                    hidden = true;
                }

                uint bufferFrames;
                hr = client.GetBufferSize(out bufferFrames);
                if (hr < 0) return "Output device error" + Code(hr);
                hr = client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0) return "Output device error" + Code(hr);
                Guid renderId = typeof(IAudioRenderClient).GUID;
                object service;
                hr = client.GetService(ref renderId, out service);
                if (hr < 0 || service == null) return "Output device error" + Code(hr);
                render = (IAudioRenderClient)service;

                // Load the device with silence before starting, as WASAPI asks, to avoid a start-up glitch.
                IntPtr first;
                if (render.GetBuffer(bufferFrames, out first) >= 0) render.ReleaseBuffer(bufferFrames, 2);

                hr = client.Start();
                if (hr < 0) return "Cannot start the output device" + Code(hr);
                started = true;
                _error = null;

                while (!_stop)
                {
                    bool signalled = wake.WaitOne(200);
                    if (_stop) break;
                    uint padding;
                    hr = client.GetCurrentPadding(out padding);
                    if (hr < 0) return "The output device was disconnected";
                    if (!signalled) continue;
                    int frames = (int)(bufferFrames - padding);
                    if (frames <= 0) continue;
                    IntPtr data;
                    hr = render.GetBuffer((uint)frames, out data);
                    if (hr < 0) return "The output device was disconnected";
                    _buffer.Read((short*)data, frames, channels);
                    hr = render.ReleaseBuffer((uint)frames, 0);
                    if (hr < 0) return "The output device was disconnected";
                }
                return null;
            }
            catch (COMException e) { return "Output device error" + Code(e.ErrorCode); }
            catch (InvalidCastException) { return "Output device error"; }
            finally
            {
                if (started) client.Stop();
                if (render != null) Marshal.ReleaseComObject(render);
                if (client != null) Marshal.ReleaseComObject(client);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        static string Code(int hr)
        {
            return " (0x" + hr.ToString("X8") + ")";
        }
    }
}
