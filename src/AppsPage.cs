using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Manual clean-up of programs: tick the unneeded ones and remove them with one button.</summary>
    class AppsPage : Page
    {
        readonly Panel toolbar = new BufferedPanel(), listHost = new BufferedPanel(), content = new BufferedPanel();
        readonly Segmented filter;
        readonly FlatButton btnUninstall, btnStop, btnRefresh, btnClear;
        readonly SearchBox search = new SearchBox();
        readonly ThemedListView lv = new ThemedListView();
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly ToolTip tip = new ToolTip();

        List<InstalledApp> apps = new List<InstalledApp>();
        bool loading, storeLoading, uninstalling, stopRequested, loaded;

        public AppsPage(IHost host) : base(host)
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            filter = new Segmented(new[] { "", "", "" }, new[] { "", "", "" }, 0);
            filter.SelectedIndexChanged += (s, e) => Transition.CrossFade(listHost, Render, 200);
            btnUninstall = Button("\uE74D", ButtonKind.Danger);
            btnUninstall.Click += (s, e) => DoUninstall();
            btnStop = Button("\uE71A");
            btnStop.Click += (s, e) => { stopRequested = true; btnStop.Enabled = false; };
            btnStop.Visible = false;
            btnClear = Button("\uE894", ButtonKind.Ghost);
            btnClear.Click += (s, e) => { lv.SetAllChecked(false); UpdateButtons(); };
            btnRefresh = Button("\uE72C");
            btnRefresh.Click += (s, e) => RefreshData();
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                Transition.CrossFade(listHost, Render, 160);
            };
            foreach (Control c in new Control[] { filter, btnUninstall, btnStop, btnClear, btnRefresh, search }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            lv.CheckBoxesEx = true;
            lv.Icons = icons;
            lv.Numeric = new[] { false, false, true, true, false, false };
            lv.SortColumn = 0;
            lv.CheckChanged += (s, e) => UpdateButtons();
            lv.ItemActivate += (s, e) => OpenLocation();
            foreach (int w in new[] { 300, 200, 100, 120, 130, 200 }) lv.Columns.Add("", S(w));
            lv.ColumnWeights = new[] { 0.29f, 0.2f, 0.1f, 0.12f, 0.13f, 0.16f };
            lv.ColumnMins = new[] { 180, 120, 72, 96, 96, 120 };
            lv.Loading = true;
            lv.DefaultGlyph = "\uE71D";
            lv.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Controls.Add(lv);

            content.Padding = new Padding(S(16), 0, S(16), S(4));
            content.Dock = DockStyle.Fill;
            content.Controls.Add(listHost);
            listHost.Dock = DockStyle.Fill;
            Controls.Add(content);
            Controls.Add(toolbar);
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            foreach (Control c in Controls) c.BackColor = Theme.Current.Bg;
            listHost.BackColor = Theme.Current.Border;
            search.ApplyTheme();
            lv.ApplyTheme();
        }

        public override void ApplyLanguage()
        {
            filter.SetTexts(new[] { L.T("Все", "All"), L.T("Классические", "Desktop"), "Microsoft Store" });
            SetButton(btnRefresh, L.T("Обновить", "Refresh"));
            SetButton(btnStop, L.T("Остановить", "Stop"));
            SetButton(btnClear, L.T("Снять отметки", "Clear ticks"));
            search.SetCue(L.T("Поиск программ", "Search programs"));
            tip.SetToolTip(btnUninstall, L.T("Где возможно — тихо, без окон. Иначе откроется мастер удаления самой программы",
                "Silently where possible; otherwise the program's own uninstaller opens"));
            string[] cols = { L.T("Программа", "Program"), L.T("Издатель", "Publisher"), L.T("Размер", "Size"),
                              L.T("Установлена", "Installed"), L.T("Тип", "Type"), L.T("Состояние", "Status") };
            for (int i = 0; i < cols.Length; i++) lv.Columns[i].Text = cols[i];
            if (!loaded) lv.EmptyText = L.T("Загрузка списка программ…", "Loading programs…");
            else Render();
            UpdateButtons();
            LayoutToolbar();
        }

        public override void ApplyScale()
        {
            toolbar.Height = S(60);
            content.Padding = new Padding(S(16), 0, S(16), S(4));
            LayoutToolbar();
        }

        void LayoutToolbar()
        {
            int x = S(16), y = (toolbar.Height - S(32)) / 2;
            // Keep at least 220 px for the search box: drop the "clear ticks" caption first.
            string clearText = L.T("Снять отметки", "Clear ticks");
            int need = S(16) + filter.Width + S(20) + btnUninstall.PreferredWidth + S(8) + (uninstalling ? btnStop.PreferredWidth + S(8) : 0) +
                       (S(24) + S(24) + TextRenderer.MeasureText(clearText, Theme.UiFont, Size.Empty, TextFormatFlags.NoPadding).Width) + S(8) + btnRefresh.PreferredWidth + S(16) + S(220) + S(16);
            string want = need > toolbar.Width ? "" : clearText;
            if (btnClear.Text != want) SetButton(btnClear, want);
            tip.SetToolTip(btnClear, clearText);
            filter.Location = new Point(x, y);
            x = filter.Right + S(20);
            foreach (var b in new[] { btnUninstall, btnStop, btnClear, btnRefresh })
            {
                if (b == btnStop && !uninstalling) continue;
                b.Location = new Point(x, y);
                x = b.Right + S(8);
            }
            int w = Math.Max(S(160), Math.Min(S(300), toolbar.Width - x - S(32)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(32));
        }

        public override void FocusSearch() { search.FocusBox(); }

        protected override void FirstActivate() { Load(); }

        public override void RefreshData()
        {
            if (!uninstalling) Load();
        }

        // ---------- loading ----------

        void Load()
        {
            if (loading || storeLoading) return;
            loading = storeLoading = true;
            Host.BeginBusy();
            UpdateButtons();
            var known = new HashSet<string>(icons.Keys, StringComparer.OrdinalIgnoreCase);
            var th = new Thread(() =>
            {
                var win32 = new List<InstalledApp>();
                try { win32 = Win32Apps.Load(); } catch { }
                var newIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in win32)
                {
                    if (a.Icon == null || known.Contains(a.Icon) || newIcons.ContainsKey(a.Icon)) continue;
                    try
                    {
                        var bmp = Win32Apps.LoadIcon(a.Icon);
                        if (bmp != null) newIcons[a.Icon] = bmp;
                    }
                    catch { }
                }
                Post(() => ApplyData(win32, newIcons, AppKind.Win32));

                var store = new List<InstalledApp>();
                try { store = StoreApps.Load(); } catch { }
                var storeIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in store)
                {
                    if (a.Icon == null || known.Contains(a.Icon) || storeIcons.ContainsKey(a.Icon)) continue;
                    var bmp = StoreApps.LoadLogo(a.Icon);
                    if (bmp != null) storeIcons[a.Icon] = bmp;
                }
                Post(() => ApplyData(store, storeIcons, AppKind.Store));

                // Sizes that the registry does not report: measure the install folder.
                foreach (var a in win32.Concat(store))
                {
                    if (a.Size >= 0 || a.InstallLocation.Length == 0) continue;
                    long size = 0;
                    try { size = FileOps.DirSize(a.InstallLocation); } catch { }
                    if (size <= 0) continue;
                    var app = a;
                    Post(() => { app.Size = size; UpdateRow(app); });
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void ApplyData(List<InstalledApp> list, Dictionary<string, Bitmap> newIcons, AppKind kind)
        {
            foreach (var kv in newIcons) icons[kv.Key] = kv.Value;
            // Keep ticks across a refresh.
            var ticked = new HashSet<string>(apps.Where(a => a.Checked).Select(a => a.Kind + "|" + a.Name));
            foreach (var a in list) a.Checked = ticked.Contains(a.Kind + "|" + a.Name);
            apps = apps.Where(a => a.Kind != kind).Concat(list).ToList();
            if (kind == AppKind.Win32) loading = false;
            else
            {
                storeLoading = false;
                Host.EndBusy();
            }
            loaded = true;
            Transition.CrossFade(listHost, Render, kind == AppKind.Win32 ? 300 : 200);
            UpdateButtons();
        }

        static string RowStatus(InstalledApp a)
        {
            return a.StatusText.Length > 0 ? a.StatusText : (a.CanQuiet ? L.T("Тихое удаление", "Silent removal") : L.T("Мастер удаления", "Uninstall wizard"));
        }

        string[] Cells(InstalledApp a)
        {
            return new[]
            {
                a.Name, a.Publisher, a.Size > 0 ? L.Size(a.Size) : "",
                a.InstallDate.HasValue ? a.InstallDate.Value.ToString("d") : "", a.KindText, RowStatus(a)
            };
        }

        void Render()
        {
            string q = search.Text.Trim();
            int f = filter.SelectedIndex;
            var rows = new List<ListViewItem>();
            foreach (var a in apps)
            {
                if (f == 1 && a.Kind != AppKind.Win32) continue;
                if (f == 2 && a.Kind != AppKind.Store) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    a.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new ListViewItem(Cells(a)) { Tag = a });
            }
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            lv.Items.AddRange(rows.ToArray());
            lv.Sort();
            lv.EndUpdate();
            lv.Loading = !loaded || (f == 2 && storeLoading);
            lv.FitLastColumn();
            lv.EmptyGlyph = q.Length > 0 ? "\uE721" : "\uE71D";
            if (q.Length > 0) lv.EmptyText = L.T("Ничего не найдено по запросу ", "Nothing matches ") + L.Quote(q);
            else if (f == 2 && storeLoading) lv.EmptyText = L.T("Загрузка приложений Microsoft Store…", "Loading Microsoft Store apps…");
            else lv.EmptyText = L.T("Программ нет", "No programs");
            Host.UpdateStatus();
        }

        ListViewItem RowOf(InstalledApp a)
        {
            foreach (ListViewItem it in lv.Items) if (it.Tag == a) return it;
            return null;
        }

        void UpdateRow(InstalledApp a)
        {
            var row = RowOf(a);
            if (row == null) return;
            var cells = Cells(a);
            for (int i = 0; i < cells.Length; i++)
                if (row.SubItems[i].Text != cells[i]) row.SubItems[i].Text = cells[i];
        }

        List<InstalledApp> Checked()
        {
            return apps.Where(a => a.Checked).ToList();
        }

        void UpdateButtons()
        {
            var sel = Checked();
            string text = sel.Count > 0 ? L.T("Удалить выбранные", "Remove selected") + " (" + sel.Count + ")" : L.T("Удалить выбранные", "Remove selected");
            if (btnUninstall.Text != text) SetButton(btnUninstall, text);
            btnUninstall.Enabled = sel.Count > 0 && !uninstalling;
            btnClear.Enabled = sel.Count > 0 && !uninstalling;
            btnRefresh.Enabled = !loading && !storeLoading && !uninstalling;
            filter.Enabled = !uninstalling;
            LayoutToolbar();
            Host.UpdateStatus();
        }

        public override string Status
        {
            get
            {
                if (!loaded) return L.T("Загрузка…", "Loading…");
                var sel = Checked();
                string s = string.Format(L.T("Программ: {0}  ·  классических: {1}  ·  из Microsoft Store: {2}", "Programs: {0}  ·  desktop: {1}  ·  Microsoft Store: {2}"),
                    apps.Count, apps.Count(a => a.Kind == AppKind.Win32), storeLoading ? "…" : apps.Count(a => a.Kind == AppKind.Store).ToString());
                if (sel.Count > 0)
                    s += string.Format(L.T("  ·  отмечено: {0} ({1})", "  ·  ticked: {0} ({1})"), sel.Count, L.Size(sel.Where(a => a.Size > 0).Sum(a => a.Size)));
                return s;
            }
        }

        void OpenLocation()
        {
            var row = lv.FocusedItem;
            var a = row != null ? row.Tag as InstalledApp : null;
            if (a == null || a.InstallLocation.Length == 0 || !Directory.Exists(a.InstallLocation)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + a.InstallLocation + "\""); }
            catch { }
        }

        // ---------- uninstall queue ----------

        void DoUninstall()
        {
            var queue = Checked();
            if (queue.Count == 0 || uninstalling) return;
            int quiet = queue.Count(a => a.CanQuiet);
            string msg = queue.Count == 1
                ? L.T("Удалить ", "Remove ") + L.Quote(queue[0].Name) + "?"
                : L.T("Удалить выбранные программы (" + queue.Count + ")?", "Remove the selected programs (" + queue.Count + ")?");
            msg += "\n\n" + (quiet == queue.Count
                ? L.T("Всё будет удалено тихо, по очереди.", "Everything is removed silently, one by one.")
                : L.T("Программы удаляются по очереди. Где тихое удаление невозможно (" + (queue.Count - quiet) + "), откроется мастер удаления самой программы — пройдите его.",
                      "Programs are removed one by one. Where silent removal is not possible (" + (queue.Count - quiet) + "), the program's own uninstaller opens — follow it."));
            msg += L.T("\nПосле этого Wisp Cleaner предложит убрать оставшиеся папки и записи реестра.", "\nAfterwards Wisp Cleaner offers to remove leftover folders and registry keys.");
            if (!ThemedDialog.Confirm(Host.Window, L.T("Удаление программ", "Remove programs"), msg, L.T("Удалить", "Remove"), "\uE74D", true)) return;

            uninstalling = true;
            stopRequested = false;
            btnStop.Visible = true;
            btnStop.Enabled = true;
            btnStop.Appear();
            foreach (var a in queue) { a.StatusText = L.T("В очереди", "Queued"); a.Failed = false; UpdateRow(a); }
            UpdateButtons();
            Host.BeginBusy();
            var locations = apps.Select(x => Tuple.Create(x, x.InstallLocation)).ToList();

            var th = new Thread(() =>
            {
                var removed = new List<InstalledApp>();
                var failed = new List<InstalledApp>();
                foreach (var a in queue)
                {
                    if (stopRequested) break;
                    var app = a;
                    Post(() => { app.StatusText = L.T("Удаление…", "Removing…"); UpdateRow(app); });
                    bool ok;
                    try { ok = app.Kind == AppKind.Store ? StoreApps.Uninstall(app) : Win32Apps.Uninstall(app, () => stopRequested); }
                    catch { ok = false; }
                    if (ok) removed.Add(app); else failed.Add(app);
                    Post(() => AfterOne(app, ok));
                }
                // Leftovers are looked for only after the uninstallers are done with their files.
                var leftovers = new List<Leftover>();
                var stillThere = locations.Where(t => !removed.Contains(t.Item1) && t.Item2.Length > 0).Select(t => t.Item2).ToList();
                foreach (var a in removed)
                {
                    try { leftovers.AddRange(Leftovers.Find(a, stillThere)); } catch { }
                }
                Post(() => QueueDone(queue, removed, failed, leftovers));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void AfterOne(InstalledApp a, bool ok)
        {
            var row = RowOf(a);
            if (ok)
            {
                a.Checked = false;
                apps.Remove(a);
                if (row != null) lv.FadeOutAndRemove(new List<ListViewItem> { row }, () => Host.UpdateStatus());
            }
            else
            {
                a.Failed = true;
                a.StatusText = L.T("Не удалено", "Not removed");
                UpdateRow(a);
                if (row != null) lv.Flash(new[] { row }, Theme.Current.Danger);
            }
        }

        void QueueDone(List<InstalledApp> queue, List<InstalledApp> removed, List<InstalledApp> failed, List<Leftover> leftovers)
        {
            uninstalling = false;
            btnStop.Visible = false;
            Host.EndBusy();
            foreach (var a in queue.Where(x => !removed.Contains(x) && !failed.Contains(x)))
            {
                a.StatusText = "";
                UpdateRow(a);
            }
            UpdateButtons();
            if (removed.Count > 0)
                History.Add(HistoryKind.Apps, removed.Where(a => a.Size > 0).Sum(a => a.Size), string.Join(", ", removed.Select(a => a.Name)));

            string text = removed.Count == 1 ? L.Quote(removed[0].Name) + L.T(" удалена", " removed") : L.T("Удалено программ: ", "Programs removed: ") + removed.Count;
            if (failed.Count > 0) text += L.T(" · не удалось: ", " · failed: ") + failed.Count;
            Host.ShowToast(text, failed.Count > 0 ? (removed.Count > 0 ? ToastKind.Info : ToastKind.Error) : ToastKind.Success);

            if (leftovers.Count > 0) LeftoversForm.ShowFor(Host.Window, leftovers, Host);
        }
    }

    /// <summary>"These were left behind" — folders and registry keys of removed programs, all ticked.</summary>
    class LeftoversForm : ThemedWindow
    {
        readonly ThemedListView lv = new ThemedListView();
        readonly List<Leftover> items;
        readonly FlatButton btnRemove;

        LeftoversForm(List<Leftover> items) : base(L.T("Остатки удалённых программ", "Leftovers of removed programs"), new Size(Theme.S(860), Theme.S(480)))
        {
            this.items = items;
            var head = new Label
            {
                Dock = DockStyle.Top, Height = S(48), ForeColor = Theme.Current.Text, Font = Theme.UiFont, UseMnemonic = false,
                Text = L.T("Эти папки и разделы реестра остались после удаления. Отмеченное будет убрано: папки — в Корзину, разделы реестра — с резервной копией.",
                           "These folders and registry keys were left behind. The ticked ones are removed: folders to the Recycle Bin, registry keys with a backup copy."),
            };
            var frame = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Current.Border };
            lv.Dock = DockStyle.Fill;
            lv.CheckBoxesEx = true;
            lv.IconColumn = false;
            lv.Columns.Add(L.T("Расположение", "Location"), S(460));
            lv.Columns.Add(L.T("Тип", "Type"), S(130));
            lv.Columns.Add(L.T("Размер", "Size"), S(100));
            lv.Columns.Add(L.T("Программа", "Program"), S(140));
            lv.ListViewItemSorter = null;
            foreach (var l in items)
                lv.Items.Add(new ListViewItem(new[] { l.Path, l.IsRegistry ? L.T("Реестр", "Registry") : L.T("Папка", "Folder"), l.IsRegistry ? "" : L.Size(l.Size), l.App }) { Tag = l });
            lv.CheckChanged += (s, e) => btnRemove.Enabled = items.Any(i => i.Checked);
            frame.Controls.Add(lv);
            Body.Controls.Add(frame);
            Body.Controls.Add(head);
            AddButton(L.T("Оставить", "Keep"), "", ButtonKind.Normal, DialogResult.Cancel);
            btnRemove = AddButton(L.T("Убрать отмеченное", "Remove ticked"), "\uE74D", ButtonKind.Primary, DialogResult.OK);
        }

        public static void ShowFor(Form owner, List<Leftover> items, IHost host)
        {
            List<Leftover> chosen;
            using (var f = new LeftoversForm(items))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return;
                chosen = items.Where(i => i.Checked).ToList();
            }
            if (chosen.Count == 0) return;
            var errors = new List<string>();
            int done = Leftovers.Remove(chosen, errors);
            long bytes = chosen.Where(c => !c.IsRegistry).Sum(c => c.Size);
            if (done > 0) History.Add(HistoryKind.Apps, bytes, L.T("Остатки: ", "Leftovers: ") + string.Join(", ", chosen.Select(c => c.Path)));
            if (errors.Count > 0) host.ShowToast(L.T("Не удалось убрать: ", "Could not remove: ") + errors[0] + (errors.Count > 1 ? " (+" + (errors.Count - 1) + ")" : ""), ToastKind.Error);
            else host.ShowToast(L.T("Остатки убраны: ", "Leftovers removed: ") + done + (bytes > 0 ? " · " + L.Size(bytes) : ""), ToastKind.Success);
        }
    }
}
