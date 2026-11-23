using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wisp
{
    /// <summary>
    /// Programs removed not completely: still on the disk, but without an entry in the programs list — so
    /// neither Windows nor the list shows them. Two kinds are found:
    /// entries Wisp Cleaner removed earlier (from its backups) whose program is still there — those can be
    /// brought back and the program removed by its own uninstaller; and folders in Program Files that no
    /// installed program owns, from which a service, a process, autostart or a scheduled task still runs.
    /// </summary>
    static class OrphanApps
    {
        public static List<InstalledApp> Find(List<InstalledApp> installed)
        {
            var result = new List<InstalledApp>();
            var keep = Keep.From(installed.Where(a => a.Kind == AppKind.Win32));
            using (UserHives.LoadAll())
            {
                try { FromBackups(installed, result); } catch { }
                try { FromRunning(keep, installed, result); } catch { }
            }
            return result;
        }

        // ---------- entries removed by Wisp Cleaner ----------

        static readonly Regex entryPath = new Regex(@"\\(Microsoft\\Windows\\CurrentVersion\\Uninstall\\[^\\]+|Installer\\UserData\\[^\\]+\\Products\\[0-9A-F]{32})$", RegexOptions.IgnoreCase);

        static void FromBackups(List<InstalledApp> installed, List<InstalledApp> result)
        {
            string root = Settings.BackupDir;
            if (!Directory.Exists(root)) return;
            var names = new HashSet<string>(installed.Where(a => a.Kind == AppKind.Win32).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            // The newest backup of a program wins.
            foreach (string dir in Directory.GetDirectories(root).OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
            {
                var index = ReadIndex(dir);
                foreach (string file in Directory.GetFiles(dir, "*.reg"))
                {
                    try
                    {
                        var reg = RegFile.Read(file);
                        if (reg == null || !entryPath.IsMatch(reg.Key)) continue;
                        string sid = reg.Sid;
                        Func<string, string> val = n => UserProfiles.Expand(reg.Value(n) ?? "", sid).Trim();
                        string name = val("DisplayName");
                        if (name.Length == 0 || names.Contains(name)) continue;   // back in the list, or listed already

                        string loc = val("InstallLocation").Trim('"').TrimEnd('\\');
                        string icon = PathCheck.ExePath(val("DisplayIcon"), sid);
                        string uninstall = val("UninstallString");
                        string folder = loc.Length > 3 && Directory.Exists(loc) && FileOps.IsSafeRoot(loc) ? loc : null;
                        if (folder == null && !string.IsNullOrEmpty(icon) && File.Exists(icon) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            folder = Path.GetDirectoryName(icon);
                        if (folder == null || !FileOps.IsSafeRoot(folder)) continue;   // the program is gone: nothing to show

                        string what;
                        bool view32 = index.TryGetValue(Path.GetFileName(file), out what)
                            ? what.IndexOf(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase) >= 0
                            : FileOps.IsUnder(folder, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) &&
                              reg.Key.StartsWith(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase);
                        names.Add(name);
                        result.Add(new InstalledApp
                        {
                            Kind = AppKind.Win32, Orphan = true, Name = name, Publisher = val("Publisher"), Version = val("DisplayVersion"),
                            InstallLocation = folder, Icon = !string.IsNullOrEmpty(icon) && File.Exists(icon) ? icon : null,
                            UninstallString = uninstall, QuietUninstallString = val("QuietUninstallString"),
                            UserSid = sid, BackupReg = file, View = view32 ? RegistryView.Registry32 : RegistryView.Registry64,
                            Evidence = L.T("Запись убрана ", "Entry removed ") + File.GetLastWriteTime(file).ToString("d") + L.T(", а программа на месте", ", the program is still there"),
                        });
                    }
                    catch { }
                }
            }
        }

        static Dictionary<string, string> ReadIndex(string dir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(dir, "index.txt");
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) map[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
            }
            catch { }
            return map;
        }

        /// <summary>Brings a removed entry back from its backup (reg import); true when it is in the registry again.</summary>
        public static bool Restore(InstalledApp app, out string error)
        {
            error = null;
            if (app.BackupReg == null || !File.Exists(app.BackupReg) || app.BackupReg.IndexOf('"') >= 0)
            {
                error = L.T("резервная копия не найдена", "the backup is missing");
                return false;
            }
            // Saved while its user was signed out (HKEY_USERS\WispUser_SID): back into their registry — loaded
            // again for the import, or, when they are signed in now, rewritten to HKEY_USERS\SID.
            string file = app.BackupReg, temp = null;
            var reg = RegFile.Read(file);
            string sid = reg != null && reg.Key.StartsWith(@"HKEY_USERS\" + UserHives.MountPrefix, StringComparison.OrdinalIgnoreCase) ? reg.Sid : null;
            try
            {
                using (sid != null ? UserHives.Load(new[] { sid }) : null)
                {
                    if (sid != null && !UserHives.Roots().Any(r => string.Equals(r.Sid, sid, StringComparison.OrdinalIgnoreCase)))
                    {
                        temp = Path.Combine(Path.GetTempPath(), "WispCleaner restore " + Guid.NewGuid().ToString("N") + ".reg");
                        File.WriteAllText(temp, File.ReadAllText(file).Replace(@"[HKEY_USERS\" + UserHives.MountPrefix, @"[HKEY_USERS\"), Encoding.Unicode);
                        file = temp;
                    }
                    return Import(file, app.View, out error);
                }
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { }
            }
        }

        static bool Import(string file, RegistryView view, out string error)
        {
            error = null;
            var psi = new ProcessStartInfo("reg.exe", "import \"" + file + "\"" + (view == RegistryView.Registry32 ? " /reg:32" : " /reg:64"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }
                if (p.HasExited && p.ExitCode == 0) return true;
                error = err.Trim().Length > 0 ? err.Trim() : "reg import: " + (p.HasExited ? p.ExitCode.ToString() : "timeout");
                return false;
            }
        }

        // ---------- folders that still run something ----------

        static readonly string[] systemPrefixes = { "Windows", "Microsoft", "Internet Explorer", "Common Files", "Reference Assemblies", "MSBuild", "IIS", "dotnet", "PackageManagement", "ModifiableWindowsApps", "WindowsApps", "Uninstall Information" };

        static bool SystemName(string leaf)
        {
            return string.IsNullOrEmpty(leaf) || Leftovers.IsShared(leaf) || systemPrefixes.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        class Hit
        {
            public string Exe;
            public bool Nested;      // inside a vendor's folder that also holds an installed program
            public bool Strong;      // a service, a running process or autostart (not only a scheduled task)
            public readonly List<string> Why = new List<string>();
        }

        static void FromRunning(Keep keep, List<InstalledApp> installed, List<InstalledApp> result)
        {
            var roots = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(f => Environment.GetFolderPath(f)).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);   // program folder → what runs from it
            Action<string, string, bool> note = (exe, why, strong) =>
            {
                if (string.IsNullOrEmpty(exe)) return;
                bool nested;
                string dir = Candidate(exe, roots, keep, out nested);
                if (dir == null) return;
                Hit h;
                if (!found.TryGetValue(dir, out h)) found[dir] = h = new Hit { Exe = exe, Nested = nested };
                if (strong && !h.Strong) { h.Strong = true; h.Exe = exe; }
                if (!h.Why.Contains(why)) h.Why.Add(why);
            };

            // Services (drivers are left to the driver tools).
            try
            {
                using (var services = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services"))
                    if (services != null)
                        foreach (string name in services.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = services.OpenSubKey(name))
                                {
                                    object type = k != null ? k.GetValue("Type") : null;
                                    if (!(type is int) || ((int)type & 0x30) == 0) continue;
                                    note(PathCheck.ExePath(Reg.Str(k, "ImagePath")), L.T("служба", "service"), true);
                                }
                            }
                            catch { }
                        }
            }
            catch { }
            // Running processes.
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.Id > 4) note(Secure.ImagePath(p.Id), L.T("запущена", "running"), true); }
                catch { }
                finally { p.Dispose(); }
            }
            // Autostart of everyone and every signed-in user.
            foreach (var src in RegSource.MachineAndUsers(RegSource.Run))
            {
                try
                {
                    using (var k = Reg.Open(src.Hive, src.View, src.Sub))
                        if (k != null)
                            foreach (string name in k.GetValueNames())
                                if (name.Length > 0) note(PathCheck.ExePath(Reg.Str(k, name), src.Sid), L.T("автозапуск", "autostart"), true);
                }
                catch { }
            }
            // Scheduled tasks.
            var command = new Regex(@"<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            TaskScheduler.ForEach((path, xml) =>
            {
                foreach (Match m in command.Matches(xml))
                    note(PathCheck.ExePath(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)), L.T("задача планировщика", "scheduled task"), false);
            });

            // Names of installed programs (hidden components too): a folder that carries one is that program's,
            // even when its entry does not say where it lives ("Node.js" in "nodejs", "Python" in "Python314").
            var names = new HashSet<string>(installed.Where(a => a.Kind == AppKind.Win32 && !a.Orphan).Select(a => Leftovers.CleanName(a.Name)).Where(n => n.Length >= 3),
                                            StringComparer.OrdinalIgnoreCase);
            Func<string, string> squash = s => Regex.Replace(s ?? "", @"[^\p{L}\p{N}]+", "").ToLowerInvariant();
            var squashed = new HashSet<string>(names.Select(squash).Where(n => n.Length >= 3));
            // A vendor's own top folder while another program of that vendor is installed (Yandex Browser in
            // "Program Files\Yandex", its update service in "Program Files (x86)\Yandex") belongs to it. Inside a
            // vendor's folder each product still counts on its own ("Kaspersky Lab\…" with a VPN installed).
            var vendors = new HashSet<string>(installed.Where(a => a.Kind == AppKind.Win32 && !a.Orphan).Select(a => Vendor(a.Publisher)).Where(v => v.Length >= 3));
            // Profiles of users who are not signed in: their per-user programs are invisible (their registry is
            // closed), so a folder named like one of theirs is not called a leftover.
            var loaded = new HashSet<string>(Reg.UserRoots().Select(u => u.Sid), StringComparer.OrdinalIgnoreCase);
            var closed = UserProfiles.All.Where(u => !loaded.Contains(u.Sid ?? "")).ToList();

            foreach (var kv in found)
            {
                if (result.Any(r => FileOps.SamePath(r.InstallLocation, kv.Key) || FileOps.IsUnder(r.InstallLocation, kv.Key) || FileOps.IsUnder(kv.Key, r.InstallLocation))) continue;
                var hit = kv.Value;
                // Next to an installed program of the same vendor only something that really runs counts
                // (an updater with just a scheduled task is that program's).
                if (hit.Nested && !hit.Strong) continue;
                string exe = hit.Exe, leaf = Path.GetFileName(kv.Key);
                string product = "", company = "", version = "";
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    product = (vi.ProductName ?? "").Trim();
                    company = (vi.CompanyName ?? "").Trim();
                    version = (vi.ProductVersion ?? "").Trim();
                }
                catch { }
                if (company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // a part of Windows
                if (names.Contains(leaf) || names.Contains(Leftovers.CleanName(product)) ||
                    squashed.Contains(squash(leaf)) || squashed.Contains(squash(Leftovers.CleanName(product))) ||
                    names.Any(n => Leftovers.NamedAfter(leaf, n))) continue;
                if (!hit.Nested && (vendors.Contains(Vendor(company)) || vendors.Contains(Vendor(leaf)))) continue;
                if (closed.Any(u => Directory.Exists(Path.Combine(u.Local, leaf)) || Directory.Exists(Path.Combine(u.Roaming, leaf)))) continue;
                string name = product.Length >= 3 && !SystemName(product) && !product.StartsWith("TODO", StringComparison.OrdinalIgnoreCase) ? product : leaf;
                result.Add(new InstalledApp
                {
                    Kind = AppKind.Win32, Orphan = true, Name = name, Publisher = company.StartsWith("TODO", StringComparison.OrdinalIgnoreCase) ? "" : company,
                    Version = version, InstallLocation = kv.Key, Icon = exe, View = RegistryView.Registry64,
                    Evidence = L.T("Нет в списке программ: ", "Not in the programs list: ") + string.Join(", ", hit.Why.Take(3)),
                });
            }
        }

        static readonly Regex companySuffix = new Regex(@"\b(llc|inc|ltd|gmbh|corporation|corp|co|limited|ooo|ag|sa|lab|labs|software|technologies)\b", RegexOptions.IgnoreCase);

        /// <summary>A publisher reduced to its name: "YANDEX LLC" and "Yandex" both give "yandex".</summary>
        static string Vendor(string publisher)
        {
            return Regex.Replace(companySuffix.Replace(publisher ?? "", ""), @"[^\p{L}\p{N}]+", "").ToLowerInvariant();
        }

        /// <summary>
        /// The program folder an executable belongs to: the first folder under Program Files — or, inside a
        /// vendor's folder that also holds an installed program, the second — when no installed program owns it.
        /// </summary>
        static string Candidate(string exe, List<string> roots, Keep keep, out bool nested)
        {
            nested = false;
            try
            {
                foreach (string root in roots)
                {
                    if (!FileOps.IsUnder(exe, root)) continue;
                    var parts = exe.Substring(root.Length).TrimStart('\\').Split('\\');
                    if (parts.Length < 2 || SystemName(parts[0])) return null;
                    string dir = Path.Combine(root, parts[0]);
                    if (keep.Contains(dir))
                    {
                        if (parts.Length < 3 || SystemName(parts[1])) return null;
                        dir = Path.Combine(dir, parts[1]);
                        nested = true;
                    }
                    if (keep.Touches(dir) || !Directory.Exists(dir)) return null;
                    return dir;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>The first key of a .reg file (as reg.exe exports it) and its values.</summary>
    class RegFile
    {
        public string Key = "";
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Value(string name)
        {
            string v;
            return values.TryGetValue(name, out v) ? v : null;
        }

        /// <summary>Whose registry: the SID of an HKEY_USERS key, this account's for HKEY_CURRENT_USER, null for the machine.</summary>
        public string Sid
        {
            get
            {
                var m = Regex.Match(Key, @"^HKEY_USERS\\(?:" + UserHives.MountPrefix + @")?(S-1-[0-9\-]+)\\", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
                m = Regex.Match(Key, @"\\Installer\\UserData\\(S-1-5-21-[0-9\-]+)\\Products\\", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
                return Key.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase) ? Secure.SelfSid : null;
            }
        }

        static readonly Regex valueLine = new Regex(@"^""((?:[^""\\]|\\.)*)""=(.*)$");

        /// <summary>
        /// Reads the first key; for an installer registration ("…\Products\&lt;packed&gt;") the values of its
        /// InstallProperties are taken. Null when the file is not a registry export.
        /// </summary>
        public static RegFile Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) return null;
            var lines = new List<string>();
            var sb = new StringBuilder();
            foreach (string raw in File.ReadAllLines(path))   // reg.exe writes UTF-16 with a BOM
            {
                string line = raw.TrimEnd();
                if (sb.Length > 0) line = line.TrimStart();
                if (line.EndsWith("\\") && !line.StartsWith("["))
                {
                    sb.Append(line, 0, line.Length - 1);
                    continue;
                }
                sb.Append(line);
                lines.Add(sb.ToString());
                sb.Clear();
            }
            var f = new RegFile();
            string section = null;
            bool want = false;
            foreach (string line in lines)
            {
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    if (f.Key.Length == 0) { f.Key = section; want = true; }
                    else want = section.Equals(f.Key + @"\InstallProperties", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!want) continue;
                var m = valueLine.Match(line);
                if (!m.Success) continue;
                string name = Unescape(m.Groups[1].Value), data = m.Groups[2].Value;
                string value = null;
                if (data.StartsWith("\"") && data.EndsWith("\"") && data.Length >= 2) value = Unescape(data.Substring(1, data.Length - 2));
                else if (data.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase)) value = HexString(data.Substring(7));
                else if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                {
                    uint d;
                    if (uint.TryParse(data.Substring(6), System.Globalization.NumberStyles.HexNumber, null, out d)) value = d.ToString();
                }
                if (value != null) f.values[name] = value;
            }
            return f.Key.Length > 0 ? f : null;
        }

        static string Unescape(string s)
        {
            return Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value);
        }

        /// <summary>REG_EXPAND_SZ as reg.exe writes it: UTF-16 bytes in hex, ending with a zero character.</summary>
        static string HexString(string hex)
        {
            try
            {
                var bytes = hex.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => Convert.ToByte(h.Trim(), 16)).ToArray();
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            catch { return null; }
        }
    }
}
