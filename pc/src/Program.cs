using System;
using System.Threading;
using System.Windows.Forms;

namespace MicBridge
{
    static class Program
    {
        internal static int ShowMessage;

        [STAThread]
        static void Main(string[] args)
        {
            ShowMessage = Native.RegisterWindowMessage("MicBridge.ShowWindow");

            bool first;
            using (var single = new Mutex(true, "MicBridge.SingleInstance", out first))
            {
                if (!first)
                {
                    // Already running (maybe hidden in the tray): ask that copy to show itself.
                    Native.PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                bool minimized = false;
                foreach (string arg in args)
                    if (string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) minimized = true;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(minimized));
                GC.KeepAlive(single);
            }
        }
    }
}
