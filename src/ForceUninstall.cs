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
        public bool IsEntry;            // the program's own uninstall entry: removed last, only if the program is really gone
        public bool WithEntry;          // goes together with the entry (Windows Installer registration)
        public bool Protected;          // a service Windows starts as a protected process: nothing can stop or delete it
        public List<string> Evidence;   // entry only: the program's own files; while one exists, the program is not gone
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
        /// <summary>Where installers keep copies of uninstallers for many programs.</summary>
        static readonly HashSet<string> installerCaches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Package Cache", "InstallShield Installation Information", "Installer", "Downloaded Installations", "Uninstall Information", "Temp",
        };

        static readonly Regex GuidRe =new Regex(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", RegexOptions.IgnoreCase);

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

            if (!app.Orphan)
                add(new Leftover
                {
                    Kind = LeftoverKind.RegKey, Hive = app.Hive, View = app.View, SubKey = app.KeyPath + "\\" + app.KeyName, Path = app.RegistryPath,
                    Reason = Leftover.R("Запись в списке программ", "Entry in the programs list"), Checked = true, IsEntry = true,
                    Evidence = Evidence(app),
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

            string code = app.ProductCode ?? (GuidRe.IsMatch(app.KeyName) ? app.KeyName : null);
            if (code != null && !app.Orphan) Msi(code, add);
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
        /// The program's own files that say it is still installed: its folder (InstallLocation) and the
        /// executables of its icon and uninstaller — not those kept in shared places (Windows, "Package Cache",
        /// installer caches), which stay after a forced removal anyway.
        /// </summary>
        static List<string> Evidence(InstalledApp app)
        {
            var list = new List<string>();
            string win = FileOps.Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Func<string, bool> sharedPlace = p =>
                FileOps.Norm(p).StartsWith(win, StringComparison.OrdinalIgnoreCase) || !FileOps.IsSafeRoot(p) ||
                p.Split('\\').Any(part => installerCaches.Contains(part));
            try
            {
                string loc = app.InstallLocation.Trim().Trim('"').TrimEnd('\\');
                if (loc.Length > 3 && loc.IndexOf('%') < 0 && Directory.Exists(loc) && !sharedPlace(loc) && !Leftovers.IsShared(System.IO.Path.GetFileName(loc)))
                    list.Add(loc);
            }
            catch { }
            // The icon is the program itself; an uninstaller counts only inside Program Files (elsewhere it is mostly
            // an installer's cached copy).
            var programFiles = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(f => Environment.GetFolderPath(f)).Where(f => !string.IsNullOrEmpty(f)).ToList();
            foreach (string cmd in new[] { app.Icon, app.UninstallString, app.QuietUninstallString })
            {
                try
                {
                    string exe = PathCheck.ExePath(cmd);
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || sharedPlace(exe)) continue;
                    if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;   // an .ico proves nothing
                    if (cmd != app.Icon && !programFiles.Any(pf => FileOps.IsUnder(exe, pf))) continue;
                    if (System.IO.Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase)) list.Add(exe);
                }
                catch { }
            }
            return list;
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
            // Removed together with the entry: a program whose files could not be removed stays registered.
            foreach (var k in keys) AddKeyIfExists(k.Item1, RegistryView.Registry64, k.Item2, reason, true, add, true);

            // The upgrade code lists the product as a value.
            const string upgrade = @"SOFTWARE\Classes\Installer\UpgradeCodes";
            try
            {
                using (var root = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, upgrade))
                    if (root != null)
                        foreach (string code in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(code))
                                if (k != null && k.GetValue(p) != null)
                                {
                                    var v = Value(RegistryHive.LocalMachine, RegistryView.Registry64, upgrade + "\\" + code, p, reason, true);
                                    v.WithEntry = true;
                                    add(v);
                                }
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
                                object type = k.GetValue("Type"), launch = k.GetValue("LaunchProtected");
                                bool driver = type is int && ((int)type & 0x3) != 0;
                                bool prot = launch is int && (int)launch != 0;
                                string display = Reg.Str(k, "DisplayName");
                                add(new Leftover
                                {
                                    Kind = LeftoverKind.Service, Name = name, Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, SubKey = root + "\\" + name,
                                    Path = name + (display.Length > 0 && !display.StartsWith("@") && display != name ? " (" + display + ")" : ""),
                                    Reason = prot ? Leftover.R("Защищённая служба", "Protected service") : driver ? Leftover.R("Драйвер", "Driver") : Leftover.R("Служба", "Service"),
                                    // A driver may be shared with other software: listed, not ticked.
                                    Checked = !driver, Protected = prot,
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

        static void AddKeyIfExists(RegistryHive hive, RegistryView view, string sub, string reason, bool check, Action<Leftover> add, bool withEntry = false)
        {
            try
            {
                using (var k = Reg.Open(hive, view, sub))
                    if (k == null) return;
                add(new Leftover
                {
                    Kind = LeftoverKind.RegKey, Hive = hive, View = view, SubKey = sub, Path = Reg.Display(hive, view, sub), Reason = reason, Checked = check,
                    WithEntry = withEntry,
                });
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
        public int Reboot;               // things that go away only after a restart (files were in use)
        public string BackupDir;         // where the registry backups went (null if nothing was backed up)
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Pending = new List<string>();   // files and folders queued for deletion at the next restart
    }

    /// <summary>
    /// Removes leftovers in a safe order; every registry key and value is exported to a .reg file first.
    /// The result is what is really gone: every item is looked up again afterwards, and whatever is still
    /// there is reported with the reason.
    /// </summary>
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

        /// <summary>
        /// What makes a forced removal impossible: services Windows starts as protected processes and protected
        /// processes running from the programs' folders (antivirus self-defense). Neither can be stopped even by
        /// an administrator, so nothing is touched when this is not empty. all: the whole list shown, so an
        /// unticked protected service of a chosen program counts too.
        /// </summary>
        public static List<string> ProtectedParts(List<Leftover> all, List<Leftover> chosen)
        {
            var result = new List<string>();
            var apps = new HashSet<string>(chosen.Select(c => c.App ?? ""), StringComparer.OrdinalIgnoreCase);
            var mine = all.Where(i => apps.Contains(i.App ?? "")).ToList();
            foreach (var s in mine.Where(i => i.Kind == LeftoverKind.Service && i.Protected))
                result.Add(L.T("служба ", "service ") + s.Path);
            var dirs = mine.Where(i => i.Kind == LeftoverKind.Folder).Select(f => FileOps.Norm(f.Path)).ToList();
            foreach (var p in ProcessesUnder(dirs))
            {
                try { if (Secure.IsProtectedProcess(p.Id)) result.Add(L.T("процесс ", "process ") + p.ProcessName + ".exe"); }
                catch { }
                finally { p.Dispose(); }
            }
            return result.Distinct().ToList();
        }

        enum State { Gone, Pending, Present }

        public static RemoveResult Remove(List<Leftover> items)
        {
            // Keys of signed-out users: their registry is loaded for the removal and its check.
            using (UserHives.Load(UserHives.SidsIn(items.Select(i => i.SubKey)).ToList())) return RemoveNow(items);
        }

        static RemoveResult RemoveNow(List<Leftover> items)
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
            var why = new Dictionary<Leftover, string>();
            Action<Leftover, string> fail = (l, reason) => { if (!why.ContainsKey(l)) why[l] = reason; };
            var blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // program → why it is left alone
            Func<Leftover, bool> open = l => !blocked.ContainsKey(l.App ?? "");

            // 1. Services: stopped and deleted first, so their processes end and Windows does not restart them.
            foreach (var s in items.Where(i => i.Kind == LeftoverKind.Service))
            {
                try
                {
                    export(s.Hive, s.View, s.SubKey);
                    Run("sc.exe", "stop \"" + s.Name + "\"", 15000);
                    WaitStopped(s.Name);
                    int code = Run("sc.exe", "delete \"" + s.Name + "\"", 15000);
                    if (code != 0 && code != 1072 /* already marked for deletion */ && code != 1060 /* does not exist */)
                        fail(s, L.T("служба не удаляется: ", "the service cannot be deleted: ") + Win32Message(code));
                }
                catch (Exception ex) { fail(s, ex.Message); }
            }

            // 2. Whatever still runs from the program's folders would keep its files locked (the user saw the
            //    list and agreed, see RunningUnder). A program that keeps running — or whose service stays — is
            //    left alone from here on: half of it removed would only break it.
            var folders = items.Where(i => i.Kind == LeftoverKind.Folder).ToList();
            var survivors = KillProcessesUnder(folders.Where(StillTheSame).Select(f => FileOps.Norm(f.Path)).ToList());
            foreach (var sv in survivors)
            {
                var owner = folders.FirstOrDefault(f => sv.Item1 != null && FileOps.IsUnder(sv.Item1, f.Path));
                string app = owner != null ? owner.App ?? "" : "";
                if (!blocked.ContainsKey(app)) blocked[app] = sv.Item2;
            }
            foreach (var s in items.Where(i => i.Kind == LeftoverKind.Service))
                if (Check(s, r.Pending) == State.Present && !blocked.ContainsKey(s.App ?? ""))
                    blocked[s.App ?? ""] = L.T("служба ", "service ") + s.Name + L.T(" осталась", " is still there") + (why.ContainsKey(s) ? " — " + why[s] : "");

            // 3. Scheduled tasks (their XML is kept in the backup folder: "schtasks /create /xml" brings one back).
            foreach (var t in items.Where(i => i.Kind == LeftoverKind.Task).Where(open))
            {
                try
                {
                    string xml = TaskScheduler.Xml(t.Name);
                    if (xml != null) File.WriteAllText(backupFile(L.T("Задача ", "Task ") + t.Name, ".xml"), xml, System.Text.Encoding.Unicode);
                    int code = Run("schtasks.exe", "/delete /tn \"" + t.Name + "\" /f", 15000);
                    if (code != 0) fail(t, "schtasks: " + code);
                }
                catch (Exception ex) { fail(t, ex.Message); }
            }

            // 4. Registry values, then keys (a key inside one already removed is done with it). The program's
            //    own entry and its installer registration wait until the program is surely gone (step 6).
            foreach (var v in items.Where(i => i.Kind == LeftoverKind.RegValue && !i.WithEntry).Where(open))
            {
                try
                {
                    export(v.Hive, v.View, v.SubKey);
                    using (var k = Reg.Open(v.Hive, v.View, v.SubKey, true))
                        if (k != null) k.DeleteValue(v.ValueName, false);
                }
                catch (Exception ex) { fail(v, ex.Message); }
            }
            var gone = new List<string>();
            Action<Leftover> removeKey = k =>
            {
                string id = Reg.Display(k.Hive, k.View, k.SubKey);
                if (gone.Any(g => id.StartsWith(g + "\\", StringComparison.OrdinalIgnoreCase))) return;
                try
                {
                    using (var test = Reg.Open(k.Hive, k.View, k.SubKey))
                        if (test == null) return;
                    export(k.Hive, k.View, k.SubKey);
                    using (var b = RegistryKey.OpenBaseKey(k.Hive, k.View)) b.DeleteSubKeyTree(k.SubKey, false);
                    gone.Add(id);
                }
                catch (Exception ex) { fail(k, ex.Message); }
            };
            foreach (var k in items.Where(i => i.Kind == LeftoverKind.RegKey && !i.IsEntry && !i.WithEntry).Where(open).OrderBy(i => i.SubKey.Length)) removeKey(k);

            // 5. Shortcuts, then the folders: to the Recycle Bin; what remains is deleted directly, and a file
            //    another program holds open is deleted at the next restart.
            foreach (var f in items.Where(i => i.Kind == LeftoverKind.File).Where(open))
            {
                try
                {
                    if (!StillTheSame(f)) { fail(f, L.T("путь изменился", "the path changed")); continue; }
                    if (File.Exists(f.Path)) Native.MoveToRecycleBin(new[] { f.Path });
                    if (File.Exists(f.Path)) File.Delete(f.Path);
                    RemoveEmptyStartMenuFolders(Path.GetDirectoryName(f.Path));
                }
                catch (Exception ex) { fail(f, ex.Message); }
            }
            var pendingFolders = new HashSet<Leftover>();
            foreach (var d in folders.Where(open).OrderByDescending(f => f.Path.Length))
            {
                if (!Directory.Exists(d.Path)) continue;
                if (!FileOps.IsSafeRoot(d.Path) || !StillTheSame(d)) { fail(d, L.T("путь изменился", "the path changed")); continue; }
                Native.MoveToRecycleBin(new[] { d.Path });
                if (!Directory.Exists(d.Path) || !StillTheSame(d)) continue;
                bool pending;
                string error = HardDelete(d.Path, r.Pending, out pending);
                if (error != null) fail(d, error);
                else if (pending) pendingFolders.Add(d);
            }

            // 6. The program's entry (and its installer registration): only when the program is surely gone —
            //    nothing of it runs, its folders and services went, and its own files are no longer there.
            //    Otherwise it stays in the list, and the removal can be finished later.
            var skipped = new HashSet<Leftover>();
            foreach (var e in items.Where(i => i.Kind == LeftoverKind.RegKey && i.IsEntry))
            {
                string app = e.App ?? "";
                var together = items.Where(i => i.WithEntry && string.Equals(i.App ?? "", app, StringComparison.OrdinalIgnoreCase)).ToList();
                string keep = null;
                if (blocked.ContainsKey(app)) keep = blocked[app];
                else
                {
                    var left = items.FirstOrDefault(i => string.Equals(i.App ?? "", app, StringComparison.OrdinalIgnoreCase) &&
                                                         (i.Kind == LeftoverKind.Folder || i.Kind == LeftoverKind.Service) &&
                                                         Check(i, r.Pending, pendingFolders) == State.Present);
                    if (left != null) keep = L.T("не удалось удалить ", "could not remove ") + left.Path;
                    else
                    {
                        string file = (e.Evidence ?? new List<string>()).FirstOrDefault(p => (File.Exists(p) || Directory.Exists(p)) && !IsPending(p, r.Pending));
                        if (file != null) keep = L.T("на месте файлы программы: ", "the program's files are still there: ") + file;
                    }
                }
                if (keep != null)
                {
                    fail(e, L.T("запись оставлена в списке — ", "entry kept in the list — ") + keep);
                    foreach (var w in together) skipped.Add(w);
                    continue;
                }
                foreach (var w in together.Where(i => i.Kind == LeftoverKind.RegValue))
                {
                    try
                    {
                        export(w.Hive, w.View, w.SubKey);
                        using (var k = Reg.Open(w.Hive, w.View, w.SubKey, true))
                            if (k != null) k.DeleteValue(w.ValueName, false);
                    }
                    catch (Exception ex) { fail(w, ex.Message); }
                }
                foreach (var w in together.Where(i => i.Kind == LeftoverKind.RegKey).OrderBy(i => i.SubKey.Length)) removeKey(w);
                removeKey(e);
            }

            // 7. The result: every item is looked up again.
            foreach (var b in blocked)
                r.Errors.Add((b.Key.Length > 0 ? b.Key + ": " : "") + L.T("удаление остановлено — ", "removal stopped — ") + b.Value);
            foreach (var i in items)
            {
                if (skipped.Contains(i)) continue;
                var state = Check(i, r.Pending, pendingFolders);
                // A stopped program's items were left alone on purpose (one line above says why); only what went counts.
                if (!open(i) && state == State.Present) continue;
                switch (state)
                {
                    case State.Gone:
                        r.Done++;
                        if (i.Kind == LeftoverKind.Folder || i.Kind == LeftoverKind.File) r.Freed += i.Size;
                        break;
                    case State.Pending:
                        r.Done++;
                        r.Reboot++;
                        break;
                    default:
                        r.Errors.Add(i.Path + ": " + (why.ContainsKey(i) ? why[i] : L.T("осталось на месте", "is still there")));
                        break;
                }
            }
            if (r.Pending.Count > 0) PendingCheck.Remember(r.Pending);
            return r;
        }

        static bool IsPending(string path, List<string> pending)
        {
            return pending.Any(p => FileOps.SamePath(p, path) || FileOps.IsUnder(path, p));
        }

        static State Check(Leftover l, List<string> pending, HashSet<Leftover> pendingFolders = null)
        {
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.RegKey:
                        using (var k = Reg.Open(l.Hive, l.View, l.SubKey)) return k == null ? State.Gone : State.Present;
                    case LeftoverKind.RegValue:
                        using (var k = Reg.Open(l.Hive, l.View, l.SubKey)) return k == null || k.GetValue(l.ValueName) == null ? State.Gone : State.Present;
                    case LeftoverKind.Service:
                        using (var k = Reg.Open(l.Hive, l.View, l.SubKey))
                        {
                            if (k == null) return State.Gone;
                            object flag = k.GetValue("DeleteFlag");   // marked for deletion: goes at the restart
                            return flag is int && (int)flag != 0 ? State.Pending : State.Present;
                        }
                    case LeftoverKind.Task:
                        return TaskScheduler.Xml(l.Name) == null ? State.Gone : State.Present;
                    case LeftoverKind.File:
                        if (!File.Exists(l.Path)) return State.Gone;
                        return IsPending(l.Path, pending) ? State.Pending : State.Present;
                    case LeftoverKind.Folder:
                        if (!Directory.Exists(l.Path)) return State.Gone;
                        return pendingFolders != null && pendingFolders.Contains(l) ? State.Pending : State.Present;
                }
            }
            catch { }
            return State.Present;
        }

        static string Win32Message(int code)
        {
            return new System.ComponentModel.Win32Exception(code).Message + " (" + code + ")";
        }

        /// <summary>
        /// Deletes what the Recycle Bin left. A file another program holds open or has loaded (Restart Manager
        /// names it) is deleted at the next restart and pending is set; anything else that refuses — a file a
        /// protection driver guards, say — is an error: the first one is returned (null when all went).
        /// </summary>
        static string HardDelete(string dir, List<string> pendingList, out bool pending)
        {
            pending = false;
            string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            string parentFinal = parent != null ? Secure.FinalPath(parent) : null;
            string dirFinal = Secure.FinalPath(dir);
            if (parentFinal == null || dirFinal == null) return L.T("папку не открыть", "the folder cannot be opened");
            string error = null;
            var queued = new List<string>();
            var files = new List<FileEntry>();
            FileOps.Collect(dir, null, true, 0, files);
            foreach (var f in files)
            {
                bool inUse;
                if (Secure.Delete(f.Path, dirFinal, out inUse) || !File.Exists(f.Path)) continue;
                var by = inUse ? Secure.LockedBy(f.Path) : new List<string>();
                if (by.Count > 0 && Secure.DeleteOnReboot(f.Path))
                {
                    pending = true;
                    queued.Add(f.Path);
                    continue;
                }
                if (error == null)
                    error = Path.GetFileName(f.Path) + ": " + (by.Count > 0
                        ? L.T("занят (", "in use (") + string.Join(", ", by) + ")"
                        : L.T("доступ запрещён — файл защищён", "access denied — the file is protected"));
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
                    // Folders follow their files: queued for the restart only when everything inside was.
                    if (Directory.Exists(d) && !Secure.Delete(d, d == dir ? parentFinal : dirFinal) && pending && error == null && Secure.DeleteOnReboot(d))
                        queued.Add(d);
                }
                catch { }
            }
            if (error != null) pending = false;
            pendingList.AddRange(queued);
            if (Directory.Exists(dir) && !pending && error == null) error = L.T("папка не удалилась", "the folder was not removed");
            return error;
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

        /// <summary>
        /// Ends what runs from the folders, in a few rounds (a watchdog may start a process again), and returns
        /// what is still running afterwards: its image path and why it did not close.
        /// </summary>
        static List<Tuple<string, string>> KillProcessesUnder(List<string> dirs)
        {
            var left = new List<Tuple<string, string>>();
            if (dirs.Count == 0) return left;
            var denied = new HashSet<int>();
            for (int round = 0; round < 3; round++)
            {
                var list = ProcessesUnder(dirs);
                if (list.Count == 0) return left;
                foreach (var p in list)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch { denied.Add(p.Id); }
                    finally { p.Dispose(); }
                }
                System.Threading.Thread.Sleep(700);
            }
            foreach (var p in ProcessesUnder(dirs))
            {
                try
                {
                    string reason = Secure.IsProtectedProcess(p.Id)
                        ? L.T("защищённый процесс, Windows не даёт его завершить", "a protected process, Windows does not let it be ended")
                        : denied.Contains(p.Id) ? L.T("нет доступа", "access denied") : L.T("запускается снова", "it starts again");
                    left.Add(Tuple.Create(Secure.ImagePath(p.Id), p.ProcessName + ".exe " + L.T("не закрывается: ", "does not close: ") + reason));
                }
                catch { }
                finally { p.Dispose(); }
            }
            return left;
        }

        /// <summary>Waits up to 10 s for the service to stop; true when it is stopped or gone.</summary>
        static bool WaitStopped(string service)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                string output;
                Run("sc.exe", "query \"" + service + "\"", 5000, out output);
                var m = Regex.Match(output ?? "", @"STATE\s*:\s*(\d+)");
                if (!m.Success || m.Groups[1].Value == "1") return true;   // 1 = STOPPED
                System.Threading.Thread.Sleep(300);
            }
            return false;
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

    /// <summary>
    /// Files queued for deletion at the restart are checked at the next start: what is still there after a
    /// restart is reported, so "deleted after a restart" is never taken on trust.
    /// </summary>
    static class PendingCheck
    {
        static string FilePath { get { return Path.Combine(Settings.BackupDir, "pending.txt"); } }

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        static DateTime BootTime { get { return DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64()); } }

        public static void Remember(IEnumerable<string> paths)
        {
            try
            {
                Directory.CreateDirectory(Settings.BackupDir);
                string boot = BootTime.ToString("o");
                File.AppendAllLines(FilePath, paths.Select(p => boot + "\t" + p), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>After a restart: the queued paths that are still there (the list is then forgotten). Empty before a restart.</summary>
        public static List<string> StillThere()
        {
            var left = new List<string>();
            try
            {
                if (!File.Exists(FilePath)) return left;
                var keep = new List<string>();
                DateTime boot = BootTime;
                foreach (string line in File.ReadAllLines(FilePath, System.Text.Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    DateTime then;
                    if (tab < 0 || !DateTime.TryParse(line.Substring(0, tab), null, System.Globalization.DateTimeStyles.RoundtripKind, out then)) continue;
                    // The same boot (give or take the clock's drift): not restarted yet.
                    if (Math.Abs((then - boot).TotalMinutes) < 2) { keep.Add(line); continue; }
                    string path = line.Substring(tab + 1);
                    if (File.Exists(path) || Directory.Exists(path)) left.Add(path);
                }
                if (keep.Count > 0) File.WriteAllLines(FilePath, keep, System.Text.Encoding.UTF8);
                else File.Delete(FilePath);
            }
            catch { }
            return left;
        }
    }
}
