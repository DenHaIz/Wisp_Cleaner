using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Wisp
{
    class CleanResult
    {
        public long Freed;
        public long Files;
        public long Failed;
        public readonly List<string> Items = new List<string>();
    }

    /// <summary>Scans and cleans catalog items. Everything here runs on worker threads.</summary>
    static class Cleaner
    {
        /// <summary>Measures one item: collects its files (or asks the shell about the Recycle Bin).</summary>
        public static void ScanItem(CleanItem item, Func<bool> cancel)
        {
            var files = new List<FileEntry>();
            long size = 0, count = 0;
            if (item.Special == Special.RecycleBin)
            {
                Native.QueryRecycleBin(out size, out count);
            }
            else
            {
                foreach (string root in item.ExistingRoots())
                {
                    if (cancel != null && cancel()) break;
                    FileOps.Collect(root, item.Patterns, item.Recursive, item.MinAgeHours, files, cancel);
                }
                foreach (var f in files) size += f.Size;
                count = files.Count;
            }
            lock (item)
            {
                item.Files = files;
                item.Size = size;
                item.Count = count;
                item.Scanned = true;
            }
        }

        /// <summary>
        /// Scans groups side by side (one thread per group); itemDone is called from those threads
        /// after each item. Blocks until all are done.
        /// </summary>
        public static void Scan(IEnumerable<CleanGroup> groups, Action<CleanItem> itemDone, Func<bool> cancel)
        {
            var threads = new List<Thread>();
            foreach (var g in groups)
            {
                var group = g;
                var t = new Thread(() =>
                {
                    foreach (var item in group.Items)
                    {
                        if (cancel != null && cancel()) return;
                        try { ScanItem(item, cancel); } catch { item.Scanned = true; }
                        if (itemDone != null) itemDone(item);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();
        }

        /// <summary>
        /// Deletes what the last scan found in the given items. progress(freedSoFar, total) is called
        /// from this thread at most every ~40 ms.
        /// </summary>
        public static CleanResult Clean(List<CleanItem> items, Action<long, long> progress)
        {
            var result = new CleanResult();
            long total = items.Sum(i => i.Size);
            var sw = Stopwatch.StartNew();
            long lastReport = -100;
            Action report = () =>
            {
                if (progress == null || sw.ElapsedMilliseconds - lastReport < 40) return;
                lastReport = sw.ElapsedMilliseconds;
                progress(result.Freed, total);
            };

            foreach (var item in items)
            {
                long before = result.Freed;
                if (item.Special == Special.RecycleBin)
                {
                    if (item.Size > 0 && Native.EmptyRecycleBin())
                    {
                        result.Freed += item.Size;
                        result.Files += item.Count;
                    }
                }
                else
                {
                    List<FileEntry> files;
                    lock (item) files = item.Files;
                    var roots = item.ExistingRoots().ToList();
                    var finals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string r in roots) finals[r] = FileOps.RootFinal(r);
                    foreach (var f in files)
                    {
                        // Only what lies under one of the item's own roots, whatever the scan list says;
                        // the real path is checked again at the moment of deletion.
                        string root = roots.FirstOrDefault(r => FileOps.IsUnder(f.Path, r));
                        if (root == null) continue;
                        if (FileOps.DeleteFile(f.Path, finals[root]))
                        {
                            result.Freed += f.Size;
                            result.Files++;
                        }
                        else result.Failed++;
                        report();
                    }
                    foreach (string r in roots) FileOps.RemoveEmptyDirs(r, item.MinAgeHours);
                }
                if (result.Freed > before) result.Items.Add(item.Name);
                report();
            }
            if (progress != null) progress(result.Freed, total);
            return result;
        }

        /// <summary>Scan + clean of the checked items without any UI (tray auto-clean and /clean).</summary>
        public static CleanResult RunUnattended()
        {
            var groups = CleanCatalog.Build();
            foreach (var g in groups) g.Items.RemoveAll(i => !i.Checked);
            groups.RemoveAll(g => g.Items.Count == 0);
            Scan(groups, null, null);
            var items = groups.SelectMany(g => g.Items).ToList();
            return Clean(items, null);
        }
    }
}
