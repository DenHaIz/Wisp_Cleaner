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
        public string UserSid;           // per-user entry: whose (null for programs installed for everyone)
        public bool Broken;              // the uninstaller is gone: only forced removal helps

        // Store
        public string PackageFullName = "", PackageFamilyName = "";

        public RowKind RowKind { get { return Failed ? RowKind.Danger : RowKind.Normal; } }
        public string IconPath { get { return Icon; } }
        public int Dot { get { return Broken && !Failed ? 3 : 0; } }

        public UserProfile User { get { return UserProfiles.BySid(UserSid); } }

        public string KindText
        {
            get
            {
                if (Kind == AppKind.Store) return "Microsoft Store";
                var u = User;
                return L.T("Классическая", "Desktop") + (u != null && UserProfiles.Many ? " · " + u.Name : "");
            }
        }

        public bool CanQuiet { get { return Kind == AppKind.Store || IsMsi || QuietUninstallString.Length > 0; } }

        /// <summary>
        /// Whether the program's own uninstaller can be run: it exists, and a per-user one belongs to the
        /// signed-in user or to this app's account (it cannot be started in another user's name).
        /// </summary>
        public bool CanRunUninstaller
        {
            get
            {
                if (Kind == AppKind.Store) return true;
                if (Broken) return false;
                return UserSid == null || string.Equals(UserSid, Secure.ShellSid, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(UserSid, Secure.SelfSid, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>The uninstall entry as shown in regedit (WOW6432Node for 32-bit entries).</summary>
        public string RegistryPath { get { return Reg.Display(Hive, View, KeyPath + "\\" + KeyName); } }

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

        /// <summary>
        /// Programs for everyone (both registry views), this account's own, and — as administrator —
        /// those of the other users who are signed in (their registry is loaded under HKEY_USERS).
        /// </summary>
        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<Tuple<RegistryHive, RegistryView, string, string>>
            {
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64, "", (string)null),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32, "", (string)null),
            };
            foreach (var u in Reg.UserRoots())
                sources.Add(Tuple.Create(u.Hive, RegistryView.Registry64, u.Prefix, u.Sid));
            foreach (var src in sources)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(src.Item1, src.Item2))
                    using (var root = b.OpenSubKey(src.Item3 + UninstallPath))
                    {
                        if (root == null) continue;
                        foreach (string name in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = root.OpenSubKey(name))
                                {
                                    var app = Read(k, name, src.Item1, src.Item2, src.Item4);
                                    if (app == null) continue;
                                    app.KeyPath = src.Item3 + UninstallPath;
                                    app.UserSid = src.Item4;
                                    // The same program in both views or for everyone and this account is listed once;
                                    // other users' own copies are listed separately.
                                    bool own = src.Item4 == null || string.Equals(src.Item4, Secure.SelfSid, StringComparison.OrdinalIgnoreCase);
                                    if (seen.Add(app.Name + "|" + app.Version + (own ? "" : "|" + src.Item4))) list.Add(app);
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

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MsiQueryProductState(string product);

        /// <summary>
        /// True when the uninstaller surely cannot run: its file is gone from a local disk, or Windows
        /// Installer no longer knows the product.
        /// </summary>
        static bool IsBroken(InstalledApp app)
        {
            if (app.IsMsi)
            {
                if (app.Hive != RegistryHive.LocalMachine) return false;
                try { return MsiQueryProductState(app.KeyName) == -1; } // INSTALLSTATE_UNKNOWN
                catch { return false; }
            }
            string cmd = app.QuietUninstallString.Length > 0 ? app.QuietUninstallString : app.UninstallString;
            return PathCheck.IsMissing(cmd, app.View == RegistryView.Registry32);   // values are already expanded for their user
        }

        /// <summary>A value with %VARS% expanded for the user whose registry it is (sid null: the machine's).</summary>
        static string Str(RegistryKey k, string name, string sid)
        {
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v == null) return "";
            return v is string ? UserProfiles.Expand(((string)v).Trim(), sid).Trim() : v.ToString().Trim();
        }

        static InstalledApp Read(RegistryKey k, string keyName, RegistryHive hive, RegistryView view, string sid)
        {
            if (k == null) return null;
            Func<RegistryKey, string, string> Str = (key, value) => Win32Apps.Str(key, value, sid);
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
                UninstallString = uninstall, QuietUninstallString = quiet, IsMsi = msi, UserSid = sid,
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
            app.Broken = IsBroken(app);
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
            if (!app.CanRunUninstaller) return false;
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

            if (app.UserSid != null && string.Equals(app.UserSid, Secure.ShellSid, StringComparison.OrdinalIgnoreCase))
            {
                // A per-user entry can be written without administrator rights: never run it elevated.
                Secure.WaitAndClose(Secure.StartAsUser(exe, args, true));
            }
            else if (app.UserSid != null)
            {
                // This app's own account, entered at the UAC prompt (not the signed-in user): its entry,
                // its rights. Programs of other users never get here (CanRunUninstaller).
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = !interactive }))
                {
                    if (p != null) p.WaitForExit();
                }
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

    /// <summary>
    /// What stays installed, for removals to keep away from: every folder another program lives in (its
    /// InstallLocation and the folders of its uninstaller and icon) and the names its settings go under.
    /// </summary>
    class Keep
    {
        public readonly List<string> Folders = new List<string>();   // normalized, ending with "\"
        public readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Keep From(IEnumerable<InstalledApp> staying)
        {
            var k = new Keep();
            string win = FileOps.Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            foreach (var a in staying)
            {
                k.Names.Add(Leftovers.CleanName(a.Name));
                var dirs = new List<string>();
                if (a.InstallLocation.Length > 0) dirs.Add(a.InstallLocation);
                foreach (string cmd in new[] { a.UninstallString, a.QuietUninstallString, a.Icon })
                {
                    string exe = PathCheck.ExePath(cmd);
                    if (string.IsNullOrEmpty(exe)) continue;
                    try { dirs.Add(Path.GetDirectoryName(exe)); } catch { }
                }
                foreach (string d in dirs)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(d) || d.IndexOf('%') >= 0 || d.Length < 4) continue;
                        string n = FileOps.Norm(d.Trim('"'));
                        // Program Files itself, a profile or Windows says nothing about one program.
                        if (n.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !FileOps.IsSafeRoot(n)) continue;
                        if (!k.Folders.Contains(n, StringComparer.OrdinalIgnoreCase)) k.Folders.Add(n);
                    }
                    catch { }
                }
            }
            return k;
        }

        /// <summary>Another program lives in this folder, or the folder lies inside another program's.</summary>
        public bool Touches(string folder)
        {
            string n = FileOps.Norm(folder);
            return Folders.Any(o => o.StartsWith(n, StringComparison.OrdinalIgnoreCase) || n.StartsWith(o, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Another program lives inside this folder (it cannot be offered at all).</summary>
        public bool Contains(string folder)
        {
            string n = FileOps.Norm(folder);
            return Folders.Any(o => o.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>What a removed program may have left behind: its folders and registry keys.</summary>
    static class Leftovers
    {
        static readonly HashSet<string> generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Microsoft Corporation", "Windows", "Common Files", "Google", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD",
            "Mozilla", "Package Cache", "Temp", "Programs", "Packages", "Apple", "Adobe", "Oracle", "Java", "Classes", "Policies",
            "Wow6432Node", "Clients", "RegisteredApplications", "Software", "Microsoft Office", "Windows Kits", "dotnet", "Realtek",
        };

        /// <summary>Folders shared by many programs: never taken as one program's folder.</summary>
        static readonly HashSet<string> shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "InstallShield Installation Information", "Package Cache", "Common Files", "Program Files", "Program Files (x86)",
            "ProgramData", "Users", "Windows", "Microsoft", "Installer", "Downloaded Installations", "Uninstall Information",
            "Programs", "Apps", "Tools", "Games", "Software", "Setup", "Temp", "AppData", "Local", "Roaming", "LocalLow",
        };

        public static string CleanName(string name)
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

        static IEnumerable<string> Words(string s)
        {
            return Regex.Split(s ?? "", @"[^\p{L}\p{N}]+").Where(w => w.Length > 0);
        }

        /// <summary>True when a folder name shares a word (3+ letters) with the program's name or publisher (a loose match).</summary>
        public static bool LooksLike(string folder, string name, string publisher)
        {
            if (string.IsNullOrEmpty(folder) || generic.Contains(folder) || shared.Contains(folder)) return false;
            var words = Words((name ?? "") + " " + (publisher ?? "")).Where(w => w.Length >= 3 && !generic.Contains(w));
            return words.Any(w => folder.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// A strict match, enough to tick a folder: the folder name is the program's name, or shares a whole
        /// word of 4+ letters with it ("WispFakeApp" ~ "WispFakeApp Test 1.0"); never a shared folder.
        /// </summary>
        public static bool NamedAfter(string folder, string appName)
        {
            if (string.IsNullOrEmpty(folder) || generic.Contains(folder) || shared.Contains(folder)) return false;
            string clean = CleanName(appName);
            if (string.Equals(folder, clean, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folder.Replace(" ", ""), clean.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return true;
            var mine = new HashSet<string>(Words(clean).Where(w => w.Length >= 4 && !generic.Contains(w)), StringComparer.OrdinalIgnoreCase);
            return Words(folder).Any(w => mine.Contains(w));
        }

        public static bool IsShared(string folder)
        {
            return shared.Contains(folder ?? "") || generic.Contains(folder ?? "");
        }

        /// <summary>
        /// Candidates for one removed program. Nothing containing a folder of a program that stays is offered;
        /// a folder or key under a name another installed program also uses ("Python 3.11" and "Python 3.12"
        /// both clean to "Python") is listed but not ticked. Data of other users is listed unticked; for a
        /// program installed for one user only, only that user's profile and registry are looked at.
        /// </summary>
        public static List<Leftover> Find(InstalledApp app, Keep keep)
        {
            var found = new List<Leftover>();
            if (app.Kind != AppKind.Win32) return found;
            var names = new List<string>();
            string clean = CleanName(app.Name);
            if (Usable(clean)) names.Add(clean);
            string loc = app.InstallLocation.TrimEnd('\\');
            if (loc.Length > 0 && loc.IndexOf('%') < 0)
            {
                string leaf = System.IO.Path.GetFileName(loc);
                if (Usable(leaf) && !IsShared(leaf) && !names.Contains(leaf, StringComparer.OrdinalIgnoreCase)) names.Add(leaf);
            }
            string pub = CleanPublisher(app.Publisher);
            if (!Usable(pub) || IsShared(pub)) pub = "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Whose data: the program's own user, or — for a program installed for everyone — the signed-in
            // user and this account ticked, everyone else's listed unticked.
            var owner = UserProfiles.BySid(app.UserSid);
            var profiles = app.UserSid != null ? (owner != null ? new List<UserProfile> { owner } : new List<UserProfile>()) : UserProfiles.All;
            Func<UserProfile, bool> mine = u => app.UserSid != null || u.IsShell || u.IsSelf;

            Action<string, string, bool, string> addDir = (dir, reason, tick, user) =>
            {
                try
                {
                    if (!Directory.Exists(dir) || !FileOps.IsSafeRoot(dir) || !seen.Add(FileOps.Norm(dir))) return;
                    if (!LooksLike(System.IO.Path.GetFileName(dir.TrimEnd('\\')), app.Name, app.Publisher)) return;
                    if (keep.Contains(dir)) return;
                    string leaf = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
                    bool sharedName = keep.Names.Contains(leaf);
                    found.Add(new Leftover
                    {
                        Kind = LeftoverKind.Folder, Path = dir, Size = FileOps.DirSize(dir), App = app.Name,
                        Reason = sharedName ? Leftover.R("Имя есть и у другой программы", "Another program uses this name")
                               : (user != null ? Leftover.R("Данные пользователя ", "Data of user ") + user : reason),
                        Checked = tick && !sharedName && !keep.Touches(dir),
                    });
                }
                catch { }
            };
            if (loc.Length > 0 && loc.IndexOf('%') < 0)
                addDir(loc, Leftover.R("Папка программы", "Program folder"), NamedAfter(System.IO.Path.GetFileName(loc), app.Name), null);

            // Program folders and shared data (for programs installed for everyone), then each profile's AppData.
            if (app.UserSid == null)
                foreach (string b in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                }.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (string n in names)
                    {
                        addDir(System.IO.Path.Combine(b, n), Leftover.R("Данные программы", "Program data"), true, null);
                        if (pub.Length > 0) addDir(System.IO.Path.Combine(b, pub, n), Leftover.R("Данные программы", "Program data"), true, null);
                    }
            foreach (var u in profiles)
                foreach (string b in new[] { u.Roaming, u.Local, System.IO.Path.Combine(u.Dir, @"AppData\LocalLow") })
                    foreach (string n in names)
                    {
                        string user = mine(u) ? null : u.Name;
                        addDir(System.IO.Path.Combine(b, n), Leftover.R("Данные программы", "Program data"), mine(u), user);
                        if (pub.Length > 0) addDir(System.IO.Path.Combine(b, pub, n), Leftover.R("Данные программы", "Program data"), mine(u), user);
                    }

            // Settings in the registry: for everyone (both views) and in the loaded user registries.
            var roots = new List<Tuple<RegistryHive, RegistryView, string, UserProfile, bool>>();
            if (app.UserSid == null)
            {
                roots.Add(Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64, "", (UserProfile)null, true));
                roots.Add(Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32, "", (UserProfile)null, true));
            }
            foreach (var r in Reg.UserRoots())
            {
                if (app.UserSid != null && !string.Equals(r.Sid, app.UserSid, StringComparison.OrdinalIgnoreCase)) continue;
                var u = UserProfiles.BySid(r.Sid);
                bool tick = u == null ? app.UserSid != null : mine(u);
                roots.Add(Tuple.Create(r.Hive, RegistryView.Registry64, r.Prefix, u, tick));
            }
            foreach (string n in names)
                foreach (var r in roots)
                {
                    bool tick = r.Item5 && !keep.Names.Contains(n);
                    string user = r.Item5 || r.Item4 == null ? null : r.Item4.Name;
                    AddKey(found, seen, app, r.Item1, r.Item2, r.Item3 + @"SOFTWARE\" + n, tick, user);
                    if (pub.Length > 0) AddKey(found, seen, app, r.Item1, r.Item2, r.Item3 + @"SOFTWARE\" + pub + @"\" + n, tick, user);
                }
            return found;
        }

        static void AddKey(List<Leftover> found, HashSet<string> seen, InstalledApp app, RegistryHive hive, RegistryView view, string sub, bool tick, string user)
        {
            try
            {
                using (var key = Reg.Open(hive, view, sub))
                {
                    if (key == null) return;
                    string display = Reg.Display(hive, view, sub);
                    if (!seen.Add(display)) return;
                    found.Add(new Leftover
                    {
                        Kind = LeftoverKind.RegKey, Path = display, Hive = hive, View = view, SubKey = sub, App = app.Name,
                        Reason = user != null ? Leftover.R("Настройки пользователя ", "Settings of user ") + user : Leftover.R("Настройки в реестре", "Registry settings"),
                        Checked = tick,
                    });
                }
            }
            catch { }
        }
    }
}
