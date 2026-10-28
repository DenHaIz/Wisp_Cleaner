using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wisp
{
    enum LeftoverKind { Folder, File, RegKey, RegValue, Service, Task }

    /// <summary>
    /// One thing to remove after (or instead of) a program's uninstaller: a folder, a shortcut, a registry
    /// key or value, a service or a scheduled task. Also used for the registry clean-up.
    /// </summary>
    class Leftover : IRowInfo, ICheckRow, ISortable
    {
        /// <summary>Which list column sorts by size (-1: the list has no size column).</summary>
        public int SizeColumn = 2;
        public IComparable SortKey(int column) { return column == SizeColumn ? (IComparable)Size : null; }

        public LeftoverKind Kind;
        public string Final;            // folders: their real path when found; removal requires it unchanged
        public bool IsEntry;            // the program's own uninstall entry: removed last, only if its folders went
        public string Path = "";        // what the user sees: folder, file, registry path, service or task
        public RegistryHive Hive;
        public RegistryView View;
        public string SubKey = "";      // registry key; for services their key under Services
        public string ValueName;        // RegValue only
        public string Name = "";        // service or task name
        public long Size;
        public string App = "";         // which program it belongs to (or which missing file it points at)
        public string Reason = "";
        public bool Checked { get; set; }
        public bool Checkable { get { return true; } }
        public RowKind RowKind { get { return RowKind.Normal; } }
        public string IconPath { get { return null; } }
        public int Dot { get { return 0; } }

        public bool IsRegistry { get { return Kind == LeftoverKind.RegKey || Kind == LeftoverKind.RegValue; } }

        public static string R(string ru, string en) { return L.T(ru, en); }
    }

    /// <summary>
    /// Forced removal of a program whose uninstaller is missing or does not work — what Bulk Crap
    /// Uninstaller calls "force uninstall". Nothing of the program is run: everything that refers to its
    /// folder is collected for the user to review — the folder itself, its data, its uninstall entry,
    /// Windows Installer registration, services, scheduled tasks, autostart, shortcuts, App Paths and
    /// COM registrations. Removal backs up every registry key and value first.
    /// </summary>
    static class ForceUninstall
    {
        static readonly Regex GuidRe = new Regex(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", RegexOptions.IgnoreCase);

        /// <summary>keep: the programs that stay; nothing of theirs is offered.</summary>
        public static List<Leftover> Scan(InstalledApp app, Keep keep)
        {
            var found = new List<Leftover>();
            if (app.Kind != AppKind.Win32) return found;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<Leftover> add = l =>
            {
                string id = l.Kind + "|" + (l.Kind == LeftoverKind.Folder ? FileOps.Norm(l.Path) : l.Path) + "|" + l.ValueName;
                if (!seen.Add(id)) return;
                if (string.IsNullOrEmpty(l.App)) l.App = app.Name;
                found.Add(l);
            };

            add(new Leftover
            {
                Kind = LeftoverKind.RegKey, Hive = app.Hive, View = app.View, SubKey = app.KeyPath + "\\" + app.KeyName, Path = app.RegistryPath,
                Reason = Leftover.R("Запись в списке программ", "Entry in the programs list"), Checked = true, IsEntry = true,
            });

            var dirs = new List<string>();   // folders whose contents surely belong to the program
            foreach (var d in InstallDirs(app, keep))
            {
                add(new Leftover
                {
                    Kind = LeftoverKind.Folder, Path = d.Item1, Size = FileOps.DirSize(d.Item1), Checked = d.Item2,
                    Reason = d.Item2 ? Leftover.R("Папка программы", "Program folder") : Leftover.R("Папка программы? Проверьте", "Program folder? Check it"),
                });
            }
            foreach (var l in Leftovers.Find(app, keep)) add(l);
            foreach (var l in found.Where(f => f.Kind == LeftoverKind.Folder))
            {
                l.Final = Secure.FinalPath(l.Path);
                // Only ticked folders say what else belongs to the program.
                if (l.Checked && !dirs.Any(d => FileOps.IsUnder(l.Path, d))) dirs.Add(FileOps.Norm(l.Path));
            }

            if (app.IsMsi || GuidRe.IsMatch(app.KeyName)) Msi(app.KeyName, add);
            if (dirs.Count > 0)
            {
                Services(dirs, add);
                Tasks(dirs, add);
                Autostart(dirs, add);
                AppPaths(dirs, add);
                Com(dirs, add);
                Shortcuts(dirs, add);
            }
            return found;
        }

        /// <summary>
        /// The program's folder: InstallLocation, or the folder of its icon or uninstaller. Item2 is false
        /// when it is not certain the folder is the program's own (the name does not match, or another
        /// installed program lives there) — such a folder is listed but not ticked.
        /// </summary>
        static List<Tuple<string, bool>> InstallDirs(InstalledApp app, Keep keep)
        {
            var result = new List<Tuple<string, bool>>();
            var candidates = new List<Tuple<string, bool>>();   // path, is InstallLocation
            if (app.InstallLocation.Length > 0) candidates.Add(Tuple.Create(app.InstallLocation, true));
            foreach (string cmd in new[] { app.Icon, app.UninstallString, app.QuietUninstallString })
            {
                string exe = PathCheck.ExePath(cmd);   // already expanded for the program's user
                if (string.IsNullOrEmpty(exe)) continue;
                try { candidates.Add(Tuple.Create(System.IO.Path.GetDirectoryName(exe), false)); } catch { }
            }
            string win = FileOps.Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string self = FileOps.Norm(System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                string dir = c.Item1;
                try
                {
                    if (string.IsNullOrEmpty(dir) || dir.IndexOf('%') >= 0) continue;
                    dir = dir.Trim().Trim('"').TrimEnd('\\');
                    if (!Directory.Exists(dir)) continue;
                    // An uninstaller in a "bin" or "uninstall" subfolder: the program's folder is the parent —
                    // only when the parent carries the program's name and is not a shared folder
                    // ("InstallShield Installation Information", "Package Cache"...).
                    string leaf = System.IO.Path.GetFileName(dir);
                    if (!c.Item2 && !Leftovers.NamedAfter(leaf, app.Name))
                    {
                        string parent = System.IO.Path.GetDirectoryName(dir);
                        string pleaf = parent != null ? System.IO.Path.GetFileName(parent) : null;
                        if (pleaf != null && !Leftovers.IsShared(pleaf) && Leftovers.NamedAfter(pleaf, app.Name)) dir = parent;
                    }
                    string n = FileOps.Norm(dir);
                    if (n.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !FileOps.IsSafeRoot(dir) || self.StartsWith(n, StringComparison.OrdinalIgnoreCase)) continue;
                    if (keep.Contains(dir) && !c.Item2) continue;
                    if (!seen.Add(n)) continue;
                    // Ticked only when the folder carries the program's own name (a publisher's folder may hold its
                    // other products), is not shared, and no program that stays lives in it or around it.
                    bool sure = Leftovers.NamedAfter(System.IO.Path.GetFileName(dir), app.Name) && !keep.Touches(dir);
                    result.Add(Tuple.Create(dir, sure));
                }
                catch { }
            }
            return result;
        }

        // ---------- Windows Installer ----------

        /// <summary>{12345678-ABCD-...} → the "packed" form Windows Installer uses as a key name.</summary>
        static string Pack(string guid)
        {
            string g = guid.Trim('{', '}').Replace("-", "");
            if (g.Length != 32) return null;
            Func<string, string> rev = s => new string(s.Reverse().ToArray());
            var sb = new System.Text.StringBuilder(rev(g.Substring(0, 8)) + rev(g.Substring(8, 4)) + rev(g.Substring(12, 4)));
            for (int i = 16; i < 32; i += 2) sb.Append(g[i + 1]).Append(g[i]);
            return sb.ToString().ToUpperInvariant();
        }

        static void Msi(string guid, Action<Leftover> add)
        {
            string p = Pack(guid);
            if (p == null) return;
            string reason = Leftover.R("Регистрация установщика Windows", "Windows Installer registration");
            var keys = new List<Tuple<RegistryHive, string>>
            {
                Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Products\" + p),
                Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Features\" + p),
            };
            const string userData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";
            try
            {
                using (var ud = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, userData))
                    if (ud != null)
                        foreach (string sid in ud.GetSubKeyNames()) keys.Add(Tuple.Create(RegistryHive.LocalMachine, userData + "\\" + sid + @"\Products\" + p));
            }
            catch { }
            foreach (var u in Reg.UserRoots())
            {
                keys.Add(Tuple.Create(u.Hive, u.Prefix + @"Software\Microsoft\Installer\Products\" + p));
                keys.Add(Tuple.Create(u.Hive, u.Prefix + @"Software\Microsoft\Installer\Features\" + p));
            }
            foreach (var k in keys) AddKeyIfExists(k.Item1, RegistryView.Registry64, k.Item2, reason, true, add);

            // The upgrade code lists the product as a value.
            const string upgrade = @"SOFTWARE\Classes\Installer\UpgradeCodes";
            try
            {
                using (var root = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, upgrade))
                    if (root != null)
                        foreach (string code in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(code))
                                if (k != null && k.GetValue(p) != null)
                                    add(Value(RegistryHive.LocalMachine, RegistryView.Registry64, upgrade + "\\" + code, p, reason, true));
            }
            catch { }
        }

        // ---------- what refers to the program's folder ----------

        static void Services(List<string> dirs, Action<Leftover> add)
        {
            const string root = @"SYSTEM\CurrentControlSet\Services";
            try
            {
                using (var services = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, root))
                {
                    if (services == null) return;
                    foreach (string name in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = services.OpenSubKey(name))
                            {
                                if (k == null || !PathCheck.Mentions(Reg.Str(k, "ImagePath"), dirs)) continue;
                                object type = k.GetValue("Type");
                                bool driver = type is int && ((int)type & 0x3) != 0;
                                string display = Reg.Str(k, "DisplayName");
                                add(new Leftover
                                {
                                    Kind = LeftoverKind.Service, Name = name, Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, SubKey = root + "\\" + name,
                                    Path = name + (display.Length > 0 && !display.StartsWith("@") && display != name ? " (" + display + ")" : ""),
                                    Reason = driver ? Leftover.R("Драйвер", "Driver") : Leftover.R("Служба", "Service"),
                                    // A driver may be shared with other software: listed, not ticked.
                                    Checked = !driver,
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static readonly Regex taskPart = new Regex(@"<(Command|Arguments)>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        /// <summary>
        /// Scheduled tasks that start something in the folder (read through the Task Scheduler, so it works
        /// without administrator rights too). Ticked only when the program itself is the command; a task that
        /// merely names the folder in its arguments (a backup job, say) is listed unticked.
        /// </summary>
        static void Tasks(List<string> dirs, Action<Leftover> add)
        {
            TaskScheduler.ForEach((path, xml) =>
            {
                bool command = false, args = false;
                foreach (Match m in taskPart.Matches(xml))
                    if (PathCheck.Mentions(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value), dirs))
                    {
                        if (m.Groups[1].Value.Equals("Command", StringComparison.OrdinalIgnoreCase)) command = true;
                        else args = true;
                    }
                if (command || args)
                    add(new Leftover
                    {
                        Kind = LeftoverKind.Task, Name = path, Path = path, Checked = command,
                        Reason = command ? Leftover.R("Задача планировщика", "Scheduled task") : Leftover.R("Задача упоминает папку — проверьте", "Task mentions the folder — check it"),
                    });
            });
        }

        static void Autostart(List<string> dirs, Action<Leftover> add)
        {
            foreach (string sub in new[] { RegSource.Run, RegSource.Run + "Once" })
                foreach (var src in RegSource.MachineAndUsers(sub))
                {
                    try
                    {
                        using (var k = Reg.Open(src.Hive, src.View, src.Sub))
                        {
                            if (k == null) continue;
                            foreach (string name in k.GetValueNames())
                            {
                                if (name.Length == 0 || !PathCheck.Mentions(Reg.Str(k, name), dirs, src.Sid)) continue;
                                add(Value(src.Hive, src.View, src.Sub, name, Leftover.R("Автозапуск", "Autostart"), true));
                                // Task Manager's on/off state of the same entry (Run only; RunOnce has none).
                                if (sub != RegSource.Run) continue;
                                var ap = src.StartupApproved();
                                using (var a = Reg.Open(ap.Hive, ap.View, ap.Sub))
                                    if (a != null && a.GetValue(name) != null)
                                        add(Value(ap.Hive, ap.View, ap.Sub, name, Leftover.R("Состояние автозапуска", "Autostart state"), true));
                            }
                        }
                    }
                    catch { }
                }
        }

        static void AppPaths(List<string> dirs, Action<Leftover> add)
        {
            foreach (var src in RegSource.MachineAndUsers(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
            {
                try
                {
                    using (var root = Reg.Open(src.Hive, src.View, src.Sub))
                    {
                        if (root == null) continue;
                        foreach (string name in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(name))
                                if (k != null && (PathCheck.Mentions(Reg.Str(k, ""), dirs, src.Sid) || PathCheck.Mentions(Reg.Str(k, "Path"), dirs, src.Sid)))
                                    AddKeyIfExists(src.Hive, src.View, src.Sub + "\\" + name, Leftover.R("Путь запуска (App Paths)", "Launch path (App Paths)"), true, add);
                    }
                }
                catch { }
            }
        }

        /// <summary>COM classes served from the program's folder (shell extensions, automation servers).</summary>
        static void Com(List<string> dirs, Action<Leftover> add)
        {
            var roots = new List<RegSource>
            {
                new RegSource(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\CLSID", null),
                new RegSource(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Classes\CLSID", null),
            };
            foreach (var u in Reg.UserRoots()) roots.Add(new RegSource(u.Hive, RegistryView.Registry64, u.ClassesPrefix + "CLSID", u.Sid));
            foreach (var src in roots)
            {
                try
                {
                    using (var root = Reg.Open(src.Hive, src.View, src.Sub))
                    {
                        if (root == null) continue;
                        foreach (string clsid in root.GetSubKeyNames())
                        {
                            try
                            {
                                foreach (string server in new[] { @"\InprocServer32", @"\LocalServer32" })
                                    using (var k = root.OpenSubKey(clsid + server))
                                        if (k != null && PathCheck.Mentions(Reg.Str(k, ""), dirs, src.Sid))
                                        {
                                            AddKeyIfExists(src.Hive, src.View, src.Sub + "\\" + clsid, Leftover.R("COM-компонент", "COM component"), true, add);
                                            break;
                                        }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>Start menu, desktop and taskbar shortcuts of every user that start something in the folder.</summary>
        static void Shortcuts(List<string> dirs, Action<Leftover> add)
        {
            var places = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            foreach (var u in UserProfiles.All)
            {
                places.Add(System.IO.Path.Combine(u.Roaming, @"Microsoft\Windows\Start Menu\Programs"));
                places.Add(System.IO.Path.Combine(u.Roaming, @"Microsoft\Internet Explorer\Quick Launch"));
                places.Add(System.IO.Path.Combine(u.Dir, "Desktop"));
            }
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(t);
                foreach (string place in places.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var stack = new Stack<Tuple<string, int>>();
                    stack.Push(Tuple.Create(place, 0));
                    while (stack.Count > 0)
                    {
                        var cur = stack.Pop();
                        try
                        {
                            if (!Directory.Exists(cur.Item1)) continue;
                            if (cur.Item2 < 5)
                                foreach (string d in Directory.GetDirectories(cur.Item1))
                                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(Tuple.Create(d, cur.Item2 + 1));
                            foreach (string lnk in Directory.GetFiles(cur.Item1, "*.lnk"))
                            {
                                string target = LinkTarget(shell, lnk);
                                if (target == null || !PathCheck.Mentions(target, dirs)) continue;
                                long size = 0;
                                try { size = new FileInfo(lnk).Length; } catch { }
                                add(new Leftover { Kind = LeftoverKind.File, Path = lnk, Size = size, Reason = Leftover.R("Ярлык", "Shortcut"), Checked = true });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        static string LinkTarget(object shell, string lnk)
        {
            object sc = null;
            try
            {
                sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                string args = sc.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null) as string;
                return "\"" + target + "\" " + args;
            }
            catch { return null; }
            finally
            {
                if (sc != null) Marshal.ReleaseComObject(sc);
            }
        }

        static void AddKeyIfExists(RegistryHive hive, RegistryView view, string sub, string reason, bool check, Action<Leftover> add)
        {
            try
            {
                using (var k = Reg.Open(hive, view, sub))
                    if (k == null) return;
                add(new Leftover { Kind = LeftoverKind.RegKey, Hive = hive, View = view, SubKey = sub, Path = Reg.Display(hive, view, sub), Reason = reason, Checked = check });
            }
            catch { }
        }

        static Leftover Value(RegistryHive hive, RegistryView view, string sub, string name, string reason, bool check)
        {
            return new Leftover
            {
                Kind = LeftoverKind.RegValue, Hive = hive, View = view, SubKey = sub, ValueName = name,
                Path = Reg.Display(hive, view, sub) + " → " + name, Reason = reason, Checked = check,
            };
        }
    }

    /// <summary>Late-bound access to the Task Scheduler ("Schedule.Service"): the tasks this account may see.</summary>
    static class TaskScheduler
    {
        static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        static object Get(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);
        }

        static object Connect()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return null;
            object svc = Activator.CreateInstance(t);
            Call(svc, "Connect");
            return svc;
        }

        /// <summary>Calls each(path, xml) for every task, hidden ones included.</summary>
        public static void ForEach(Action<string, string> each)
        {
            object svc = null;
            try
            {
                svc = Connect();
                if (svc == null) return;
                var folders = new Stack<object>();
                folders.Push(Call(svc, "GetFolder", "\\"));
                while (folders.Count > 0)
                {
                    object folder = folders.Pop();
                    try
                    {
                        object tasks = Call(folder, "GetTasks", 1 /* TASK_ENUM_HIDDEN */);
                        int n = (int)Get(tasks, "Count");
                        for (int i = 1; i <= n; i++)
                        {
                            object task = null;
                            try
                            {
                                task = Get(tasks, "Item", i);
                                each((string)Get(task, "Path"), (string)Get(task, "Xml"));
                            }
                            catch { }
                            finally { if (task != null) Marshal.ReleaseComObject(task); }
                        }
                        Marshal.ReleaseComObject(tasks);
                        object subs = Call(folder, "GetFolders", 0);
                        int m = (int)Get(subs, "Count");
                        for (int i = 1; i <= m; i++) folders.Push(Get(subs, "Item", i));
                        Marshal.ReleaseComObject(subs);
                    }
                    catch { }
                    finally { Marshal.ReleaseComObject(folder); }
                }
            }
            catch { }
            finally { if (svc != null) Marshal.ReleaseComObject(svc); }
        }

        /// <summary>The task's definition, for the backup; null when it cannot be read.</summary>
        public static string Xml(string path)
        {
            object svc = null, folder = null, task = null;
            try
            {
                svc = Connect();
                if (svc == null) return null;
                folder = Call(svc, "GetFolder", "\\");
                task = Call(folder, "GetTask", path);
                return (string)Get(task, "Xml");
            }
            catch { return null; }
            finally
            {
                foreach (var o in new[] { task, folder, svc }) if (o != null) Marshal.ReleaseComObject(o);
            }
        }
    }

    class RemoveResult
    {
        public int Done;
        public long Freed;
        public int Reboot;               // folders that go away only after a restart (files were in use)
        public string BackupDir;         // where the registry backups went (null if nothing was backed up)
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>Removes leftovers in a safe order; every registry key and value is exported to a .reg file first.</summary>
    static class LeftoverRemover
    {
        static readonly object backupLock = new object();

        /// <summary>A new, empty backup folder for this removal (never shared with another one).</summary>
        static string NewBackupDir()
        {
            lock (backupLock)
            {
                string root = Settings.BackupDir;
                if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(L.T("папка резервных копий — ссылка на другое место", "the backup folder is a link to another place"));
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
                for (int i = 1; ; i++)
                {
                    string dir = Path.Combine(root, i == 1 ? stamp : stamp + " (" + i + ")");
                    if (!Directory.Exists(dir)) return dir;
                }
            }
        }

        /// <summary>The folder (or the file's folder) is still where it was found and no junction leads elsewhere now.</summary>
        static bool StillTheSame(Leftover l)
        {
            if (Secure.HasReparsePoint(l.Path)) return false;
            if (l.Kind != LeftoverKind.Folder || l.Final == null) return true;
            string now = Secure.FinalPath(l.Path);
            return now != null && string.Equals(now, l.Final, StringComparison.OrdinalIgnoreCase);
        }

        public static RemoveResult Remove(List<Leftover> items)
        {
            var r = new RemoveResult();
            string backup = NewBackupDir();
            int seq = 0;
            var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Short file names (the full key paths are listed in "index.txt"): reg.exe cannot write past MAX_PATH.
            Func<string, string, string> backupFile = (what, ext) =>
            {
                Directory.CreateDirectory(backup);
                r.BackupDir = backup;
                string leaf = Regex.Replace(what.Substring(what.LastIndexOf('\\') + 1), @"[\\/:*?""<>|{}\s]+", "_").Trim('_');
                if (leaf.Length > 40) leaf = leaf.Substring(0, 40);
                string file = (++seq).ToString("000") + " " + leaf + ext;
                File.AppendAllText(Path.Combine(backup, "index.txt"), file + "\t" + what + "\r\n", System.Text.Encoding.UTF8);
                return Path.Combine(backup, file);
            };
            Action<RegistryHive, RegistryView, string> export = (hive, view, sub) =>
            {
                string id = Reg.Display(hive, view, sub);
                if (!exported.Add(id)) return;
                if (!Reg.Export(hive, view, sub, backupFile(id, ".reg")))
                    throw new IOException(L.T("не удалось сохранить резервную копию", "could not save a backup"));
            };

            // 1. Services: stopped and deleted first, so their processes end and Windows does not restart them.
            foreach (var s in items.Where(i => i.Kind == LeftoverKind.Service))
            {
                try
                {
                    export(s.Hive, s.View, s.SubKey);
                    Run("sc.exe", "stop \"" + s.Name + "\"", 15000);
                    WaitStopped(s.Name);
                    int code = Run("sc.exe", "delete \"" + s.Name + "\"", 15000);
                    if (code == 0 || code == 1072 /* already marked for deletion */ || code == 1060 /* does not exist */) r.Done++;
                    else r.Errors.Add(s.Path + ": sc " + code);
                }
                catch (Exception ex) { r.Errors.Add(s.Path + ": " + ex.Message); }
            }

            // 2. Whatever still runs from the program's folders would keep its files locked
            //    (the user saw the list and agreed, see RunningUnder).
            var folders = items.Where(i => i.Kind == LeftoverKind.Folder).ToList();
            KillProcessesUnder(folders.Where(StillTheSame).Select(f => FileOps.Norm(f.Path)).ToList());

            // 3. Scheduled tasks (their XML is kept in the backup folder: "schtasks /create /xml" brings one back).
            foreach (var t in items.Where(i => i.Kind == LeftoverKind.Task))
            {
                try
                {
                    string xml = TaskScheduler.Xml(t.Name);
                    if (xml != null) File.WriteAllText(backupFile(L.T("Задача ", "Task ") + t.Name, ".xml"), xml, System.Text.Encoding.Unicode);
                    int code = Run("schtasks.exe", "/delete /tn \"" + t.Name + "\" /f", 15000);
                    if (code == 0) r.Done++;
                    else r.Errors.Add(t.Path + ": schtasks " + code);
                }
                catch (Exception ex) { r.Errors.Add(t.Path + ": " + ex.Message); }
            }

            // 4. Registry values, then keys (a key inside one already removed is done with it). The program's
            //    own uninstall entry waits until its folders are gone (step 6).
            foreach (var v in items.Where(i => i.Kind == LeftoverKind.RegValue))
            {
                try
                {
                    export(v.Hive, v.View, v.SubKey);
                    using (var k = Reg.Open(v.Hive, v.View, v.SubKey, true))
                        if (k != null) k.DeleteValue(v.ValueName, false);
                    r.Done++;
                }
                catch (Exception ex) { r.Errors.Add(v.Path + ": " + ex.Message); }
            }
            var gone = new List<string>();
            Action<Leftover> removeKey = k =>
            {
                string id = Reg.Display(k.Hive, k.View, k.SubKey);
                if (gone.Any(g => id.StartsWith(g + "\\", StringComparison.OrdinalIgnoreCase))) { r.Done++; return; }
                try
                {
                    using (var test = Reg.Open(k.Hive, k.View, k.SubKey))
                        if (test == null) { r.Done++; return; }
                    export(k.Hive, k.View, k.SubKey);
                    using (var b = RegistryKey.OpenBaseKey(k.Hive, k.View)) b.DeleteSubKeyTree(k.SubKey, false);
                    gone.Add(id);
                    r.Done++;
                }
                catch (Exception ex) { r.Errors.Add(k.Path + ": " + ex.Message); }
            };
            foreach (var k in items.Where(i => i.Kind == LeftoverKind.RegKey && !i.IsEntry).OrderBy(i => i.SubKey.Length)) removeKey(k);

            // 5. Shortcuts, then the folders: to the Recycle Bin; files in use are deleted at the next restart.
            foreach (var f in items.Where(i => i.Kind == LeftoverKind.File))
            {
                try
                {
                    if (!StillTheSame(f)) { r.Errors.Add(f.Path + ": " + L.T("путь изменился", "the path changed")); continue; }
                    if (File.Exists(f.Path)) Native.MoveToRecycleBin(new[] { f.Path });
                    if (File.Exists(f.Path)) File.Delete(f.Path);
                    r.Done++;
                    r.Freed += f.Size;
                    RemoveEmptyStartMenuFolders(Path.GetDirectoryName(f.Path));
                }
                catch (Exception ex) { r.Errors.Add(f.Path + ": " + ex.Message); }
            }
            bool foldersFailed = false;
            foreach (var d in folders.OrderByDescending(f => f.Path.Length))
            {
                if (!Directory.Exists(d.Path)) { r.Done++; r.Freed += d.Size; continue; }
                if (!FileOps.IsSafeRoot(d.Path) || !StillTheSame(d))
                {
                    r.Errors.Add(d.Path + ": " + L.T("путь изменился", "the path changed"));
                    foldersFailed = true;
                    continue;
                }
                Native.MoveToRecycleBin(new[] { d.Path });
                bool reboot = Directory.Exists(d.Path) && StillTheSame(d) && HardDelete(d.Path);
                if (!Directory.Exists(d.Path)) { r.Done++; r.Freed += d.Size; }
                else if (reboot) { r.Done++; r.Reboot++; }
                else { r.Errors.Add(d.Path); foldersFailed = true; }
            }

            // 6. The program's entry: only when its folders went, so a failed removal can be tried again.
            foreach (var k in items.Where(i => i.Kind == LeftoverKind.RegKey && i.IsEntry))
            {
                if (foldersFailed) r.Errors.Add(k.Path + ": " + L.T("запись оставлена — папку не удалось удалить", "entry kept — a folder could not be removed"));
                else removeKey(k);
            }
            return r;
        }

        /// <summary>
        /// Deletes what the Recycle Bin left. A file still in use (and only that) is deleted at the next restart;
        /// returns true if something waits for it.
        /// </summary>
        static bool HardDelete(string dir)
        {
            string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            string parentFinal = parent != null ? Secure.FinalPath(parent) : null;
            string dirFinal = Secure.FinalPath(dir);
            if (parentFinal == null || dirFinal == null) return false;
            bool pending = false;
            var files = new List<FileEntry>();
            FileOps.Collect(dir, null, true, 0, files);
            foreach (var f in files)
            {
                bool inUse;
                if (!Secure.Delete(f.Path, dirFinal, out inUse) && inUse && Secure.DeleteOnReboot(f.Path)) pending = true;
            }
            FileOps.RemoveEmptyDirs(dir, 0);
            var rest = new List<string>();
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                rest.Add(d);
                try
                {
                    foreach (string sub in Directory.GetDirectories(d))
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                }
                catch { }
            }
            foreach (string d in rest.OrderByDescending(x => x.Length))
            {
                try
                {
                    // Folders follow their files: queued for the restart only after something inside was.
                    if (Directory.Exists(d) && !Secure.Delete(d, d == dir ? parentFinal : dirFinal) && pending) Secure.DeleteOnReboot(d);
                }
                catch { }
            }
            return pending;
        }

        /// <summary>A Start menu folder left empty by removing a shortcut goes too (never the Programs folder itself).</summary>
        static void RemoveEmptyStartMenuFolders(string dir)
        {
            var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) };
            roots.AddRange(UserProfiles.All.Select(u => Path.Combine(u.Roaming, @"Microsoft\Windows\Start Menu\Programs")));
            string root = roots.FirstOrDefault(x => !string.IsNullOrEmpty(x) && FileOps.IsUnder(dir, x) && !FileOps.SamePath(dir, x));
            if (root == null) return;
            while (dir != null && !FileOps.SamePath(dir, root) && FileOps.IsUnder(dir, root))
            {
                try
                {
                    if (Secure.HasReparsePoint(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                    Directory.Delete(dir);
                }
                catch { return; }
                dir = Path.GetDirectoryName(dir);
            }
        }

        static List<Process> ProcessesUnder(List<string> dirs)
        {
            var list = new List<Process>();
            if (dirs.Count == 0) return list;
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                string img = p.Id == self || p.Id <= 4 ? null : Secure.ImagePath(p.Id);
                if (img != null && dirs.Any(d => img.StartsWith(d, StringComparison.OrdinalIgnoreCase))) list.Add(p);
                else p.Dispose();
            }
            return list;
        }

        /// <summary>
        /// Programs running from the ticked folders, as "name.exe — user (session N)", so the user can see
        /// before agreeing whose work would be closed (on a terminal server: other users' too).
        /// </summary>
        public static List<string> RunningUnder(IEnumerable<Leftover> chosen)
        {
            var result = new List<string>();
            var dirs = chosen.Where(i => i.Kind == LeftoverKind.Folder).Select(f => FileOps.Norm(f.Path)).ToList();
            foreach (var p in ProcessesUnder(dirs))
            {
                try
                {
                    string user = "";
                    string sid = Secure.OwnerSid(p.Id);
                    if (sid != null)
                    {
                        try { user = new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value; }
                        catch { user = sid; }
                    }
                    result.Add(p.ProcessName + ".exe — " + (user.Length > 0 ? user + ", " : "") + L.T("сеанс ", "session ") + p.SessionId);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return result.Distinct().ToList();
        }

        static void KillProcessesUnder(List<string> dirs)
        {
            foreach (var p in ProcessesUnder(dirs))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static void WaitStopped(string service)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                string output;
                Run("sc.exe", "query \"" + service + "\"", 5000, out output);
                var m = Regex.Match(output ?? "", @"STATE\s*:\s*(\d+)");
                if (!m.Success || m.Groups[1].Value == "1") return;   // 1 = STOPPED
                System.Threading.Thread.Sleep(300);
            }
        }

        static int Run(string exe, string args, int timeout)
        {
            string ignored;
            return Run(exe, args, timeout, out ignored);
        }

        /// <summary>Runs a console tool; output is read as it comes, so the timeout holds even if the tool hangs.</summary>
        static int Run(string exe, string args, int timeout, out string output)
        {
            var sb = new System.Text.StringBuilder();
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool exited = p.WaitForExit(timeout);
                if (exited) p.WaitForExit();   // drains the asynchronous output
                else { try { p.Kill(); } catch { } }
                lock (sb) output = sb.ToString();
                return exited ? p.ExitCode : -1;
            }
        }
    }
}
