using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wisp
{
    enum Schedule { Daily = 0, Weekly = 1, LowSpace = 2 }

    /// <summary>
    /// Portable settings: WispCleaner.ini next to the exe, or %LOCALAPPDATA%\WispCleaner when that
    /// folder is read-only (e.g. Program Files). Nothing is written to the registry.
    /// Auto-clean and start-with-Windows are off until the user turns them on.
    /// </summary>
    static class Settings
    {
        public static string Theme = "dark";
        public static string Language = "";
        public static bool AutoClean;
        public static Schedule Schedule = Schedule.Weekly;
        public static int ThresholdGb = 10;
        public static bool Autostart;
        public static DateTime LastAuto = DateTime.MinValue;
        public static DateTime LastLowSpace = DateTime.MinValue;
        public static bool TrayHintShown;
        /// <summary>Per-item choices that differ from the catalog defaults (id → on/off).</summary>
        public static readonly Dictionary<string, bool> Items = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        static string dir;

        /// <summary>Raised after every save (the autostart copy mirrors the settings).</summary>
        public static Action Saved;

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetEntryAssembly().Location; }
        }

        /// <summary>Folder for the .ini, the cleaning log and registry backups.</summary>
        public static string Dir
        {
            get
            {
                if (dir == null) dir = PickDir();
                return dir;
            }
        }

        public static string IniPath { get { return Path.Combine(Dir, "WispCleaner.ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, "WispCleaner.log"); } }
        public static string BackupDir { get { return Path.Combine(Dir, "WispCleaner Backup"); } }

        static string PickDir()
        {
            string exeDir = Path.GetDirectoryName(ExePath);
            try
            {
                string probe = Path.Combine(exeDir, ".wisp-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return exeDir;
            }
            catch
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WispCleaner");
                try { Directory.CreateDirectory(local); } catch { }
                return local;
            }
        }

        public static void Load()
        {
            string path = IniPath;
            if (!File.Exists(path)) return;
            string section = "";
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';') continue;
                    if (line[0] == '[' && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    if (section.Equals("Items", StringComparison.OrdinalIgnoreCase)) Items[key] = val == "1";
                    else Apply(key, val);
                }
            }
            catch { }
        }

        static void Apply(string key, string val)
        {
            int n;
            switch (key)
            {
                case "Theme": Theme = val; break;
                case "Language": Language = val; break;
                case "AutoClean": AutoClean = val == "1"; break;
                case "Schedule": if (int.TryParse(val, out n) && n >= 0 && n <= 2) Schedule = (Schedule)n; break;
                case "ThresholdGb": if (int.TryParse(val, out n) && n > 0) ThresholdGb = n; break;
                case "Autostart": Autostart = val == "1"; break;
                case "LastAuto": LastAuto = ParseDate(val); break;
                case "LastLowSpace": LastLowSpace = ParseDate(val); break;
                case "TrayHintShown": TrayHintShown = val == "1"; break;
            }
        }

        static DateTime ParseDate(string s)
        {
            DateTime d;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
        }

        static string FormatDate(DateTime d)
        {
            return d == DateTime.MinValue ? "" : d.ToString("o", CultureInfo.InvariantCulture);
        }

        public static void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; Wisp Cleaner settings");
            sb.AppendLine("[Settings]");
            sb.AppendLine("Theme=" + Theme);
            sb.AppendLine("Language=" + Language);
            sb.AppendLine("AutoClean=" + (AutoClean ? "1" : "0"));
            sb.AppendLine("Schedule=" + (int)Schedule);
            sb.AppendLine("ThresholdGb=" + ThresholdGb);
            sb.AppendLine("Autostart=" + (Autostart ? "1" : "0"));
            sb.AppendLine("LastAuto=" + FormatDate(LastAuto));
            sb.AppendLine("LastLowSpace=" + FormatDate(LastLowSpace));
            sb.AppendLine("TrayHintShown=" + (TrayHintShown ? "1" : "0"));
            sb.AppendLine("[Items]");
            foreach (var kv in Items) sb.AppendLine(kv.Key + "=" + (kv.Value ? "1" : "0"));
            try { File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
            if (Saved != null) Saved();
        }
    }
}
