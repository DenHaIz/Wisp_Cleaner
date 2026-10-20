using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Manual clean-up of files: the largest ones and duplicates. Deletion always goes to the Recycle Bin.</summary>
    class FilesPage : Page
    {
        readonly Panel toolbar = new BufferedPanel(), actions = new BufferedPanel(), listHost = new BufferedPanel(), content = new BufferedPanel();
        readonly Segmented mode, threshold;
        readonly FlatButton btnFolder, btnScan, btnMarkCopies, btnClear, btnShow, btnDelete;
        readonly StatusText progressText = new StatusText();
        readonly ThemedListView lv = new ThemedListView();
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static readonly long[] Thresholds = { 100L << 20, 500L << 20, 1L << 30 };

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<FileItem> items = new List<FileItem>();
        bool scanning, deleting, scanned;
        volatile bool cancel;
        int shownMode = -1;
        readonly System.Collections.IComparer sorter;

        public FilesPage(IHost host) : base(host)
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            mode = new Segmented(new[] { "", "" }, new[] { "\uE9F9", "\uE8C8" }, 0);
            mode.SelectedIndexChanged += (s, e) =>
            {
                threshold.Visible = mode.SelectedIndex == 0;
                LayoutToolbar();
                UpdateButtons();
                if (!scanning) Transition.CrossFade(listHost, () => { items.Clear(); scanned = false; Render(); }, 200);
            };
            threshold = new Segmented(new[] { "", "", "" }, new[] { "", "", "" }, 0);
            btnFolder = Button("\uED25");
            btnFolder.Click += (s, e) => PickFolder();
            btnScan = Button("\uE721", ButtonKind.Primary);
            btnScan.Click += (s, e) => { if (scanning) { cancel = true; btnScan.Enabled = false; } else Scan(); };
            foreach (Control c in new Control[] { mode, btnFolder, threshold, btnScan, progressText }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            actions.Dock = DockStyle.Bottom;
            actions.Height = S(56);
            btnMarkCopies = Button("\uE8C8");
            btnMarkCopies.Click += (s, e) => MarkCopies();
            btnClear = Button("\uE894", ButtonKind.Ghost);
            btnClear.Click += (s, e) => { lv.SetAllChecked(false); UpdateButtons(); };
            btnShow = Button("\uEC50");
            btnShow.Click += (s, e) => ShowInExplorer();
            btnDelete = Button("\uE74D", ButtonKind.Danger);
            btnDelete.Click += (s, e) => DoDelete();
            foreach (Control c in new Control[] { btnMarkCopies, btnClear, btnShow, btnDelete }) actions.Controls.Add(c);
            actions.Resize += (s, e) => LayoutActions();

            lv.CheckBoxesEx = true;
            lv.Icons = icons;
            lv.Numeric = new[] { false, true, true, false };
            lv.SortColumn = 1;
            lv.SortDesc = true;
            sorter = lv.ListViewItemSorter;
            lv.CheckChanged += (s, e) => UpdateButtons();
            lv.SelectedIndexChanged += (s, e) => btnShow.Enabled = SelectedFile() != null;
            lv.ItemActivate += (s, e) => ShowInExplorer();
            foreach (int w in new[] { 340, 100, 150, 400 }) lv.Columns.Add("", S(w));
            lv.ColumnWeights = new[] { 0.32f, 0.11f, 0.15f, 0.42f };
            lv.ColumnMins = new[] { 160, 84, 120, 140 };
            lv.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Dock = DockStyle.Fill;
            listHost.Controls.Add(lv);
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(S(16), 0, S(16), 0);
            content.Controls.Add(listHost);
            Controls.Add(content);
            Controls.Add(actions);
            Controls.Add(toolbar);
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            foreach (Control c in Controls) c.BackColor = Theme.Current.Bg;
            listHost.BackColor = Theme.Current.Border;
            lv.ApplyTheme();
        }

        public override void ApplyLanguage()
        {
            mode.SetTexts(new[] { L.T("Большие файлы", "Large files"), L.T("Дубликаты", "Duplicates") });
            threshold.SetTexts(new[] { L.T("от 100 МБ", "100 MB+"), L.T("от 500 МБ", "500 MB+"), L.T("от 1 ГБ", "1 GB+") });
            SetButton(btnMarkCopies, L.T("Отметить копии", "Tick copies"));
            SetButton(btnClear, L.T("Снять отметки", "Clear ticks"));
            SetButton(btnShow, L.T("Показать в проводнике", "Show in Explorer"));
            string[] cols = { L.T("Имя", "Name"), L.T("Размер", "Size"), L.T("Изменён", "Modified"), L.T("Папка", "Folder") };
            for (int i = 0; i < cols.Length; i++) lv.Columns[i].Text = cols[i];
            UpdateFolderButton();
            Render();
            UpdateButtons();
            LayoutToolbar();
        }

        void UpdateFolderButton()
        {
            string p = folder;
            if (p.Length > 30) p = p.Substring(0, 3) + "…" + p.Substring(p.Length - 24);
            SetButton(btnFolder, p);
        }

        public override void ApplyScale()
        {
            toolbar.Height = S(60);
            actions.Height = S(56);
            content.Padding = new Padding(S(16), 0, S(16), 0);
            LayoutToolbar();
            LayoutActions();
        }

        void LayoutToolbar()
        {
            int x = S(16), y = (toolbar.Height - S(32)) / 2;
            foreach (Control c in new Control[] { mode, btnFolder, threshold, btnScan })
            {
                if (c == threshold && mode.SelectedIndex != 0) continue;
                c.Location = new Point(x, y);
                x = c.Right + (c == mode ? S(16) : S(8));
            }
            progressText.SetBounds(x + S(12), y, Math.Max(S(40), toolbar.Width - x - S(28)), S(32));
        }

        void LayoutActions()
        {
            int y = (actions.Height - S(32)) / 2, x = S(16);
            foreach (var b in new[] { btnMarkCopies, btnClear, btnShow })
            {
                if (b == btnMarkCopies && mode.SelectedIndex != 1) continue;
                b.Location = new Point(x, y);
                x = b.Right + S(8);
            }
            btnDelete.Location = new Point(actions.Width - btnDelete.Width - S(16), y);
        }

        public override void RefreshData() { if (!scanning && scanned) Scan(); }

        void PickFolder()
        {
            using (var d = new FolderBrowserDialog { SelectedPath = folder, ShowNewFolderButton = false,
                Description = L.T("Где искать файлы", "Where to look for files") })
            {
                if (d.ShowDialog(Host.Window) != DialogResult.OK || string.IsNullOrEmpty(d.SelectedPath)) return;
                folder = d.SelectedPath;
            }
            UpdateFolderButton();
            LayoutToolbar();
        }

        // ---------- scanning ----------

        void Scan()
        {
            if (scanning || deleting) return;
            if (!Directory.Exists(folder))
            {
                Host.ShowToast(L.T("Папка не найдена: ", "Folder not found: ") + folder, ToastKind.Error);
                return;
            }
            scanning = true;
            cancel = false;
            int m = mode.SelectedIndex;
            long min = Thresholds[threshold.SelectedIndex];
            string root = folder;
            Host.BeginBusy();
            UpdateButtons();
            lv.Loading = true;
            progressText.SetText(L.T("Поиск…", "Searching…"));
            var th = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long last = -1000;
                Action<int> progress = n =>
                {
                    if (sw.ElapsedMilliseconds - last < 250) return;
                    last = sw.ElapsedMilliseconds;
                    Post(() => progressText.SetText(string.Format(L.T("Просмотрено файлов: {0}", "Files checked: {0}"), n.ToString("N0"))));
                };
                List<FileItem> found;
                try
                {
                    found = m == 0
                        ? FileScan.Large(root, min, 500, () => cancel, progress)
                        : FileScan.Duplicates(root, 1L << 20, () => cancel, progress, t => Post(() => progressText.SetText(t)));
                }
                catch { found = new List<FileItem>(); }
                var newIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in found)
                {
                    string key = f.IconPath;
                    if (f.IsHeader || icons.ContainsKey(key) || newIcons.ContainsKey(key)) continue;
                    var bmp = Native.GetTypeIcon(key);
                    if (bmp != null) newIcons[key] = bmp;
                }
                bool cancelled = cancel;
                Post(() => ScanDone(found, newIcons, m, cancelled));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void ScanDone(List<FileItem> found, Dictionary<string, Bitmap> newIcons, int m, bool cancelled)
        {
            foreach (var kv in newIcons) icons[kv.Key] = kv.Value;
            scanning = false;
            scanned = !cancelled;
            Host.EndBusy();
            items = cancelled ? new List<FileItem>() : found;
            shownMode = m;
            Transition.CrossFade(listHost, Render, 280);
            UpdateButtons();
            if (cancelled) progressText.SetText(L.T("Поиск остановлен", "Search stopped"));
            else if (m == 0)
                progressText.SetText(string.Format(L.T("Найдено файлов: {0} · {1}", "Files found: {0} · {1}"), items.Count, L.Size(items.Sum(i => i.Size))));
            else
            {
                var heads = items.Where(i => i.IsHeader).ToList();
                progressText.SetText(string.Format(L.T("Групп дубликатов: {0} · лишние копии занимают {1}", "Duplicate groups: {0} · extra copies take {1}"),
                    heads.Count, L.Size(heads.Sum(h => h.Size * (h.Copies - 1)))));
            }
        }

        void Render()
        {
            bool dup = shownMode == 1;
            lv.SortEnabled = !dup;
            lv.ListViewItemSorter = dup ? null : sorter;
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            var rows = new List<ListViewItem>();
            foreach (var f in items)
            {
                if (f.IsHeader)
                    rows.Add(new ListViewItem(new[]
                    {
                        string.Format(L.T("Одинаковых файлов: {0}", "Identical files: {0}"), f.Copies),
                        L.Size(f.Size), "", L.T("лишние копии: ", "extra copies: ") + L.Size(f.Size * (f.Copies - 1))
                    }) { Tag = f });
                else
                    rows.Add(new ListViewItem(new[] { f.Name, L.Size(f.Size), f.Modified.ToString("g"), f.Dir }) { Tag = f });
            }
            lv.Items.AddRange(rows.ToArray());
            if (!dup) lv.Sort();
            lv.EndUpdate();
            lv.Loading = scanning;
            lv.FitLastColumn();
            lv.EmptyGlyph = scanned ? "\uE930" : (dup ? "\uE8C8" : "\uE721");
            if (scanned) lv.EmptyText = dup ? L.T("Дубликатов не найдено", "No duplicates found") : L.T("Таких больших файлов нет", "No files that large");
            else lv.EmptyText = L.T("Выберите папку и нажмите «Найти»", "Choose a folder and press \"Find\"");
            Host.UpdateStatus();
        }

        // ---------- actions ----------

        FileItem SelectedFile()
        {
            var row = lv.FocusedItem ?? (lv.SelectedItems.Count > 0 ? lv.SelectedItems[0] : null);
            var f = row != null ? row.Tag as FileItem : null;
            return f != null && !f.IsHeader ? f : null;
        }

        void ShowInExplorer()
        {
            var f = SelectedFile();
            if (f == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f.Path + "\""); }
            catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
        }

        void MarkCopies()
        {
            // In each group keep the newest file (groups list it first) and tick the rest.
            var keep = new HashSet<FileItem>(items.Where(i => !i.IsHeader).GroupBy(i => i.Group).Select(g => g.First()));
            foreach (ListViewItem row in lv.Items)
            {
                var f = (FileItem)row.Tag;
                if (!f.IsHeader) lv.SetChecked(row, !keep.Contains(f));
            }
            UpdateButtons();
        }

        List<FileItem> CheckedFiles()
        {
            return items.Where(i => !i.IsHeader && i.Checked).ToList();
        }

        void UpdateButtons()
        {
            bool dupMode = mode.SelectedIndex == 1;
            var sel = CheckedFiles();
            long bytes = sel.Sum(f => f.Size);
            string text = L.T("Удалить в Корзину", "Move to Recycle Bin") + (sel.Count > 0 ? " (" + sel.Count + " · " + L.Size(bytes) + ")" : "");
            if (btnDelete.Text != text) SetButton(btnDelete, text);
            btnDelete.Enabled = sel.Count > 0 && !scanning && !deleting;
            btnClear.Enabled = sel.Count > 0 && !deleting;
            btnMarkCopies.Visible = dupMode;
            btnMarkCopies.Enabled = shownMode == 1 && items.Count > 0 && !deleting;
            btnShow.Enabled = SelectedFile() != null;
            SetButton(btnScan, scanning ? L.T("Остановить", "Stop") : L.T("Найти", "Find"));
            btnScan.Glyph = scanning ? "\uE71A" : "\uE721";
            btnScan.Kind = scanning ? ButtonKind.Normal : ButtonKind.Primary;
            btnScan.Enabled = !deleting;
            btnFolder.Enabled = mode.Enabled = threshold.Enabled = !scanning && !deleting;
            LayoutToolbar();
            LayoutActions();
            Host.UpdateStatus();
        }

        public override string Status
        {
            get
            {
                if (scanning) return L.T("Поиск файлов…", "Searching files…");
                var sel = CheckedFiles();
                string s = L.T("Папка: ", "Folder: ") + folder;
                if (sel.Count > 0) s += string.Format(L.T("  ·  отмечено: {0} ({1})", "  ·  ticked: {0} ({1})"), sel.Count, L.Size(sel.Sum(f => f.Size)));
                return s;
            }
        }

        void DoDelete()
        {
            var sel = CheckedFiles();
            if (sel.Count == 0 || deleting) return;
            string msg = sel.Count == 1
                ? L.T("Переместить ", "Move ") + L.Quote(sel[0].Name) + L.T(" в Корзину?", " to the Recycle Bin?")
                : string.Format(L.T("Переместить отмеченные файлы ({0}, {1}) в Корзину?", "Move the ticked files ({0}, {1}) to the Recycle Bin?"), sel.Count, L.Size(sel.Sum(f => f.Size)));
            if (shownMode == 1)
            {
                int whole = sel.GroupBy(f => f.Group).Count(g => g.Count() == items.Count(i => !i.IsHeader && i.Group == g.Key));
                if (whole > 0)
                    msg += string.Format(L.T("\n\nВнимание: в {0} группах отмечены все копии — от этих файлов не останется ни одной.",
                        "\n\nWarning: in {0} groups every copy is ticked — no copy of those files will remain."), whole);
            }
            msg += L.T("\n\nИх можно будет восстановить из Корзины.", "\n\nThey can be restored from the Recycle Bin.");
            if (!ThemedDialog.Confirm(Host.Window, L.T("Удаление файлов", "Delete files"), msg, L.T("В Корзину", "To Recycle Bin"), "\uE74D", true)) return;

            deleting = true;
            Host.BeginBusy();
            UpdateButtons();
            var th = new Thread(() =>
            {
                Native.MoveToRecycleBin(sel.Select(f => f.Path));
                var gone = sel.Where(f => !File.Exists(f.Path)).ToList();
                History.Add(HistoryKind.Files, gone.Sum(f => f.Size), string.Join(", ", gone.Select(f => f.Name).Take(20)));
                Post(() => DeleteDone(sel, gone));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void DeleteDone(List<FileItem> sel, List<FileItem> gone)
        {
            deleting = false;
            Host.EndBusy();
            var goneSet = new HashSet<FileItem>(gone);
            items.RemoveAll(goneSet.Contains);
            // A duplicate group with a single file left is no longer a group.
            if (shownMode == 1)
            {
                var small = items.Where(i => !i.IsHeader).GroupBy(i => i.Group).Where(g => g.Count() < 2).Select(g => g.Key).ToList();
                var emptied = items.Where(i => i.IsHeader && items.All(o => o.IsHeader || o.Group != i.Group)).Select(i => i.Group);
                var drop = new HashSet<int>(small.Concat(emptied));
                items.RemoveAll(i => drop.Contains(i.Group));
                foreach (var h in items.Where(i => i.IsHeader)) h.Copies = items.Count(i => !i.IsHeader && i.Group == h.Group);
            }
            var rows = lv.Items.Cast<ListViewItem>().Where(r => !items.Contains((FileItem)r.Tag)).ToList();
            lv.FadeOutAndRemove(rows, () =>
            {
                if (shownMode == 1) Render();
                UpdateButtons();
            });
            UpdateButtons();
            int failed = sel.Count - gone.Count;
            string text = L.T("В Корзину перемещено: ", "Moved to the Recycle Bin: ") + gone.Count + " · " + L.Size(gone.Sum(f => f.Size));
            if (failed > 0) text += L.T(" · не удалось: ", " · failed: ") + failed;
            Host.ShowToast(text, failed > 0 && gone.Count == 0 ? ToastKind.Error : ToastKind.Success);
        }
    }
}
