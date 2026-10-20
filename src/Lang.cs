using System.Globalization;

namespace Wisp
{
    /// <summary>
    /// UI language (Russian / English). Strings are written as pairs right where they are used:
    /// L.T("Очистить", "Clean"). The choice is remembered in WispCleaner.ini; the first run
    /// follows the Windows display language.
    /// </summary>
    static class L
    {
        public static bool En;

        public static string T(string ru, string en)
        {
            return En ? en : ru;
        }

        public static void Init()
        {
            string saved = Settings.Language;
            En = saved == "en" || (saved != "ru" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru");
        }

        public static void Save()
        {
            Settings.Language = En ? "en" : "ru";
            Settings.Save();
        }

        /// <summary>«name» in Russian, "name" in English.</summary>
        public static string Quote(string name)
        {
            return En ? "\"" + name + "\"" : "«" + name + "»";
        }

        /// <summary>Human-readable size: 512 КБ, 3,4 ГБ / 512 KB, 3.4 GB.</summary>
        public static string Size(long bytes)
        {
            var ci = En ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU");
            if (bytes < 1024) return bytes + (En ? " B" : " Б");
            double v = bytes / 1024.0;
            string[] units = En ? new[] { "KB", "MB", "GB", "TB" } : new[] { "КБ", "МБ", "ГБ", "ТБ" };
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            string fmt = v >= 100 || u == 0 ? "0" : (v >= 10 ? "0.#" : "0.##");
            return v.ToString(fmt, ci) + " " + units[u];
        }
    }
}
