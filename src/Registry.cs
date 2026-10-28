using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wisp
{
    /// <summary>A user's part of the registry: HKCU for this account, HKEY_USERS\SID for other signed-in users.</summary>
    class UserRoot
    {
        public RegistryHive Hive;
        public string Prefix = "";      // prepended to "Software\..." paths
        public string Sid;

        /// <summary>Where this user's Software\Classes lives (a separate hive under HKEY_USERS).</summary>
        public string ClassesPrefix { get { return Hive == RegistryHive.Users ? Sid + @"_Classes\" : @"Software\Classes\"; } }
    }

    static class Reg
    {
        /// <summary>
        /// This account's HKCU and — as administrator — the registry of every other user who is signed in
        /// (loaded under HKEY_USERS). Signed-out users' registry is a closed file and is not touched.
        /// </summary>
        public static List<UserRoot> UserRoots()
        {
            var list = new List<UserRoot> { new UserRoot { Hive = RegistryHive.CurrentUser, Sid = Secure.SelfSid } };
            if (!Secure.IsElevated) return list;
            try
            {
                foreach (string sid in Registry.Users.GetSubKeyNames())
                {
                    if (!sid.StartsWith("S-1-5-21-") && !sid.StartsWith("S-1-12-1-")) continue;
                    if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) || string.Equals(sid, Secure.SelfSid, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new UserRoot { Hive = RegistryHive.Users, Prefix = sid + @"\", Sid = sid });
                }
            }
            catch { }
            return list;
        }

        static string Short(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.LocalMachine: return "HKLM";
                case RegistryHive.CurrentUser: return "HKCU";
                case RegistryHive.Users: return "HKU";
                case RegistryHive.ClassesRoot: return "HKCR";
                default: return hive.ToString();
            }
        }

        static string Long(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.LocalMachine: return "HKEY_LOCAL_MACHINE";
                case RegistryHive.CurrentUser: return "HKEY_CURRENT_USER";
                case RegistryHive.Users: return "HKEY_USERS";
                case RegistryHive.ClassesRoot: return "HKEY_CLASSES_ROOT";
                default: return hive.ToString();
            }
        }

        /// <summary>The path a 32-bit view key really has (SOFTWARE\WOW6432Node\...).</summary>
        static string Real(RegistryHive hive, RegistryView view, string sub)
        {
            if (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine && sub.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase) &&
                !sub.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase))
                return @"SOFTWARE\WOW6432Node\" + sub.Substring(9);
            return sub;
        }

        /// <summary>"HKLM\SOFTWARE\WOW6432Node\..." — as the key is shown to the user.</summary>
        public static string Display(RegistryHive hive, RegistryView view, string sub)
        {
            return Short(hive) + "\\" + Real(hive, view, sub);
        }

        public static RegistryKey Open(RegistryHive hive, RegistryView view, string sub, bool writable = false)
        {
            using (var b = RegistryKey.OpenBaseKey(hive, view))
                return b.OpenSubKey(sub, writable);
        }

        /// <summary>
        /// A string value as stored: %VARS% are not expanded here, because they belong to the user whose
        /// registry it is (see <see cref="UserProfiles.Expand"/>), not to this app's account.
        /// </summary>
        public static string Str(RegistryKey k, string name)
        {
            try
            {
                var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return v is string ? ((string)v).Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>Saves a key with everything below it to a .reg file (reg.exe, so it can be imported back by double-click).</summary>
        public static bool Export(RegistryHive hive, RegistryView view, string sub, string file)
        {
            // A quote in a key name would break reg.exe's command line: such a key is never exported (nor deleted).
            if (sub.IndexOf('"') >= 0 || file.IndexOf('"') >= 0) return false;
            var psi = new ProcessStartInfo("reg.exe", "export \"" + Short(hive) + "\\" + sub + "\" \"" + file + "\" /y" +
                (view == RegistryView.Registry32 ? " /reg:32" : " /reg:64"))
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
        }

        /// <summary>Opens regedit at the key (it shows the key that was open last).</summary>
        public static void ShowInRegedit(RegistryHive hive, RegistryView view, string sub)
        {
            const string applet = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";
            using (var k = Registry.CurrentUser.CreateSubKey(applet))
            {
                // The first part ("Computer") is translated; take it from the last saved path when there is one.
                string last = k.GetValue("LastKey") as string ?? "";
                int cut = last.IndexOf(@"\HKEY_", StringComparison.OrdinalIgnoreCase);
                string computer = cut > 0 ? last.Substring(0, cut)
                    : (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "Компьютер" : "Computer");
                k.SetValue("LastKey", computer + "\\" + Long(hive) + "\\" + Real(hive, view, sub));
            }
            Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Reading program paths out of command lines and registry values. sid: whose registry the text comes
    /// from (null for the machine's), so %LOCALAPPDATA% and the like expand into that user's profile.
    /// </summary>
    static class PathCheck
    {
        static readonly Regex exeRe = new Regex(@"^(.+?\.(exe|com|bat|cmd|dll|ocx|sys|msc|cpl|scr|vbs|js|ps1))(?=$|[\s,""])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Programs that only start something else: their own presence says nothing about the entry.</summary>
        static readonly HashSet<string> launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rundll32.exe", "regsvr32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "msiexec.exe",
            "explorer.exe", "mshta.exe", "conhost.exe", "dllhost.exe", "svchost.exe", "reg.exe", "schtasks.exe", "sc.exe",
        };

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetFileAttributes(string path);

        /// <summary>The program a command line starts: quoted, or up to its extension; null when unclear.</summary>
        public static string ExePath(string command, string sid = null)
        {
            string s = UserProfiles.Expand((command ?? "").Trim(), sid).Trim();
            if (s.Length == 0 || s.StartsWith("@")) return null;
            string p;
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                p = end > 1 ? s.Substring(1, end - 1).Trim() : null;
            }
            else
            {
                var m = exeRe.Match(s);
                p = m.Success ? m.Groups[1].Value.Trim() : null;
            }
            // A variable that could not be expanded for that user: unknown, never guessed.
            return p != null && p.IndexOf('%') < 0 ? p : null;
        }

        /// <summary>
        /// True only when it is certain that the file of a command line or path is gone: a full path on a
        /// local disk that is present. Network paths, removable disks and launchers are never judged.
        /// </summary>
        public static bool IsMissing(string command, bool view32 = false, string sid = null)
        {
            return IsMissingPath(ExePath(command, sid), view32);
        }

        /// <summary>Gone for sure: Windows says "not found" (access denied, too long and the like are "cannot tell").</summary>
        static bool NotFound(string p)
        {
            if (GetFileAttributes(p) != 0xFFFFFFFF) return false;
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return err == 2 || err == 3;   // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        }

        public static bool IsMissingPath(string p, bool view32 = false, string sid = null)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try
            {
                p = UserProfiles.Expand(p.Trim().Trim('"'), sid);
                if (p.IndexOf('%') >= 0 || p.StartsWith(@"\\") || p.Length < 4 || p[1] != ':' || p[2] != '\\') return false;
                if (p.IndexOf(@"\sysnative\", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
                if (launchers.Contains(Path.GetFileName(p.TrimEnd('\\')))) return false;
                var drive = new DriveInfo(p.Substring(0, 3));
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
                if (!NotFound(p)) return false;
                // A 32-bit program's "System32" is really SysWOW64, and its %ProgramFiles% is "Program Files (x86)"
                // (this app runs 64-bit and expands them the 64-bit way).
                if (view32)
                    foreach (var pair in new[]
                    {
                        Tuple.Create(Environment.SystemDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")),
                        Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
                        Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)),
                    })
                    {
                        string from = pair.Item1.TrimEnd('\\') + "\\";
                        if (string.IsNullOrEmpty(pair.Item2) || !p.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!NotFound(Path.Combine(pair.Item2, p.Substring(from.Length)))) return false;
                    }
                return true;
            }
            catch { return false; }
        }

        /// <summary>True when the text names a file or folder inside one of the folders (each ending with "\").</summary>
        public static bool Mentions(string text, List<string> dirs, string sid = null)
        {
            if (string.IsNullOrEmpty(text) || dirs.Count == 0) return false;
            // Driver paths come as "\??\C:\..." — the prefix is not part of the folder.
            string t = UserProfiles.Expand(text, sid).Replace('/', '\\').Replace(@"\??\", "");
            foreach (string d in dirs)
            {
                string bare = d.TrimEnd('\\');
                int i = 0;
                while ((i = t.IndexOf(bare, i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int after = i + bare.Length;
                    char next = after < t.Length ? t[after] : '\0';
                    // Not followed by a space: "C:\Program Files\Foo" must not match "C:\Program Files\Foo Bar\...".
                    bool startOk = i == 0 || t[i - 1] == '"' || t[i - 1] == ' ' || t[i - 1] == '=' || t[i - 1] == ';' || t[i - 1] == ',';
                    if (startOk && (next == '\\' || next == '"' || next == ';' || next == ',' || next == '\0')) return true;
                    i = after;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Registry entries that point at files which no longer exist (as Bulk Crap Uninstaller and similar
    /// tools find them). Only entries whose target is surely gone are offered; every removal is backed up.
    /// </summary>
    static class RegistryCleaner
    {
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        public static List<Leftover> Scan(Func<bool> cancel)
        {
            var found = new List<Leftover>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<Leftover> add = l =>
            {
                if (seen.Add(l.Path + "|" + l.ValueName)) found.Add(l);
            };

            // Programs in "Apps & features" whose uninstaller and folder are both gone.
            foreach (var src in RegSource.MachineAndUsers(Uninstall))
            {
                if (cancel()) break;
                ForEachSubKey(src, (name, k) =>
                {
                    string display = Reg.Str(k, "DisplayName");
                    if (display.Length == 0 || Reg.Str(k, "SystemComponent") == "1" || Reg.Str(k, "WindowsInstaller") == "1") return;
                    string cmd = Reg.Str(k, "UninstallString");
                    if (cmd.Length == 0) cmd = Reg.Str(k, "QuietUninstallString");
                    string loc = Reg.Str(k, "InstallLocation").Trim('"');
                    if (!PathCheck.IsMissing(cmd, src.Is32, src.Sid) || (loc.Length > 0 && !PathCheck.IsMissingPath(loc, src.Is32, src.Sid))) return;
                    add(Key(src, src.Sub + "\\" + name, Leftover.R("Программа удалена, запись осталась", "Program gone, entry left"), display + " — " + PathCheck.ExePath(cmd, src.Sid)));
                });
            }

            // "App Paths": what Win+R and other programs start by short name.
            foreach (var src in RegSource.MachineAndUsers(AppPaths))
            {
                if (cancel()) break;
                ForEachSubKey(src, (name, k) =>
                {
                    string exe = Reg.Str(k, "");
                    if (PathCheck.IsMissing(exe, src.Is32, src.Sid))
                        add(Key(src, src.Sub + "\\" + name, Leftover.R("Путь запуска к удалённому файлу", "Launch path to a missing file"), PathCheck.ExePath(exe, src.Sid)));
                });
            }

            // Autostart entries, and their on/off state kept by Task Manager (Run only; RunOnce has none).
            foreach (string run in new[] { RegSource.Run, RegSource.Run + "Once" })
                foreach (var src in RegSource.MachineAndUsers(run))
                {
                    if (cancel()) break;
                    ForEachValue(src, (name, value) =>
                    {
                        if (!PathCheck.IsMissing(value, src.Is32, src.Sid)) return;
                        add(Value(src, name, Leftover.R("Автозапуск удалённой программы", "Autostart of a missing program"), PathCheck.ExePath(value, src.Sid)));
                        var ap = run == RegSource.Run ? src.StartupApproved() : null;
                        if (ap == null) return;
                        using (var k = SafeOpen(ap))
                            if (k != null && k.GetValue(name) != null)
                                add(Value(ap, name, Leftover.R("Состояние автозапуска", "Autostart state"), PathCheck.ExePath(value, src.Sid)));
                    });
                }

            // Shared DLL counters and Windows Installer folder references to files that are gone.
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                if (cancel()) break;
                var dll = new RegSource(RegistryHive.LocalMachine, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", null);
                ForEachValueName(dll, name =>
                {
                    if (PathCheck.IsMissingPath(name, dll.Is32))
                        add(Value(dll, name, Leftover.R("Общая библиотека удалена", "Shared library is gone"), name));
                });
            }
            var folders = new RegSource(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders", null);
            ForEachValueName(folders, name =>
            {
                if (PathCheck.IsMissingPath(name)) add(Value(folders, name, Leftover.R("Папка установщика удалена", "Installer folder is gone"), name));
            });

            // Names of programs Explorer remembered ("C:\x\app.exe.FriendlyAppName").
            foreach (var u in Reg.UserRoots())
            {
                if (cancel()) break;
                var mui = new RegSource(u.Hive, RegistryView.Registry64, u.ClassesPrefix + @"Local Settings\Software\Microsoft\Windows\Shell\MuiCache", u.Sid);
                ForEachValueName(mui, name =>
                {
                    int dot = name.LastIndexOf('.');
                    if (dot > 3 && PathCheck.IsMissingPath(name.Substring(0, dot), false, u.Sid))
                        add(Value(mui, name, Leftover.R("Имя удалённой программы в кэше", "Cached name of a missing program"), name.Substring(0, dot)));
                });
            }
            return found;
        }

        static RegistryKey SafeOpen(RegSource src)
        {
            try { return Reg.Open(src.Hive, src.View, src.Sub); }
            catch { return null; }
        }

        static void ForEachSubKey(RegSource src, Action<string, RegistryKey> each)
        {
            using (var root = SafeOpen(src))
            {
                if (root == null) return;
                foreach (string name in root.GetSubKeyNames())
                {
                    try
                    {
                        using (var k = root.OpenSubKey(name))
                            if (k != null) each(name, k);
                    }
                    catch { }
                }
            }
        }

        static void ForEachValue(RegSource src, Action<string, string> each)
        {
            using (var k = SafeOpen(src))
            {
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                {
                    if (name.Length == 0) continue;
                    string v = Reg.Str(k, name);
                    if (v.Length > 0) each(name, v);
                }
            }
        }

        static void ForEachValueName(RegSource src, Action<string> each)
        {
            using (var k = SafeOpen(src))
            {
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                    if (name.Length > 0) each(name);
            }
        }

        static Leftover Key(RegSource src, string sub, string reason, string detail)
        {
            return new Leftover
            {
                Kind = LeftoverKind.RegKey, Hive = src.Hive, View = src.View, SubKey = sub, Path = Reg.Display(src.Hive, src.View, sub),
                Reason = reason, App = detail ?? "", Checked = true,
            };
        }

        static Leftover Value(RegSource src, string name, string reason, string detail)
        {
            return new Leftover
            {
                Kind = LeftoverKind.RegValue, Hive = src.Hive, View = src.View, SubKey = src.Sub, ValueName = name,
                Path = Reg.Display(src.Hive, src.View, src.Sub) + " → " + name, Reason = reason, App = detail ?? "", Checked = true,
            };
        }
    }

    /// <summary>A registry key to look in, and whose it is (Sid null: the machine's).</summary>
    class RegSource
    {
        public const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public readonly RegistryHive Hive;
        public readonly RegistryView View;
        public readonly string Sub, Sid;
        readonly string prefix;

        public RegSource(RegistryHive hive, RegistryView view, string sub, string sid, string prefix = "")
        {
            Hive = hive;
            View = view;
            Sub = sub;
            Sid = sid;
            this.prefix = prefix;
        }

        public bool Is32 { get { return View == RegistryView.Registry32; } }

        /// <summary>The key for everyone (both registry views) and the same key of every loaded user registry.</summary>
        public static IEnumerable<RegSource> MachineAndUsers(string sub)
        {
            yield return new RegSource(RegistryHive.LocalMachine, RegistryView.Registry64, sub, null);
            yield return new RegSource(RegistryHive.LocalMachine, RegistryView.Registry32, sub, null);
            foreach (var u in Reg.UserRoots()) yield return new RegSource(u.Hive, RegistryView.Registry64, u.Prefix + sub, u.Sid, u.Prefix);
        }

        /// <summary>
        /// Where Task Manager keeps the on/off state of this Run key's entries: "Run32" for the 32-bit view of
        /// the machine's Run, "Run" otherwise (always the 64-bit view).
        /// </summary>
        public RegSource StartupApproved()
        {
            string leaf = Is32 && Hive == RegistryHive.LocalMachine ? "Run32" : "Run";
            return new RegSource(Hive, RegistryView.Registry64, prefix + @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + leaf, Sid, prefix);
        }
    }
}
