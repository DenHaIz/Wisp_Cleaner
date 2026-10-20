using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Wisp
{
    class FileItem : IRowInfo, ICheckRow, ISortable
    {
        public string Path = "", Name = "", Dir = "";
        public long Size;
        public DateTime Modified;
        public int Group = -1;          // duplicate group, -1 for large files
        public bool IsHeader;           // "3 identical files" row of a duplicate group
        public int Copies;
        public bool Checked { get; set; }
        public bool Checkable { get { return !IsHeader; } }
        public RowKind RowKind { get { return IsHeader ? RowKind.Header : RowKind.Normal; } }
        public string IconPath { get { return IsHeader ? "\u0001group" : System.IO.Path.GetExtension(Name).ToLowerInvariant(); } }
        public int Dot { get { return 0; } }

        public IComparable SortKey(int column)
        {
            if (column == 1) return Size;
            if (column == 2) return Modified;
            return null;
        }
    }

    /// <summary>Large-file and duplicate search. Walks never follow junctions and skip system and program folders.</summary>
    static class FileScan
    {
        static readonly HashSet<string> skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery", "Config.Msi", "AppData", "node_modules", ".git",
        };

        static readonly List<string> skipRoots = new List<string>();

        static FileScan()
        {
            foreach (var f in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) skipRoots.Add(FileOps.Norm(p));
            }
        }

        static IEnumerable<FileInfo> Walk(string root, Func<bool> cancel, Action<int> progress)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            int seen = 0;
            while (stack.Count > 0)
            {
                if (cancel()) yield break;
                var dir = stack.Pop();
                List<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos().ToList(); }
                catch { continue; }
                foreach (var e in entries)
                {
                    FileAttributes a;
                    try { a = e.Attributes; } catch { continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    if ((a & FileAttributes.Directory) != 0)
                    {
                        if (skipNames.Contains(e.Name)) continue;
                        if ((a & FileAttributes.System) != 0 && (a & FileAttributes.Hidden) != 0) continue;
                        string n = FileOps.Norm(e.FullName);
                        if (skipRoots.Any(r => n.Equals(r, StringComparison.OrdinalIgnoreCase))) continue;
                        stack.Push((DirectoryInfo)e);
                        continue;
                    }
                    if ((a & FileAttributes.System) != 0) continue;
                    if (++seen % 500 == 0) progress(seen);
                    yield return (FileInfo)e;
                }
            }
            progress(seen);
        }

        static FileItem Item(FileInfo f)
        {
            return new FileItem { Path = f.FullName, Name = f.Name, Dir = f.DirectoryName, Size = f.Length, Modified = f.LastWriteTime };
        }

        public static List<FileItem> Large(string root, long minSize, int max, Func<bool> cancel, Action<int> progress)
        {
            var list = new List<FileItem>();
            foreach (var f in Walk(root, cancel, progress))
            {
                long len;
                try { len = f.Length; } catch { continue; }
                if (len >= minSize) list.Add(Item(f));
            }
            return list.OrderByDescending(i => i.Size).Take(max).ToList();
        }

        /// <summary>
        /// Groups of identical files: same size, then same hash of the first 64 KB, then same full MD5.
        /// Returned flat: a header row followed by the group's files, biggest waste first.
        /// </summary>
        public static List<FileItem> Duplicates(string root, long minSize, Func<bool> cancel, Action<int> progress, Action<string> stage)
        {
            var bySize = new Dictionary<long, List<FileInfo>>();
            foreach (var f in Walk(root, cancel, progress))
            {
                long len;
                try { len = f.Length; } catch { continue; }
                if (len < minSize) continue;
                List<FileInfo> l;
                if (!bySize.TryGetValue(len, out l)) bySize[len] = l = new List<FileInfo>();
                l.Add(f);
            }
            var groups = new List<List<FileInfo>>();
            var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
            int done = 0, total = candidates.Sum(c => c.Count);
            using (var md5 = MD5.Create())
            {
                foreach (var sameSize in candidates)
                {
                    if (cancel()) return new List<FileItem>();
                    foreach (var byHead in sameSize.GroupBy(f => Hash(md5, f.FullName, 64 * 1024)).Where(g => g.Key != null && g.Count() > 1))
                    {
                        var members = byHead.ToList();
                        bool small = members[0].Length <= 64 * 1024;
                        var full = small ? new[] { members.AsEnumerable() } : members.GroupBy(f => Hash(md5, f.FullName, long.MaxValue)).Where(g => g.Key != null).Select(g => g.AsEnumerable()).ToArray();
                        foreach (var g in full)
                        {
                            var l = g.ToList();
                            if (l.Count > 1) groups.Add(l);
                        }
                        if (cancel()) return new List<FileItem>();
                    }
                    done += sameSize.Count;
                    stage(string.Format(L.T("Сравниваем содержимое: {0} из {1}", "Comparing contents: {0} of {1}"), done, total));
                }
            }

            var result = new List<FileItem>();
            int gi = 0;
            foreach (var g in groups.OrderByDescending(g => g[0].Length * (g.Count - 1)).Take(300))
            {
                result.Add(new FileItem { IsHeader = true, Group = gi, Size = g[0].Length, Copies = g.Count, Name = "", Path = "" });
                foreach (var f in g.OrderByDescending(f => f.LastWriteTime))
                {
                    var it = Item(f);
                    it.Group = gi;
                    result.Add(it);
                }
                gi++;
            }
            return result;
        }

        static string Hash(MD5 md5, string path, long limit)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    if (limit == long.MaxValue) return Convert.ToBase64String(md5.ComputeHash(fs));
                    var buf = new byte[limit];
                    int n = 0, r;
                    while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                    return Convert.ToBase64String(md5.ComputeHash(buf, 0, n));
                }
            }
            catch { return null; }
        }
    }
}
