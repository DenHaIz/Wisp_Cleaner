using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace Wisp
{
    enum AppKind { Win32, Store }

    class InstalledApp : IRowInfo, ICheckRow, ISortable
    {
        public IComparable SortKey(int column)
        {
            if (column == 2) return Size;
            if (column == 3) return InstallDate ?? DateTime.MinValue;
            return null;
        }

        public AppKind Kind;
        public string Name = "", Publisher = "", Version = "";
        public long Size = -1;
        public DateTime? InstallDate;
        public string Icon;              // key into the icon cache
        public string InstallLocation = "";
        public bool Checked { get; set; }
        public bool Checkable { get { return true; } }
        public string StatusText = "";
        public bool Failed;

        // Win32
        public RegistryHive Hive;
        public RegistryView View;
        public string KeyPath = "", KeyName = "";
        public string UninstallString = "", QuietUninstallString = "";
        public bool IsMsi;

        // Store
        public string PackageFullName = "", PackageFamilyName = "";

        public RowKind RowKind { get { return Failed ? RowKind.Danger : RowKind.Normal; } }
        public string IconPath { get { return Icon; } }
        public int Dot { get { return 0; } }

        public string KindText { get { return Kind == AppKind.Store ? "Microsoft Store" : L.T("Классическая", "Desktop"); } }
        public bool CanQuiet { get { return Kind == AppKind.Store || IsMsi || QuietUninstallString.Length > 0; } }

        public bool StillInstalled()
        {
            if (Kind == AppKind.Store) return StoreApps.IsInstalled(PackageFullName);
            try
            {
                using (var b = RegistryKey.OpenBaseKey(Hive, View))
                using (var k = b.OpenSubKey(KeyPath + "\\" + KeyName))
                    return k != null;
            }
            catch { return true; }
        }
    }

    /// <summary>Desktop programs from the Uninstall registry keys (both views, machine and user).</summary>
    static class Win32Apps
    {
        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        static readonly Regex KbName = new Regex(@"^KB\d+", RegexOptions.IgnoreCase);
        static readonly Regex Guid = new Regex(@"^\{[0-9A-F\-]{36}\}$", RegexOptions.IgnoreCase);

        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new[]
            {
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32),
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64),
            };
            foreach (var src in sources)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(src.Item1, src.Item2))
                    using (var root = b.OpenSubKey(UninstallPath))
                    {
                        if (root == null) continue;
                        foreach (string name in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = root.OpenSubKey(name))
                                {
                                    var app = Read(k, name, src.Item1, src.Item2);
                                    if (app != null && seen.Add(app.Name + "|" + app.Version)) list.Add(app);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        static string Str(RegistryKey k, string name)
        {
            var v = k.GetValue(name);
            return v != null ? v.ToString().Trim() : "";
        }

        static InstalledApp Read(RegistryKey k, string keyName, RegistryHive hive, RegistryView view)
        {
            if (k == null) return null;
            string name = Str(k, "DisplayName");
            if (name.Length == 0) return null;
            if (Str(k, "SystemComponent") == "1" || Str(k, "ParentKeyName").Length > 0) return null;
            string release = Str(k, "ReleaseType");
            if (release.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || release.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (KbName.IsMatch(keyName)) return null;
            string uninstall = Str(k, "UninstallString"), quiet = Str(k, "QuietUninstallString");
            bool msi = Str(k, "WindowsInstaller") == "1" && Guid.IsMatch(keyName);
            if (uninstall.Length == 0 && quiet.Length == 0 && !msi) return null;

            var app = new InstalledApp
            {
                Kind = AppKind.Win32, Name = name, Publisher = Str(k, "Publisher"), Version = Str(k, "DisplayVersion"),
                Hive = hive, View = view, KeyPath = UninstallPath, KeyName = keyName,
                UninstallString = uninstall, QuietUninstallString = quiet, IsMsi = msi,
                InstallLocation = Str(k, "InstallLocation").Trim('"'),
            };
            object est = k.GetValue("EstimatedSize");
            if (est is int && (int)est > 0) app.Size = (int)est * 1024L;
            DateTime d;
            if (DateTime.TryParseExact(Str(k, "InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) app.InstallDate = d;
            string icon = Str(k, "DisplayIcon");
            if (icon.Length == 0)
            {
                string exe = SplitCommand(uninstall.Length > 0 ? uninstall : quiet).Item1;
                if (exe.Length > 0 && !Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) icon = exe;
            }
            app.Icon = icon.Length > 0 ? icon : null;
            return app;
        }

        /// <summary>Icon for a DisplayIcon value ("path,index", quoted or not).</summary>
        public static System.Drawing.Bitmap LoadIcon(string displayIcon)
        {
            string s = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
            int index = 0;
            int comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s.Substring(comma + 1).Trim(), out index)) s = s.Substring(0, comma);
            s = s.Trim().Trim('"');
            if (!File.Exists(s)) return null;
            if (s.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var ico = new System.Drawing.Icon(s, 32, 32)) return ico.ToBitmap();
                }
                catch { }
            }
            return Native.GetIcon(s, index) ?? Native.GetSmallIcon(s);
        }

        /// <summary>Splits a command line into the executable and its arguments (paths with spaces, quotes, env vars).</summary>
        public static Tuple<string, string> SplitCommand(string command)
        {
            string s = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
            if (s.Length == 0) return Tuple.Create("", "");
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end < 0) return Tuple.Create(s.Trim('"'), "");
                return Tuple.Create(s.Substring(1, end - 1), s.Substring(end + 1).Trim());
            }
            int idx = -1;
            while (true)
            {
                idx = s.IndexOf(' ', idx + 1);
                string cand = idx < 0 ? s : s.Substring(0, idx);
                if (File.Exists(cand)) return Tuple.Create(cand, idx < 0 ? "" : s.Substring(idx + 1).Trim());
                if (cand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Tuple.Create(cand, idx < 0 ? "" : s.Substring(idx + 1).Trim());
                if (idx < 0) break;
            }
            int sp = s.IndexOf(' ');
            return sp > 0 ? Tuple.Create(s.Substring(0, sp), s.Substring(sp + 1).Trim()) : Tuple.Create(s, "");
        }

        /// <summary>
        /// Uninstalls one program and waits for it to finish. Quiet when the program supports it
        /// (MSI, QuietUninstallString), otherwise its own uninstaller window is shown.
        /// Returns true when the program is gone afterwards.
        /// </summary>
        public static bool Uninstall(InstalledApp app, Func<bool> cancel)
        {
            string exe, args;
            bool interactive = false;
            if (app.IsMsi)
            {
                exe = "msiexec.exe";
                args = "/x " + app.KeyName + " /qn /norestart";
            }
            else if (app.QuietUninstallString.Length > 0)
            {
                var c = SplitCommand(app.QuietUninstallString);
                exe = c.Item1;
                args = c.Item2;
            }
            else
            {
                var c = SplitCommand(app.UninstallString);
                exe = c.Item1;
                args = c.Item2;
                interactive = true;
                // "MsiExec /I{GUID}" opens the repair/remove wizard; /X goes straight to removal.
                if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                    args = Regex.Replace(args, @"/I\s*\{", "/X{", RegexOptions.IgnoreCase);
            }

            if (app.Hive == RegistryHive.CurrentUser)
            {
                // A per-user entry can be written without administrator rights: never run it elevated.
                Secure.WaitAndClose(Secure.StartAsUser(exe, args, true));
            }
            else
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = !interactive };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }
            }
            // Many uninstallers (NSIS, Inno) copy themselves to %TEMP% and exit at once: wait for the
            // entry to disappear while such helper processes run, or a few seconds for quiet removals.
            var sw = Stopwatch.StartNew();
            while (app.StillInstalled())
            {
                if (cancel != null && cancel()) break;
                bool helpers = UninstallerHelpersRunning();
                if (!helpers && sw.ElapsedMilliseconds > (interactive ? 8000 : 15000)) break;
                if (sw.Elapsed > TimeSpan.FromMinutes(30)) break;
                System.Threading.Thread.Sleep(500);
            }
            return !app.StillInstalled();
        }

        static bool UninstallerHelpersRunning()
        {
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    string n = p.ProcessName;
                    if (n.StartsWith("Au_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Un_", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || n.StartsWith("uninst", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("msiexec", StringComparison.OrdinalIgnoreCase) && p.SessionId != 0 ||
                        n.Equals("_iu14D2N", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>Microsoft Store (packaged) apps of the current user, via PowerShell's Appx module.</summary>
    static class StoreApps
    {
        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | " +
                "ForEach-Object { $_.Name + \"`t\" + $_.PackageFullName + \"`t\" + $_.PackageFamilyName + \"`t\" + $_.InstallLocation + \"`t\" + $_.Version }";
            string output = PowerShell(script, 60000);
            foreach (string line in output.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 5 || parts[3].Length == 0) continue;
                var app = new InstalledApp
                {
                    Kind = AppKind.Store, Name = parts[0], PackageFullName = parts[1], PackageFamilyName = parts[2],
                    InstallLocation = parts[3], Version = parts[4],
                };
                if (ReadManifest(app)) list.Add(app);
            }
            return list;
        }

        /// <summary>Display name, publisher and logo from AppxManifest.xml; false for packages without a Start menu entry.</summary>
        static bool ReadManifest(InstalledApp app)
        {
            string path = Path.Combine(app.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(path)) return false;
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                string display = null, publisher = null, logo = null;
                bool listed = false;
                foreach (XmlNode n in doc.GetElementsByTagName("*"))
                {
                    if (n.LocalName == "DisplayName" && n.ParentNode != null && n.ParentNode.LocalName == "Properties") display = n.InnerText;
                    else if (n.LocalName == "PublisherDisplayName") publisher = n.InnerText;
                    else if (n.LocalName == "Logo" && n.ParentNode != null && n.ParentNode.LocalName == "Properties") logo = n.InnerText;
                    else if (n.LocalName == "VisualElements")
                    {
                        var attr = n.Attributes != null ? n.Attributes["AppListEntry"] : null;
                        if (attr == null || !attr.Value.Equals("none", StringComparison.OrdinalIgnoreCase)) listed = true;
                    }
                }
                if (!listed) return false;
                app.Name = Resolve(app, display) ?? app.Name;
                app.Publisher = Resolve(app, publisher) ?? "";
                if (logo != null) app.Icon = FindLogo(Path.Combine(app.InstallLocation, logo));
                return true;
            }
            catch { return false; }
        }

        static string Resolve(InstalledApp app, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
            string key = value.Substring("ms-resource:".Length);
            var candidates = new List<string>();
            if (key.StartsWith("//")) candidates.Add("ms-resource:" + key);
            else if (key.StartsWith("/")) candidates.Add("ms-resource://" + app.Name + key);
            else
            {
                candidates.Add("ms-resource://" + app.Name + "/resources/" + key);
                candidates.Add("ms-resource://" + app.Name + "/" + key);
            }
            foreach (string c in candidates)
            {
                string r = Native.LoadIndirectString("@{" + app.PackageFullName + "?" + c + "}");
                if (!string.IsNullOrEmpty(r)) return r;
            }
            return null;
        }

        /// <summary>Logo files carry scale qualifiers (StoreLogo.scale-100.png): pick the best existing one.</summary>
        static string FindLogo(string declared)
        {
            if (File.Exists(declared)) return declared;
            string dir = Path.GetDirectoryName(declared);
            if (!Directory.Exists(dir)) return null;
            string stem = Path.GetFileNameWithoutExtension(declared), ext = Path.GetExtension(declared);
            string[] files;
            try { files = Directory.GetFiles(dir, stem + "*" + ext); }
            catch { return null; }
            foreach (string pref in new[] { "targetsize-32", "scale-200", "scale-100", "targetsize-48", "targetsize-24" })
            {
                var f = files.FirstOrDefault(x => x.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0 && x.IndexOf("contrast", StringComparison.OrdinalIgnoreCase) < 0);
                if (f != null) return f;
            }
            return files.FirstOrDefault();
        }

        public static System.Drawing.Bitmap LoadLogo(string path)
        {
            try
            {
                using (var src = System.Drawing.Image.FromFile(path))
                {
                    var bmp = new System.Drawing.Bitmap(32, 32);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, 32, 32);
                    }
                    return bmp;
                }
            }
            catch { return null; }
        }

        public static bool IsInstalled(string fullName)
        {
            string output = PowerShell("$ErrorActionPreference='SilentlyContinue'; if (Get-AppxPackage | Where-Object { $_.PackageFullName -eq '" +
                fullName.Replace("'", "''") + "' }) { 'yes' }", 30000);
            return output.Trim() == "yes";
        }

        public static bool Uninstall(InstalledApp app)
        {
            PowerShell("Remove-AppxPackage -Package '" + app.PackageFullName.Replace("'", "''") + "'", 5 * 60000);
            return !IsInstalled(app.PackageFullName);
        }

        static string PowerShell(string script, int timeoutMs)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                return output;
            }
        }
    }

    /// <summary>What a removed program may have left behind: its folders and registry keys.</summary>
    class Leftover : IRowInfo, ICheckRow, ISortable
    {
        public IComparable SortKey(int column) { return column == 2 ? (IComparable)Size : null; }

        public string Path = "";
        public bool IsRegistry;
        public RegistryHive Hive;
        public RegistryView View;
        public string SubKey = "";
        public long Size;
        public string App = "";
        public bool Checked { get; set; }
        public bool Checkable { get { return true; } }
        public RowKind RowKind { get { return RowKind.Normal; } }
        public string IconPath { get { return null; } }
        public int Dot { get { return 0; } }
    }

    static class Leftovers
    {
        static readonly HashSet<string> generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Microsoft Corporation", "Windows", "Common Files", "Google", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD",
            "Mozilla", "Package Cache", "Temp", "Programs", "Packages", "Apple", "Adobe", "Oracle", "Java", "Classes", "Policies",
            "Wow6432Node", "Clients", "RegisteredApplications", "Software", "Microsoft Office", "Windows Kits", "dotnet", "Realtek",
        };

        static string CleanName(string name)
        {
            string s = Regex.Replace(name ?? "", @"\s*\([^)]*\)", "");
            s = Regex.Replace(s, @"\s+v?\d+([\.\-]\d+)*.*$", "");
            s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit)$", "", RegexOptions.IgnoreCase);
            return s.Trim().TrimEnd('-', ',', '.').Trim();
        }

        static string CleanPublisher(string pub)
        {
            string s = Regex.Replace(pub ?? "", @",?\s+(Inc\.?|LLC|Ltd\.?|GmbH|Corporation|Corp\.?|Co\.?|Limited|s\.r\.o\.|SA|AG)$", "", RegexOptions.IgnoreCase);
            return s.Trim().TrimEnd(',', '.').Trim();
        }

        static bool Usable(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Trim('.', ' ').Length >= 3 && !name.Contains("..") && !generic.Contains(name) &&
                   name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>True when a folder name shares a word (3+ letters) with the program's name or publisher.</summary>
        static bool LooksLike(string folder, string name, string publisher)
        {
            var words = Regex.Split((name ?? "") + " " + (publisher ?? ""), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3);
            return words.Any(w => folder.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Candidates for one removed program. otherLocations are install folders of programs that are
        /// still installed: anything containing one of them is never offered.
        /// </summary>
        public static List<Leftover> Find(InstalledApp app, List<string> otherLocations)
        {
            var found = new List<Leftover>();
            if (app.Kind != AppKind.Win32) return found;
            var names = new List<string>();
            string clean = CleanName(app.Name);
            if (Usable(clean)) names.Add(clean);
            string loc = app.InstallLocation.TrimEnd('\\');
            if (loc.Length > 0)
            {
                string leaf = System.IO.Path.GetFileName(loc);
                if (Usable(leaf) && !names.Contains(leaf, StringComparer.OrdinalIgnoreCase)) names.Add(leaf);
            }
            string pub = CleanPublisher(app.Publisher);
            if (!Usable(pub)) pub = "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var others = otherLocations.Where(o => o.Length > 3).Select(FileOps.Norm).ToList();

            Action<string> addDir = dir =>
            {
                try
                {
                    if (!Directory.Exists(dir) || !FileOps.IsSafeRoot(dir) || !seen.Add(FileOps.Norm(dir))) return;
                    if (!LooksLike(System.IO.Path.GetFileName(dir.TrimEnd('\\')), app.Name, app.Publisher)) return;
                    string n = FileOps.Norm(dir);
                    if (others.Any(o => o.StartsWith(n, StringComparison.OrdinalIgnoreCase))) return;
                    found.Add(new Leftover { Path = dir, Size = FileOps.DirSize(dir), App = app.Name, Checked = true });
                }
                catch { }
            };
            if (loc.Length > 0) addDir(loc);

            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"),
            };
            foreach (string b in bases.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string n in names)
                {
                    addDir(System.IO.Path.Combine(b, n));
                    if (pub.Length > 0) addDir(System.IO.Path.Combine(b, pub, n));
                }

            var keys = new List<Tuple<RegistryHive, RegistryView, string>>();
            foreach (string n in names)
            {
                foreach (var hv in new[] { Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64), Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64), Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32) })
                {
                    keys.Add(Tuple.Create(hv.Item1, hv.Item2, @"SOFTWARE\" + n));
                    if (pub.Length > 0) keys.Add(Tuple.Create(hv.Item1, hv.Item2, @"SOFTWARE\" + pub + @"\" + n));
                }
            }
            foreach (var k in keys)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(k.Item1, k.Item2))
                    using (var key = b.OpenSubKey(k.Item3))
                    {
                        if (key == null) continue;
                        string display = (k.Item1 == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") +
                            (k.Item2 == RegistryView.Registry32 && k.Item1 == RegistryHive.LocalMachine ? k.Item3.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\") : k.Item3);
                        if (!seen.Add(display)) continue;
                        found.Add(new Leftover { Path = display, IsRegistry = true, Hive = k.Item1, View = k.Item2, SubKey = k.Item3, App = app.Name, Checked = true });
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>Folders go to the Recycle Bin; registry keys are exported to the backup folder first.</summary>
        public static int Remove(List<Leftover> items, List<string> errors)
        {
            int done = 0;
            var dirs = items.Where(i => !i.IsRegistry).ToList();
            foreach (var d in dirs)
            {
                if (!FileOps.IsSafeRoot(d.Path)) continue;
                if (Native.MoveToRecycleBin(new[] { d.Path }) && !Directory.Exists(d.Path)) done++;
                else errors.Add(d.Path);
            }
            foreach (var r in items.Where(i => i.IsRegistry))
            {
                try
                {
                    Directory.CreateDirectory(Settings.BackupDir);
                    string file = System.IO.Path.Combine(Settings.BackupDir,
                        Regex.Replace(r.Path, @"[\\/:*?""<>|]", "_") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".reg");
                    string native = (r.Hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + r.SubKey;
                    var psi = new ProcessStartInfo("reg.exe", "export \"" + native + "\" \"" + file + "\" /y" +
                        (r.View == RegistryView.Registry32 ? " /reg:32" : " /reg:64"))
                    { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) p.WaitForExit(15000);
                    using (var b = RegistryKey.OpenBaseKey(r.Hive, r.View))
                        b.DeleteSubKeyTree(r.SubKey, false);
                    done++;
                }
                catch (Exception ex) { errors.Add(r.Path + ": " + ex.Message); }
            }
            return done;
        }
    }
}
