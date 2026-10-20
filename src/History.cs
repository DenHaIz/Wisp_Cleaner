using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wisp
{
    enum HistoryKind { Manual, Auto, Browsers, Files, Apps }

    class HistoryEntry
    {
        public DateTime When;
        public HistoryKind Kind;
        public long Bytes;
        public string Details = "";

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case HistoryKind.Auto: return L.T("Автоочистка", "Auto-clean");
                    case HistoryKind.Browsers: return L.T("Браузеры", "Browsers");
                    case HistoryKind.Files: return L.T("Файлы", "Files");
                    case HistoryKind.Apps: return L.T("Программы", "Programs");
                    default: return L.T("Очистка", "Clean");
                }
            }
        }
    }

    /// <summary>Cleaning log: one tab-separated line per run in WispCleaner.log next to the exe.</summary>
    static class History
    {
        static long total = -1;
        static readonly object sync = new object();

        public static void Add(HistoryKind kind, long bytes, string details)
        {
            lock (sync) if (total >= 0) total += bytes;
            string line = string.Join("\t", new[]
            {
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture), kind.ToString(),
                bytes.ToString(CultureInfo.InvariantCulture), (details ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')
            });
            lock (sync)
            {
                try { File.AppendAllText(Settings.LogPath, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }

        /// <summary>Newest first.</summary>
        public static List<HistoryEntry> Load(int max = 500)
        {
            var list = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(Settings.LogPath)) return list;
                foreach (string line in File.ReadAllLines(Settings.LogPath, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    var e = new HistoryEntry();
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out e.When)) continue;
                    HistoryKind k;
                    if (Enum.TryParse(parts[1], out k)) e.Kind = k;
                    long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Bytes);
                    if (parts.Length > 3) e.Details = parts[3];
                    list.Add(e);
                }
            }
            catch { }
            list.Reverse();
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        /// <summary>Everything freed so far; read from the log once, then kept up to date in memory.</summary>
        public static long TotalFreed()
        {
            lock (sync)
            {
                if (total < 0)
                {
                    long sum = 0;
                    foreach (var e in Load(int.MaxValue)) sum += e.Bytes;
                    total = sum;
                }
                return total;
            }
        }
    }
}
