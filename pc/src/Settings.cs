using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace MicBridge
{
    /// <summary>User preferences, kept as plain "key=value" lines in %APPDATA%\MicBridge\settings.ini.</summary>
    sealed class Settings
    {
        public long PcId;                    // random id the phone uses to remember "always allow this PC"
        public string DeviceId = "";
        public int BufferMs = 60;
        public int Volume = 100;
        public bool AutoConnect = true;
        public long LastPhoneId;
        public string LastPhoneName = "";
        public string LastIp = "";
        public bool TrayTipShown;

        static string FilePath
        {
            get
            {
                // Normally %APPDATA%\MicBridge. The variable lets a test run (or a portable copy) keep its own settings.
                string custom = Environment.GetEnvironmentVariable("MICBRIDGE_SETTINGS_DIR");
                if (!string.IsNullOrEmpty(custom)) return Path.Combine(custom, "settings.ini");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicBridge", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                        long number;
                        bool isNumber = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
                        switch (key)
                        {
                            case "PcId": if (isNumber) s.PcId = number; break;
                            case "DeviceId": s.DeviceId = value; break;
                            case "BufferMs": if (isNumber && number >= 10 && number <= 300) s.BufferMs = (int)number; break;
                            case "Volume": if (isNumber && number >= 0 && number <= 300) s.Volume = (int)number; break;
                            case "AutoConnect": s.AutoConnect = value == "1"; break;
                            case "LastPhoneId": if (isNumber) s.LastPhoneId = number; break;
                            case "LastPhoneName": s.LastPhoneName = value; break;
                            case "LastIp": s.LastIp = value; break;
                            case "TrayTipShown": s.TrayTipShown = value == "1"; break;
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (s.PcId == 0)
            {
                var bytes = new byte[8];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
                s.PcId = BitConverter.ToInt64(bytes, 0) | 1;
                s.Save();
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "PcId=" + PcId.ToString(CultureInfo.InvariantCulture),
                    "DeviceId=" + DeviceId,
                    "BufferMs=" + BufferMs.ToString(CultureInfo.InvariantCulture),
                    "Volume=" + Volume.ToString(CultureInfo.InvariantCulture),
                    "AutoConnect=" + (AutoConnect ? "1" : "0"),
                    "LastPhoneId=" + LastPhoneId.ToString(CultureInfo.InvariantCulture),
                    "LastPhoneName=" + LastPhoneName.Replace('\r', ' ').Replace('\n', ' '),
                    "LastIp=" + LastIp,
                    "TrayTipShown=" + (TrayTipShown ? "1" : "0")
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
