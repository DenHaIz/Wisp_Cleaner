using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Wisp
{
    enum BrowserKind { Chromium, Firefox }

    /// <summary>What a browser profile can be cleaned of.</summary>
    enum BrowserData { Cache, Cookies, History, Sessions }

    class BrowserInfo
    {
        public string Id, Name, Process, ExeName, ExtensionsUrl;
        public BrowserKind Kind;
        public string UserData;      // Chromium "User Data" / Firefox Roaming root
        public string LocalData;     // Opera and Firefox keep the cache under %LOCALAPPDATA%
        public readonly List<BrowserProfile> Profiles = new List<BrowserProfile>();

        public bool IsRunning
        {
            get
            {
                try { return System.Diagnostics.Process.GetProcessesByName(Process).Length > 0; }
                catch { return false; }
            }
        }

        public string FindExe()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + ExeName))
                    {
                        string p = k != null ? k.GetValue("") as string : null;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p.Trim('"'))) return p.Trim('"');
                    }
                }
                catch { }
            }
            return null;
        }
    }

    class BrowserProfile
    {
        public BrowserInfo Browser;
        public string Dir;       // profile data
        public string CacheDir;  // where the HTTP cache lives (same as Dir for most Chromium browsers)
        public string Name;

        public string Title
        {
            get { return Browser.Profiles.Count > 1 ? Browser.Name + " — " + Name : Browser.Name; }
        }

        /// <summary>Folders and files that hold the given kind of data in this profile.</summary>
        public List<string> Paths(BrowserData what)
        {
            var list = new List<string>();
            if (Browser.Kind == BrowserKind.Chromium)
            {
                switch (what)
                {
                    case BrowserData.Cache:
                        foreach (string d in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\ScriptCache" })
                            list.Add(Path.Combine(CacheDir, d));
                        if (!string.Equals(CacheDir, Dir, StringComparison.OrdinalIgnoreCase))
                            foreach (string d in new[] { "Code Cache", "GPUCache" }) list.Add(Path.Combine(Dir, d));
                        break;
                    case BrowserData.Cookies:
                        foreach (string f in new[] { @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal" })
                            list.Add(Path.Combine(Dir, f));
                        break;
                    case BrowserData.History:
                        foreach (string f in new[] { "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal",
                                                     "Shortcuts", "Shortcuts-journal", "Network Action Predictor", "Network Action Predictor-journal" })
                            list.Add(Path.Combine(Dir, f));
                        break;
                    case BrowserData.Sessions:
                        foreach (string f in new[] { "Sessions", "Session Storage", "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
                            list.Add(Path.Combine(Dir, f));
                        break;
                }
            }
            else
            {
                switch (what)
                {
                    case BrowserData.Cache:
                        foreach (string d in new[] { "cache2", "startupCache", "thumbnails" }) list.Add(Path.Combine(CacheDir, d));
                        break;
                    case BrowserData.Cookies:
                        foreach (string f in new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm" }) list.Add(Path.Combine(Dir, f));
                        break;
                    case BrowserData.Sessions:
                        foreach (string f in new[] { "sessionstore.jsonlz4", "sessionstore-backups" }) list.Add(Path.Combine(Dir, f));
                        break;
                    // History lives in places.sqlite together with bookmarks: not touched.
                }
            }
            return list;
        }

        public bool Supports(BrowserData what)
        {
            return Browser.Kind == BrowserKind.Chromium || what != BrowserData.History;
        }
    }

    class ExtensionInfo
    {
        public BrowserProfile Profile;
        public string Id, Name, Version, Dir;
        public long Size;
    }

    static class Browsers
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }

        /// <summary>Installed browsers that have at least one profile on disk.</summary>
        public static List<BrowserInfo> Detect()
        {
            var list = new List<BrowserInfo>();
            AddChromium(list, "chrome", "Google Chrome", "chrome", "chrome.exe", Path.Combine(Local, @"Google\Chrome\User Data"), null, "chrome://extensions/?id=");
            AddChromium(list, "edge", "Microsoft Edge", "msedge", "msedge.exe", Path.Combine(Local, @"Microsoft\Edge\User Data"), null, "edge://extensions/?id=");
            AddChromium(list, "yandex", L.T("Яндекс Браузер", "Yandex Browser"), "browser", "browser.exe", Path.Combine(Local, @"Yandex\YandexBrowser\User Data"), null, "browser://extensions/?id=");
            AddChromium(list, "brave", "Brave", "brave", "brave.exe", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), null, "brave://extensions/?id=");
            AddChromium(list, "vivaldi", "Vivaldi", "vivaldi", "vivaldi.exe", Path.Combine(Local, @"Vivaldi\User Data"), null, "vivaldi://extensions/?id=");
            AddChromium(list, "opera", "Opera", "opera", "opera.exe", Path.Combine(Roaming, @"Opera Software\Opera Stable"), Path.Combine(Local, @"Opera Software\Opera Stable"), "opera://extensions/?id=");
            AddChromium(list, "operagx", "Opera GX", "opera", "opera.exe", Path.Combine(Roaming, @"Opera Software\Opera GX Stable"), Path.Combine(Local, @"Opera Software\Opera GX Stable"), "opera://extensions/?id=");
            AddFirefox(list);
            return list;
        }

        static void AddChromium(List<BrowserInfo> list, string id, string name, string process, string exe, string userData, string local, string extUrl)
        {
            if (!Directory.Exists(userData)) return;
            var b = new BrowserInfo
            {
                Id = id, Name = name, Process = process, ExeName = exe, Kind = BrowserKind.Chromium,
                UserData = userData, LocalData = local, ExtensionsUrl = extUrl,
            };
            var names = ProfileNames(Path.Combine(userData, "Local State"));
            if (local != null)
            {
                // Opera: the folder itself is the only profile.
                if (File.Exists(Path.Combine(userData, "Preferences")))
                    b.Profiles.Add(new BrowserProfile { Browser = b, Dir = userData, CacheDir = local, Name = name });
            }
            else
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.GetDirectories(userData); }
                catch { return; }
                foreach (string dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string leaf = Path.GetFileName(dir);
                    if (leaf == "System Profile" || leaf == "Guest Profile" || !File.Exists(Path.Combine(dir, "Preferences"))) continue;
                    string pname;
                    if (!names.TryGetValue(leaf, out pname) || string.IsNullOrEmpty(pname)) pname = leaf;
                    b.Profiles.Add(new BrowserProfile { Browser = b, Dir = dir, CacheDir = dir, Name = pname });
                }
            }
            if (b.Profiles.Count > 0) list.Add(b);
        }

        /// <summary>Profile folder → display name, from "Local State" (profile.info_cache).</summary>
        static Dictionary<string, string> ProfileNames(string localState)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(localState)) return map;
                var root = Json(File.ReadAllText(localState)) as Dictionary<string, object>;
                var profile = root != null && root.ContainsKey("profile") ? root["profile"] as Dictionary<string, object> : null;
                var cache = profile != null && profile.ContainsKey("info_cache") ? profile["info_cache"] as Dictionary<string, object> : null;
                if (cache == null) return map;
                foreach (var kv in cache)
                {
                    var info = kv.Value as Dictionary<string, object>;
                    if (info != null && info.ContainsKey("name")) map[kv.Key] = info["name"] as string;
                }
            }
            catch { }
            return map;
        }

        static void AddFirefox(List<BrowserInfo> list)
        {
            string root = Path.Combine(Roaming, @"Mozilla\Firefox");
            string ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) return;
            var b = new BrowserInfo
            {
                Id = "firefox", Name = "Mozilla Firefox", Process = "firefox", ExeName = "firefox.exe",
                Kind = BrowserKind.Firefox, UserData = root, LocalData = Path.Combine(Local, @"Mozilla\Firefox"), ExtensionsUrl = "about:addons",
            };
            string name = null, path = null;
            bool relative = true, inProfile = false;
            Action flush = () =>
            {
                if (inProfile && path != null)
                {
                    string rel = path.Replace('/', '\\');
                    string dir = relative ? Path.Combine(root, rel) : rel;
                    string cache = relative ? Path.Combine(b.LocalData, rel) : dir;
                    if (Directory.Exists(dir))
                        b.Profiles.Add(new BrowserProfile { Browser = b, Dir = dir, CacheDir = cache, Name = name ?? Path.GetFileName(dir) });
                }
                name = path = null;
                relative = true;
            };
            try
            {
                foreach (string raw in File.ReadAllLines(ini))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("["))
                    {
                        flush();
                        inProfile = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "Name") name = v;
                    else if (k == "Path") path = v;
                    else if (k == "IsRelative") relative = v != "0";
                }
                flush();
            }
            catch { }
            if (b.Profiles.Count > 0) list.Add(b);
        }

        /// <summary>Size of the data of one kind in a profile.</summary>
        public static long Measure(BrowserProfile p, BrowserData what)
        {
            long total = 0;
            foreach (string path in p.Paths(what))
            {
                try
                {
                    if (File.Exists(path)) total += new FileInfo(path).Length;
                    else if (Directory.Exists(path)) total += FileOps.DirSize(path);
                }
                catch { }
            }
            return total;
        }

        /// <summary>Deletes data of one kind; returns the bytes freed. The browser should be closed.</summary>
        public static long Clean(BrowserProfile p, BrowserData what, ref int failed)
        {
            long freed = 0;
            foreach (string path in p.Paths(what))
            {
                if (!FileOps.IsUnder(path, p.Dir) && !FileOps.IsUnder(path, p.CacheDir)) continue;
                var files = new List<FileEntry>();
                FileOps.Collect(path, null, true, 0, files);
                string rootFinal = FileOps.RootFinal(path);
                foreach (var f in files)
                {
                    if (FileOps.DeleteFile(f.Path, rootFinal)) freed += f.Size;
                    else failed++;
                }
                if (Directory.Exists(path)) FileOps.RemoveEmptyDirs(path, 0);
            }
            return freed;
        }

        /// <summary>Asks the browser to close its windows, then ends whatever is left.</summary>
        public static void Close(BrowserInfo b)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(b.Process); }
            catch { return; }
            foreach (var p in procs)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000 && b.IsRunning) System.Threading.Thread.Sleep(150);
            foreach (var p in Process.GetProcessesByName(b.Process))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }

        // ---------- extensions ----------

        public static List<ExtensionInfo> Extensions(BrowserProfile p)
        {
            var list = new List<ExtensionInfo>();
            try
            {
                if (p.Browser.Kind == BrowserKind.Chromium) ChromiumExtensions(p, list);
                else FirefoxExtensions(p, list);
            }
            catch { }
            return list;
        }

        static void ChromiumExtensions(BrowserProfile p, List<ExtensionInfo> list)
        {
            string root = Path.Combine(p.Dir, "Extensions");
            if (!Directory.Exists(root)) return;
            foreach (string extDir in Directory.GetDirectories(root))
            {
                string id = Path.GetFileName(extDir);
                if (id == "Temp") continue;
                string verDir = Directory.GetDirectories(extDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (verDir == null) continue;
                string manifestPath = Path.Combine(verDir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                var m = Json(File.ReadAllText(manifestPath)) as Dictionary<string, object>;
                if (m == null) continue;
                string name = Str(m, "name");
                if (name.StartsWith("__MSG_")) name = Localize(verDir, Str(m, "default_locale"), name);
                if (string.IsNullOrEmpty(name)) name = id;
                list.Add(new ExtensionInfo
                {
                    Profile = p, Id = id, Name = name, Version = Str(m, "version"), Dir = extDir, Size = FileOps.DirSize(extDir),
                });
            }
        }

        static string Localize(string dir, string defaultLocale, string msg)
        {
            string key = msg.Substring(6).TrimEnd('_');
            var locales = new List<string>();
            locales.Add(L.En ? "en" : "ru");
            if (!string.IsNullOrEmpty(defaultLocale)) locales.Add(defaultLocale);
            locales.Add("en");
            locales.Add("en_US");
            foreach (string loc in locales)
            {
                string f = Path.Combine(dir, "_locales", loc, "messages.json");
                if (!File.Exists(f)) continue;
                try
                {
                    var messages = Json(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (messages == null) continue;
                    foreach (var kv in messages)
                    {
                        if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                        var entry = kv.Value as Dictionary<string, object>;
                        if (entry != null) return Str(entry, "message");
                    }
                }
                catch { }
            }
            return "";
        }

        static void FirefoxExtensions(BrowserProfile p, List<ExtensionInfo> list)
        {
            string json = Path.Combine(p.Dir, "extensions.json");
            if (!File.Exists(json)) return;
            var root = Json(File.ReadAllText(json)) as Dictionary<string, object>;
            var addons = root != null && root.ContainsKey("addons") ? root["addons"] as ArrayList : null;
            if (addons == null) return;
            foreach (var o in addons)
            {
                var a = o as Dictionary<string, object>;
                if (a == null || Str(a, "type") != "extension" || Str(a, "location") != "app-profile") continue;
                var loc = a.ContainsKey("defaultLocale") ? a["defaultLocale"] as Dictionary<string, object> : null;
                string id = Str(a, "id");
                string xpi = Path.Combine(p.Dir, "extensions", id + ".xpi");
                long size = 0;
                try { if (File.Exists(xpi)) size = new FileInfo(xpi).Length; } catch { }
                list.Add(new ExtensionInfo
                {
                    Profile = p, Id = id, Name = loc != null ? Str(loc, "name") : id, Version = Str(a, "version"), Dir = xpi, Size = size,
                });
            }
        }

        public static void OpenExtensionPage(ExtensionInfo e)
        {
            var b = e.Profile.Browser;
            string exe = b.FindExe();
            if (exe == null) throw new FileNotFoundException(L.T("Не найден исполняемый файл браузера ", "Browser executable not found: ") + b.ExeName);
            string profileDir = Path.GetFileName(e.Profile.Dir);
            bool idOk = System.Text.RegularExpressions.Regex.IsMatch(e.Id, "^[a-p]{32}$");
            string url = b.Kind == BrowserKind.Firefox || !idOk ? b.ExtensionsUrl.Replace("?id=", "").TrimEnd('/') : b.ExtensionsUrl + e.Id;
            string args = url;
            if (b.Kind == BrowserKind.Chromium && b.LocalData == null && profileDir.IndexOf('"') < 0)
                args = "--profile-directory=\"" + profileDir + "\" " + url;
            // The browser must not inherit this app's administrator rights.
            Secure.StartAsUser(exe, args, false);
        }

        static object Json(string text)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            return js.DeserializeObject(text);
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }
    }
}
