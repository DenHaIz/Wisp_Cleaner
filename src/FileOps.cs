using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Wisp
{
    struct FileEntry
    {
        public string Path;
        public long Size;
    }

    /// <summary>
    /// File walking and deletion with the safety rules every cleaner path goes through:
    /// never follow junctions/symlinks, never treat a top-level system folder as a target,
    /// and only touch what lies under the target's own root.
    /// </summary>
    static class FileOps
    {
        static readonly HashSet<string> forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static FileOps()
        {
            foreach (var f in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) forbidden.Add(Norm(p));
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                forbidden.Add(Norm(Path.Combine(profile, "Downloads")));
                string users = Path.GetDirectoryName(profile);
                if (!string.IsNullOrEmpty(users)) forbidden.Add(Norm(users));
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                forbidden.Add(Norm(Path.Combine(local, "Programs")));
                forbidden.Add(Norm(Path.Combine(local, "Microsoft")));
                forbidden.Add(Norm(Path.Combine(local, "Packages")));
            }
            if (!string.IsNullOrEmpty(profile)) forbidden.Add(Norm(Path.Combine(profile, @"AppData\LocalLow")));
            foreach (var d in DriveInfo.GetDrives()) forbidden.Add(Norm(d.Name));

            // The same well-known folders of every other profile, the shared Start menu and desktop.
            foreach (var u in UserProfiles.All)
            {
                foreach (string rel in new[] { "", "AppData", @"AppData\LocalLow", "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "Favorites" })
                    forbidden.Add(Norm(Path.Combine(u.Dir, rel)));
                foreach (string rel in new[] { "", "Programs", "Microsoft", "Packages" }) forbidden.Add(Norm(Path.Combine(u.Local, rel)));
                foreach (string rel in new[] { "", "Microsoft", @"Microsoft\Windows\Start Menu", @"Microsoft\Windows\Start Menu\Programs", @"Microsoft\Windows\Start Menu\Programs\Startup" })
                    forbidden.Add(Norm(Path.Combine(u.Roaming, rel)));
            }
            foreach (var f in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonStartup,
                                      Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Programs,
                                      Environment.SpecialFolder.Startup, Environment.SpecialFolder.DesktopDirectory })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) forbidden.Add(Norm(p));
            }
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(common)) forbidden.Add(Norm(Path.Combine(common, "Microsoft")));
        }

        public static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\') + "\\";
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the folder may be a cleaning root: deep enough and not a well-known system or user folder.</summary>
        public static bool IsSafeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string n = Norm(path);
            if (n.Length < 8 || forbidden.Contains(n)) return false;
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch { }
            return true;
        }

        public static bool IsUnder(string path, string root)
        {
            return Norm(path).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase) || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        }

        static readonly Dictionary<string, Regex> globs = new Dictionary<string, Regex>();

        public static bool Matches(string name, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return true;
            foreach (string p in patterns)
            {
                Regex re;
                lock (globs)
                {
                    if (!globs.TryGetValue(p, out re))
                    {
                        re = new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        globs[p] = re;
                    }
                }
                if (re.IsMatch(name)) return true;
            }
            return false;
        }

        /// <summary>
        /// Files under root (or root itself when it is a file) that match the patterns and are older than
        /// minAgeHours. Reparse points are never followed. Stops early when cancel() returns true.
        /// </summary>
        public static void Collect(string root, string[] patterns, bool recursive, int minAgeHours, List<FileEntry> into, Func<bool> cancel = null)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-minAgeHours);
            try
            {
                if (File.Exists(root))
                {
                    var fi = new FileInfo(root);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) == 0 && Old(fi, cutoff, minAgeHours))
                        into.Add(new FileEntry { Path = fi.FullName, Size = fi.Length });
                    return;
                }
            }
            catch { return; }
            if (!Directory.Exists(root) || !IsSafeRoot(root)) return;

            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                if (cancel != null && cancel()) return;
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos(); }
                catch { continue; }
                try
                {
                    foreach (var e in entries)
                    {
                        FileAttributes a;
                        try { a = e.Attributes; } catch { continue; }
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;
                        if ((a & FileAttributes.Directory) != 0)
                        {
                            if (recursive) stack.Push((DirectoryInfo)e);
                            continue;
                        }
                        var fi = (FileInfo)e;
                        if (!Matches(fi.Name, patterns) || !Old(fi, cutoff, minAgeHours)) continue;
                        try { into.Add(new FileEntry { Path = fi.FullName, Size = fi.Length }); } catch { }
                    }
                }
                catch { } // folder vanished or access denied mid-enumeration
            }
        }

        static bool Old(FileInfo fi, DateTime cutoff, int minAgeHours)
        {
            if (minAgeHours <= 0) return true;
            try { return fi.LastWriteTimeUtc < cutoff && fi.CreationTimeUtc < cutoff; }
            catch { return false; }
        }

        /// <summary>Size of everything under a folder (reparse points skipped).</summary>
        public static long DirSize(string root, Func<bool> cancel = null)
        {
            if (!Directory.Exists(root)) return 0;
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                if (cancel != null && cancel()) break;
                var dir = stack.Pop();
                try
                {
                    foreach (var e in dir.EnumerateFileSystemInfos())
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((e.Attributes & FileAttributes.Directory) != 0) stack.Push((DirectoryInfo)e);
                        else total += ((FileInfo)e).Length;
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>
        /// The real folder that deletions under root must stay inside (see Secure.Delete). For a file
        /// root (e.g. MEMORY.DMP) it is the file's folder. Null when root is unusable.
        /// </summary>
        public static string RootFinal(string root)
        {
            try
            {
                if (File.Exists(root)) return Secure.FinalPath(Path.GetDirectoryName(root));
                if (!Directory.Exists(root) || !IsSafeRoot(root)) return null;
                return Secure.FinalPath(root);
            }
            catch { return null; }
        }

        /// <summary>Deletes one file (read-only too) if it really lies under rootFinal. False when locked, protected or redirected.</summary>
        public static bool DeleteFile(string path, string rootFinal)
        {
            try { return Secure.Delete(path, rootFinal); }
            catch { return false; }
        }

        /// <summary>Removes empty folders below root (never root itself), deepest first.</summary>
        public static void RemoveEmptyDirs(string root, int minAgeHours)
        {
            if (!Directory.Exists(root) || !IsSafeRoot(root)) return;
            string rootFinal = Secure.FinalPath(root);
            if (rootFinal == null) return;
            DateTime cutoff = DateTime.UtcNow.AddHours(-minAgeHours);
            var all = new List<DirectoryInfo>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var d in dir.EnumerateDirectories())
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        all.Add(d);
                        stack.Push(d);
                    }
                }
                catch { }
            }
            all.Sort((x, y) => y.FullName.Length.CompareTo(x.FullName.Length));
            foreach (var d in all)
            {
                try
                {
                    if (minAgeHours > 0 && d.CreationTimeUtc >= cutoff) continue;
                    using (var e = Directory.EnumerateFileSystemEntries(d.FullName).GetEnumerator())
                        if (e.MoveNext()) continue;
                    Secure.Delete(d.FullName, rootFinal);
                }
                catch { }
            }
        }
    }
}
