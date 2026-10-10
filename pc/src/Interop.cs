using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MicBridge
{
    // Minimal Core Audio (WASAPI) COM definitions. Only the methods we call are named,
    // but every interface keeps its vtable order up to the last method used.

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(16)] public IntPtr reserved;
    }

    static class Native
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT value);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll")]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int width, int height, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        /// <summary>Hands unused physical pages back to Windows; they fault back in on demand.</summary>
        public static void TrimMemory()
        {
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
    }

    sealed class AudioDevice
    {
        public string Id;
        public string Name;

        /// <summary>
        /// AudioRelay's own "Virtual Speakers" look like a cable but are not one: sound played into them
        /// does not come out of its "Virtual Mic" (measured), so they must never be picked automatically.
        /// </summary>
        public bool IsAudioRelayDevice
        {
            get { return Name.ToLowerInvariant().Contains("audiorelay"); }
        }

        /// <summary>True for loopback "cable" drivers whose playback side shows up as a microphone.</summary>
        public bool IsVirtualCable
        {
            get
            {
                if (IsAudioRelayDevice) return false;
                string n = Name.ToLowerInvariant();
                return n.Contains("cable") || n.Contains("virtual") || n.Contains("voicemeeter") || n.Contains("vb-audio");
            }
        }

        public override string ToString() { return Name; }

        const int Render = 0, Capture = 1, StateActive = 1;

        public static List<AudioDevice> Playback() { return List(Render); }
        public static List<AudioDevice> Recording() { return List(Capture); }

        static List<AudioDevice> List(int flow)
        {
            var result = new List<AudioDevice>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection all = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.EnumAudioEndpoints(flow, StateActive, out all) < 0) return result;
                int count;
                all.GetCount(out count);
                var nameKey = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore store = null;
                    try
                    {
                        if (all.Item(i, out device) < 0) continue;
                        string id;
                        if (device.GetId(out id) < 0 || id == null) continue;
                        string name = null;
                        if (device.OpenPropertyStore(0, out store) >= 0)
                        {
                            PROPVARIANT value;
                            if (store.GetValue(ref nameKey, out value) >= 0)
                            {
                                if (value.vt == 31 && value.pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.pointer);
                                Native.PropVariantClear(ref value);
                            }
                        }
                        result.Add(new AudioDevice { Id = id, Name = string.IsNullOrEmpty(name) ? "Audio device" : name });
                    }
                    finally
                    {
                        if (store != null) Marshal.ReleaseComObject(store);
                        if (device != null) Marshal.ReleaseComObject(device);
                    }
                }
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            finally
            {
                if (all != null) Marshal.ReleaseComObject(all);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }
    }
}
