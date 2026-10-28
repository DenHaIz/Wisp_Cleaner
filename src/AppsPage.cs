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
        readonly FlatButton btnUninstall, btnForce, btnStop, btnRefresh, btnClear, btnRegistry;
        readonly SearchBox search = new SearchBox();
        readonly ThemedListView lv = new ThemedListView();
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly ToolTip tip = new ToolTip();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miUninstall, miForce, miEntry, miFolder, miRegistry, miSearch, miCopy;

        List<InstalledApp> apps = new List<InstalledApp>();
        bool loading, storeLoading, uninstalling, stopRequested, loaded;

        public AppsPage(IHost host) : base(host)
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            filter = new Segmented(new[] { "", "", "" }, new[] { "", "", "" }, 0);
            filter.SelectedIndexChanged += (s, e) => Transition.CrossFade(listHost, Render, 200);
            btnUninstall = Button("\uE74D", ButtonKind.Danger);
            btnUninstall.Click += (s, e) => DoUninstall(Checked());
            btnForce = Button("\uE945");
            btnForce.Click += (s, e) => DoForce(Checked());
            btnStop = Button("\uE71A");
            btnStop.Click += (s, e) => { stopRequested = true; btnStop.Enabled = false; };
            btnStop.Visible = false;
            btnClear = Button("\uE894", ButtonKind.Ghost);
            btnClear.Click += (s, e) => { lv.SetAllChecked(false); UpdateButtons(); };
            btnRefresh = Button("\uE72C");
            btnRefresh.Click += (s, e) => RefreshData();
            btnRegistry = Button("\uE90F");
            btnRegistry.Click += (s, e) => DoRegistryClean();
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                Transition.CrossFade(listHost, Render, 160);
            };
            foreach (Control c in new Control[] { filter, btnUninstall, btnForce, btnStop, btnClear, btnRefresh, btnRegistry, search }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            lv.CheckBoxesEx = true;
            lv.Icons = icons;
            lv.Numeric = new[] { false, false, true, true, false, false };
            lv.SortColumn = 0;
            lv.DotColumn = 5;
            lv.CheckChanged += (s, e) => UpdateButtons();
            lv.ItemActivate += (s, e) => OpenLocation(FocusedApp());
            BuildMenu();
            lv.ContextMenuStrip = menu;
            menu.Opening += (s, e) => e.Cancel = !PrepareMenu();
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
            SetButton(btnStop, L.T("Остановить", "Stop"));
            search.SetCue(L.T("Поиск программ", "Search programs"));
            tip.SetToolTip(btnUninstall, L.T("Где возможно — тихо, без окон. Иначе откроется мастер удаления самой программы",
                "Silently where possible; otherwise the program's own uninstaller opens"));
            tip.SetToolTip(btnForce, L.T("Для программ, которые не удаляются: без их деинсталлятора убрать папку, службы, задачи, ярлыки, автозапуск и записи реестра",
                "For programs that will not uninstall: remove their folder, services, tasks, shortcuts, autostart and registry entries without their uninstaller"));
            tip.SetToolTip(btnRegistry, L.T("Найти записи реестра, которые ссылаются на удалённые файлы",
                "Find registry entries that point at deleted files"));
            miUninstall.Text = L.T("Удалить", "Uninstall");
            miForce.Text = L.T("Принудительное удаление…", "Force uninstall…");
            miEntry.Text = L.T("Убрать только запись из списка", "Remove the list entry only");
            miFolder.Text = L.T("Открыть папку программы", "Open the program folder");
            miRegistry.Text = L.T("Показать в редакторе реестра", "Show in Registry Editor");
            miSearch.Text = L.T("Найти в интернете", "Search the web");
            miCopy.Text = L.T("Копировать название", "Copy the name");
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
            int x = S(16), y = (toolbar.Height - S(Theme.ControlH)) / 2;
            // Keep at least 220 px for the search box: the secondary buttons lose their captions first
            // (they keep them as tooltips), in this order.
            var captions = new[]
            {
                Tuple.Create(btnClear, L.T("Снять отметки", "Clear ticks")),
                Tuple.Create(btnRefresh, L.T("Обновить", "Refresh")),
                Tuple.Create(btnRegistry, L.T("Чистка реестра", "Registry clean-up")),
                Tuple.Create(btnForce, L.T("Принудительно", "Force")),
            };
            Func<string, int> textWidth = t => S(8) + TextDraw.MeasureText(t, Theme.UiFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int need = S(16) + filter.Width + S(20) + btnUninstall.PreferredWidth + S(8) + (uninstalling ? btnStop.PreferredWidth + S(8) : 0) + S(16) + S(220) + S(16);
            foreach (var c in captions) need += S(24) + S(16) + textWidth(c.Item2) + S(8);
            foreach (var c in captions)
            {
                bool drop = need > toolbar.Width;
                if (drop) need -= textWidth(c.Item2);
                string want = drop ? "" : c.Item2;
                if (c.Item1.Text != want) SetButton(c.Item1, want);
                if (c.Item1 != btnForce && c.Item1 != btnRegistry) tip.SetToolTip(c.Item1, c.Item2);
            }
            filter.Location = new Point(x, y);
            x = filter.Right + S(20);
            foreach (var b in new[] { btnUninstall, btnForce, btnStop, btnClear, btnRefresh, btnRegistry })
            {
                if (b == btnStop && !uninstalling) continue;
                b.Location = new Point(x, y);
                x = b.Right + S(8);
            }
            int w = Math.Max(S(160), Math.Min(S(300), toolbar.Width - x - S(32)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(Theme.ControlH));
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
            // Keep ticks across a refresh — by the entry itself: copies of one program for different users are separate rows.
            var ticked = new HashSet<string>(apps.Where(a => a.Checked).Select(TickKey), StringComparer.OrdinalIgnoreCase);
            foreach (var a in list) a.Checked = ticked.Contains(TickKey(a));
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

        static string TickKey(InstalledApp a)
        {
            return a.Kind == AppKind.Store ? "store|" + a.PackageFullName : a.Hive + "|" + a.View + "|" + a.KeyPath + "\\" + a.KeyName;
        }

        static string RowStatus(InstalledApp a)
        {
            if (a.StatusText.Length > 0) return a.StatusText;
            if (a.Broken) return L.T("Деинсталлятор не найден", "Uninstaller is missing");
            if (!a.CanRunUninstaller) return L.T("Только принудительно", "Force uninstall only");
            return a.CanQuiet ? L.T("Тихое удаление", "Silent removal") : L.T("Мастер удаления", "Uninstall wizard");
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

        InstalledApp FocusedApp()
        {
            var row = lv.FocusedItem;
            return row != null ? row.Tag as InstalledApp : null;
        }

        void UpdateButtons()
        {
            var sel = Checked();
            string text = sel.Count > 0 ? L.T("Удалить выбранные", "Remove selected") + " (" + sel.Count + ")" : L.T("Удалить выбранные", "Remove selected");
            if (btnUninstall.Text != text) SetButton(btnUninstall, text);
            btnUninstall.Enabled = sel.Count > 0 && !uninstalling;
            btnForce.Enabled = sel.Any(a => a.Kind == AppKind.Win32) && !uninstalling;
            btnClear.Enabled = sel.Count > 0 && !uninstalling;
            btnRefresh.Enabled = !loading && !storeLoading && !uninstalling;
            btnRegistry.Enabled = !uninstalling;
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
                int broken = apps.Count(a => a.Broken);
                if (broken > 0) s += string.Format(L.T("  ·  без деинсталлятора: {0}", "  ·  without uninstaller: {0}"), broken);
                if (sel.Count > 0)
                    s += string.Format(L.T("  ·  отмечено: {0} ({1})", "  ·  ticked: {0} ({1})"), sel.Count, L.Size(sel.Where(a => a.Size > 0).Sum(a => a.Size)));
                return s;
            }
        }

        static string FolderOf(InstalledApp a)
        {
            if (a == null) return null;
            if (a.InstallLocation.Length > 0 && Directory.Exists(a.InstallLocation)) return a.InstallLocation;
            foreach (string cmd in new[] { a.Icon, a.UninstallString })
            {
                string exe = PathCheck.ExePath(cmd);
                if (string.IsNullOrEmpty(exe) || Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (Directory.Exists(dir)) return dir;
                }
                catch { }
            }
            return null;
        }

        void OpenLocation(InstalledApp a)
        {
            string dir = FolderOf(a);
            if (dir == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch { }
        }

        // ---------- context menu (as in Bulk Crap Uninstaller) ----------

        void BuildMenu()
        {
            menu.Renderer = new ThemedRenderer();
            menu.Font = Theme.UiFont;
            menu.Padding = new Padding(0, S(4), 0, S(4));
            Func<string, bool, EventHandler, ToolStripMenuItem> item = (glyph, danger, click) =>
            {
                var mi = new ToolStripMenuItem { Tag = new MenuTag { Glyph = glyph, Danger = danger }, Padding = new Padding(S(20), S(4), 0, S(4)) };
                mi.Click += click;
                menu.Items.Add(mi);
                return mi;
            };
            miUninstall = item("\uE74D", true, (s, e) => DoUninstall(MenuTargets()));
            miForce = item("\uE945", true, (s, e) => DoForce(MenuTargets()));
            miEntry = item("\uE894", false, (s, e) => RemoveEntry(FocusedApp()));
            menu.Items.Add(new ToolStripSeparator());
            miFolder = item("\uE838", false, (s, e) => OpenLocation(FocusedApp()));
            miRegistry = item("\uE90F", false, (s, e) =>
            {
                var a = FocusedApp();
                try { Reg.ShowInRegedit(a.Hive, a.View, a.KeyPath + "\\" + a.KeyName); }
                catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
            });
            miSearch = item("\uE721", false, (s, e) =>
            {
                var a = FocusedApp();
                string q = a.Name + (a.Publisher.Length > 0 && a.Name.IndexOf(a.Publisher, StringComparison.OrdinalIgnoreCase) < 0 ? " " + a.Publisher : "");
                // Opened by the signed-in user's Explorer, so the browser does not get administrator rights.
                try { Secure.StartAsUser("explorer.exe", "\"https://www.google.com/search?q=" + Uri.EscapeDataString(q) + "\"", false); }
                catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
            });
            miCopy = item("\uE8C8", false, (s, e) =>
            {
                try { Clipboard.SetText(FocusedApp().Name); } catch { }
            });
        }

        /// <summary>The ticked programs when the clicked one is among them, otherwise just the clicked one.</summary>
        List<InstalledApp> MenuTargets()
        {
            var a = FocusedApp();
            if (a == null) return new List<InstalledApp>();
            return a.Checked ? Checked() : new List<InstalledApp> { a };
        }

        /// <summary>Sets the menu up for the row under the mouse (or the focused row for Shift+F10); false when there is none.</summary>
        bool PrepareMenu()
        {
            var at = lv.PointToClient(MousePosition);
            var hit = lv.ClientRectangle.Contains(at) ? lv.GetItemAt(at.X, at.Y) : null;
            if (hit == null) hit = lv.FocusedItem;
            if (hit == null) return false;
            lv.FocusedItem = hit;
            if (!hit.Selected)
            {
                lv.SelectedItems.Clear();
                hit.Selected = true;
            }
            var a = hit.Tag as InstalledApp;
            if (a == null) return false;
            bool win32 = a.Kind == AppKind.Win32;
            int n = a.Checked ? Checked().Count : 1;
            miUninstall.Text = n > 1 ? L.T("Удалить отмеченные (" + n + ")", "Uninstall ticked (" + n + ")") : L.T("Удалить", "Uninstall");
            miForce.Text = n > 1 ? L.T("Принудительно удалить отмеченные (" + n + ")…", "Force uninstall ticked (" + n + ")…") : L.T("Принудительное удаление…", "Force uninstall…");
            miUninstall.Enabled = !uninstalling;
            miForce.Enabled = !uninstalling && (win32 || n > 1);
            miEntry.Enabled = !uninstalling && win32;
            miFolder.Enabled = FolderOf(a) != null;
            miRegistry.Enabled = win32;
            // Follows the window's zoom.
            menu.Font = Theme.UiFont;
            menu.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in menu.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(S(20), S(4), 0, S(4));
            return true;
        }

        // ---------- uninstall queue ----------

        void DoUninstall(List<InstalledApp> queue)
        {
            if (queue.Count == 0 || uninstalling) return;
            var runnable = queue.Where(a => a.CanRunUninstaller).ToList();
            int quiet = runnable.Count(a => a.CanQuiet);
            string msg = queue.Count == 1
                ? L.T("Удалить ", "Remove ") + L.Quote(queue[0].Name) + "?"
                : L.T("Удалить выбранные программы (" + queue.Count + ")?", "Remove the selected programs (" + queue.Count + ")?");
            msg += "\n\n" + (quiet == runnable.Count
                ? L.T("Программы удаляются тихо, по очереди.", "Programs are removed silently, one by one.")
                : L.T("Программы удаляются по очереди. Где тихое удаление невозможно (" + (runnable.Count - quiet) + "), откроется мастер удаления самой программы — пройдите его.",
                      "Programs are removed one by one. Where silent removal is not possible (" + (runnable.Count - quiet) + "), the program's own uninstaller opens — follow it."));
            if (runnable.Count < queue.Count)
                msg += L.T("\nУ " + (queue.Count - runnable.Count) + " из них деинсталлятор не запустить — их предложим удалить принудительно.",
                           "\n" + (queue.Count - runnable.Count) + " of them have no uninstaller that can run — they are offered for forced removal.");
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
            var all = apps.ToList();

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
                var keep = Keep.From(all.Where(x => !removed.Contains(x) && x.Kind == AppKind.Win32));
                foreach (var a in removed)
                {
                    try { leftovers.AddRange(Leftovers.Find(a, keep)); } catch { }
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

            if (removed.Count > 0 || failed.Count > 0)
            {
                string text = removed.Count == 1 ? L.Quote(removed[0].Name) + L.T(" удалена", " removed") : L.T("Удалено программ: ", "Programs removed: ") + removed.Count;
                if (failed.Count > 0) text += L.T(" · не удалось: ", " · failed: ") + failed.Count;
                Host.ShowToast(text, failed.Count > 0 ? (removed.Count > 0 ? ToastKind.Info : ToastKind.Error) : ToastKind.Success);
            }

            // What the uninstallers could not remove can be removed by force, together with the leftovers.
            var stuck = failed.Where(a => a.Kind == AppKind.Win32).ToList();
            if (stuck.Count > 0)
            {
                string names = string.Join(", ", stuck.Take(3).Select(a => L.Quote(a.Name))) + (stuck.Count > 3 ? " (+" + (stuck.Count - 3) + ")" : "");
                if (ThemedDialog.Confirm(Host.Window, L.T("Программа не удалилась", "A program was not removed"),
                        L.T("Штатно не удалось удалить: " + names + ".\n\nУдалить принудительно? Wisp Cleaner найдёт папку программы, её службы, задачи, ярлыки, автозапуск и записи реестра и покажет список перед удалением.",
                            "Could not uninstall: " + names + ".\n\nForce the removal? Wisp Cleaner finds the program's folder, services, tasks, shortcuts, autostart and registry entries and shows the list before removing."),
                        L.T("Найти и удалить", "Find and remove"), "\uE945", true))
                {
                    ScanForce(stuck, leftovers);
                    return;
                }
            }
            if (leftovers.Count > 0) ShowLeftovers(leftovers, null);
        }

        // ---------- forced removal ----------

        void DoForce(List<InstalledApp> targets)
        {
            if (uninstalling) return;
            var list = targets.Where(a => a.Kind == AppKind.Win32).ToList();
            if (list.Count == 0)
            {
                Host.ShowToast(L.T("Принудительное удаление — для классических программ. Приложения Microsoft Store удаляются обычным способом.",
                                   "Forced removal is for desktop programs. Microsoft Store apps are removed the usual way."), ToastKind.Info);
                return;
            }
            string what = list.Count == 1 ? L.Quote(list[0].Name) : L.T("выбранных программ (" + list.Count + ")", "the selected programs (" + list.Count + ")");
            string msg = L.T("Принудительное удаление " + what + " — без " + (list.Count == 1 ? "её" : "их") + " деинсталлятора.\n\nWisp Cleaner найдёт папку программы, её данные, службы, задачи планировщика, ярлыки, автозапуск и записи реестра и покажет список: удалено будет только отмеченное. Процессы программы будут завершены, папки уйдут в Корзину, записи реестра сохранятся в резервную копию.",
                             "Forced removal of " + what + " — without " + (list.Count == 1 ? "its" : "their") + " uninstaller.\n\nWisp Cleaner finds the program's folder, data, services, scheduled tasks, shortcuts, autostart and registry entries and shows the list: only the ticked items are removed. The program's processes are ended, folders go to the Recycle Bin, registry entries are backed up.");
            if (!ThemedDialog.Confirm(Host.Window, L.T("Принудительное удаление", "Forced removal"), msg, L.T("Найти", "Find"), "\uE945", true)) return;
            ScanForce(list, new List<Leftover>());
        }

        /// <summary>Collects everything of the programs in the background, then shows it together with other leftovers.</summary>
        void ScanForce(List<InstalledApp> list, List<Leftover> extra)
        {
            uninstalling = true;
            foreach (var a in list) { a.StatusText = L.T("Поиск следов…", "Looking for traces…"); a.Failed = false; UpdateRow(a); }
            UpdateButtons();
            Host.BeginBusy();
            var keep = Keep.From(apps.Where(a => !list.Contains(a) && a.Kind == AppKind.Win32).ToList());
            var th = new Thread(() =>
            {
                var found = new List<Leftover>();
                foreach (var a in list)
                {
                    try { found.AddRange(ForceUninstall.Scan(a, keep)); } catch { }
                }
                var paths = new HashSet<string>(found.Select(f => f.Kind + "|" + f.Path + "|" + f.ValueName), StringComparer.OrdinalIgnoreCase);
                found.AddRange(extra.Where(x => !paths.Contains(x.Kind + "|" + x.Path + "|" + x.ValueName)));
                Post(() =>
                {
                    uninstalling = false;
                    Host.EndBusy();
                    foreach (var a in list) { a.StatusText = ""; UpdateRow(a); }
                    UpdateButtons();
                    ShowLeftovers(found, list);
                });
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        /// <summary>
        /// Lets the user review and remove leftovers; forced lists the programs being removed by force,
        /// whose rows go away once their entry is gone.
        /// </summary>
        void ShowLeftovers(List<Leftover> items, List<InstalledApp> forced)
        {
            bool force = forced != null && forced.Count > 0;
            string title = force ? L.T("Принудительное удаление", "Forced removal") : L.T("Остатки удалённых программ", "Leftovers of removed programs");
            string head = force
                ? L.T("Найдено всё, что связано с программой. Отмеченное будет удалено: процессы программы завершатся, папки и ярлыки уйдут в Корзину, службы и задачи будут удалены, записи реестра — с резервной копией. Неотмеченное — то, в чём нет уверенности: проверьте сами.",
                      "Everything related to the program was found. The ticked items are removed: the program's processes are ended, folders and shortcuts go to the Recycle Bin, services and tasks are deleted, registry entries are backed up. Unticked items are uncertain: check them yourself.")
                : L.T("Эти папки и разделы реестра остались после удаления. Отмеченное будет убрано: папки — в Корзину, разделы реестра — с резервной копией.",
                      "These folders and registry keys were left behind. The ticked ones are removed: folders to the Recycle Bin, registry keys with a backup copy.");
            LeftoversForm.ShowFor(Host.Window, Host, title, head, L.T("Программа", "Program"), items, HistoryKind.Apps,
                force ? L.T("Принудительно: ", "Forced: ") + string.Join(", ", forced.Select(a => a.Name)) : L.T("Остатки: ", "Leftovers: "),
                RemovalStarted,
                () =>
                {
                    if (!force) { RemovalDone(); return; }
                    // The rows of programs whose entry is gone fade out; the rest stay.
                    var th = new Thread(() =>
                    {
                        var gone = forced.Where(a => !a.StillInstalled()).ToList();
                        Post(() =>
                        {
                            foreach (var a in gone) AfterOne(a, true);
                            RemovalDone();
                        });
                    }) { IsBackground = true };
                    th.Start();
                });
        }

        /// <summary>While a removal runs nothing else may start (another removal, a refresh, the registry clean-up).</summary>
        void RemovalStarted()
        {
            uninstalling = true;
            UpdateButtons();
        }

        void RemovalDone()
        {
            uninstalling = false;
            UpdateButtons();
            Host.UpdateStatus();
        }

        /// <summary>Removes only the "Apps &amp; features" entry (backed up): for entries of programs that are long gone.</summary>
        void RemoveEntry(InstalledApp a)
        {
            if (a == null || a.Kind != AppKind.Win32 || uninstalling) return;
            if (!ThemedDialog.Confirm(Host.Window, L.T("Убрать запись", "Remove the entry"),
                    L.T("Убрать " + L.Quote(a.Name) + " из списка программ? Сама программа и её файлы не удаляются — только запись в реестре:\n" + a.RegistryPath + "\n\nРезервная копия записи сохранится в папке резервных копий.",
                        "Remove " + L.Quote(a.Name) + " from the programs list? The program and its files stay — only the registry entry goes:\n" + a.RegistryPath + "\n\nA backup of the entry is kept in the backup folder."),
                    L.T("Убрать", "Remove"), "\uE894", true)) return;
            var entry = new Leftover { Kind = LeftoverKind.RegKey, Hive = a.Hive, View = a.View, SubKey = a.KeyPath + "\\" + a.KeyName, Path = a.RegistryPath, Checked = true };
            RemovalStarted();
            Host.BeginBusy();
            // reg.exe export can take a while: not on the window's thread.
            var th = new Thread(() =>
            {
                RemoveResult r;
                try { r = LeftoverRemover.Remove(new List<Leftover> { entry }); }
                catch (Exception ex) { r = new RemoveResult(); r.Errors.Add(ex.Message); }
                Post(() =>
                {
                    Host.EndBusy();
                    if (r.Errors.Count > 0) Host.ShowToast(L.T("Не удалось убрать запись: ", "Could not remove the entry: ") + r.Errors[0], ToastKind.Error);
                    else
                    {
                        AfterOne(a, true);
                        Host.ShowToast(L.Quote(a.Name) + L.T(" убрана из списка", " removed from the list"), ToastKind.Success);
                    }
                    RemovalDone();
                });
            }) { IsBackground = false };
            th.Start();
        }

        // ---------- registry clean-up ----------

        void DoRegistryClean()
        {
            if (uninstalling) return;
            uninstalling = true;
            UpdateButtons();
            Host.BeginBusy();
            var th = new Thread(() =>
            {
                List<Leftover> found;
                try { found = RegistryCleaner.Scan(() => false); }
                catch { found = new List<Leftover>(); }
                Post(() =>
                {
                    uninstalling = false;
                    Host.EndBusy();
                    UpdateButtons();
                    if (found.Count == 0)
                    {
                        Host.ShowToast(L.T("Реестр в порядке: ссылок на удалённые файлы не найдено", "The registry is fine: no entries point at deleted files"), ToastKind.Success);
                        return;
                    }
                    LeftoversForm.ShowFor(Host.Window, Host, L.T("Чистка реестра", "Registry clean-up"),
                        L.T("Эти записи реестра ссылаются на файлы и папки, которых больше нет на диске. Отмеченное будет удалено; перед удалением всё сохраняется в резервную копию (.reg — двойной щелчок вернёт запись).",
                            "These registry entries point at files and folders that no longer exist. The ticked ones are removed; everything is backed up first (.reg — double-click to bring an entry back)."),
                        L.T("Ссылается на", "Points at"), found, HistoryKind.Apps, L.T("Чистка реестра: ", "Registry clean-up: "),
                        RemovalStarted,
                        () =>
                        {
                            RemovalDone();
                            if (found.Any(f => f.Checked && f.Kind == LeftoverKind.RegKey && f.SubKey.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase) >= 0)) RefreshData();
                        });
                });
            }) { IsBackground = true };
            th.Start();
        }
    }

    /// <summary>
    /// A list of things to remove — leftovers of removed programs, a forced removal or registry entries —
    /// with ticks; removal runs in the background and ends with a toast.
    /// </summary>
    class LeftoversForm : ThemedWindow
    {
        readonly ThemedListView lv = new ThemedListView();
        readonly FlatButton btnRemove;

        LeftoversForm(string title, string headText, string detailHeader, List<Leftover> items) : base(title, new Size(Theme.S(960), Theme.S(540)))
        {
            var head = new Label
            {
                Dock = DockStyle.Top, Height = S(64), ForeColor = Theme.Current.Text, Font = Theme.UiFont, UseMnemonic = false, Text = headText,
            };
            var frame = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Current.Border };
            lv.Dock = DockStyle.Fill;
            lv.CheckBoxesEx = true;
            lv.IconColumn = false;
            // Registry-only lists (the registry clean-up) have nothing to measure: no size column there.
            Func<Leftover, bool> sized = l => l.Kind == LeftoverKind.Folder || l.Kind == LeftoverKind.File;
            bool sizes = items.Any(sized);
            lv.Columns.Add(L.T("Расположение", "Location"), S(440));
            lv.Columns.Add(L.T("Что это", "What it is"), S(190));
            if (sizes) lv.Columns.Add(L.T("Размер", "Size"), S(90));
            lv.Columns.Add(detailHeader, S(180));
            lv.ColumnWeights = sizes ? new[] { 0.46f, 0.22f, 0.1f, 0.22f } : new[] { 0.42f, 0.24f, 0.34f };
            lv.ColumnMins = sizes ? new[] { 220, 120, 72, 120 } : new[] { 220, 120, 160 };
            lv.Numeric = sizes ? new[] { false, false, true, false } : new[] { false, false, false };
            lv.ListViewItemSorter = null;
            foreach (var l in items.OrderBy(i => i.App, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => (int)i.Kind))
            {
                var cells = sizes ? new[] { l.Path, l.Reason, sized(l) ? L.Size(l.Size) : "", l.App } : new[] { l.Path, l.Reason, l.App };
                l.SizeColumn = sizes ? 2 : -1;
                lv.Items.Add(new ListViewItem(cells) { Tag = l });
            }
            lv.CheckChanged += (s, e) => btnRemove.Enabled = items.Any(i => i.Checked);
            frame.Controls.Add(lv);
            Body.Controls.Add(frame);
            Body.Controls.Add(head);
            AddButton(L.T("Оставить", "Keep"), "", ButtonKind.Normal, DialogResult.Cancel);
            btnRemove = AddButton(L.T("Убрать отмеченное", "Remove ticked"), "\uE74D", ButtonKind.Primary, DialogResult.OK);
            btnRemove.Enabled = items.Any(i => i.Checked);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            lv.FitLastColumn();
        }

        /// <summary>
        /// Shows the list; on "Remove" asks about programs that would be closed, then removes in the background.
        /// started runs when the removal begins, done when it has ended (neither when the user keeps everything).
        /// </summary>
        public static void ShowFor(Form owner, IHost host, string title, string headText, string detailHeader, List<Leftover> items,
                                   HistoryKind kind, string historyPrefix, Action started, Action done)
        {
            List<Leftover> chosen;
            using (var f = new LeftoversForm(title, headText, detailHeader, items))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return;
                chosen = items.Where(i => i.Checked).ToList();
            }
            if (chosen.Count == 0) return;
            var running = LeftoverRemover.RunningUnder(chosen);
            if (running.Count > 0 && !ThemedDialog.Confirm(owner, L.T("Программы будут закрыты", "Programs will be closed"),
                    L.T("Из удаляемых папок сейчас запущено — несохранённая работа в них пропадёт:\n\n", "These run from the folders being removed — unsaved work in them is lost:\n\n") +
                    string.Join("\n", running.Take(30)) + (running.Count > 30 ? "\n…" : ""),
                    L.T("Закрыть и удалить", "Close and remove"), "", true)) return;
            if (started != null) started();
            host.BeginBusy();
            var th = new Thread(() =>
            {
                RemoveResult r;
                try { r = LeftoverRemover.Remove(chosen); }
                catch (Exception ex)
                {
                    r = new RemoveResult();
                    r.Errors.Add(ex.Message);
                }
                Action finish = () =>
                {
                    host.EndBusy();
                    if (r.Done > 0)
                        History.Add(kind, r.Freed, historyPrefix.EndsWith(": ")
                            ? historyPrefix + string.Join(", ", chosen.Select(c => c.Path).Take(20)) + (chosen.Count > 20 ? " …" : "")
                            : historyPrefix + L.T(" (убрано: ", " (removed: ") + r.Done + ")");
                    string text;
                    if (r.Errors.Count > 0)
                        text = L.T("Убрано: ", "Removed: ") + r.Done + L.T(" · не удалось: ", " · failed: ") + r.Errors.Count + " — " + r.Errors[0];
                    else
                        text = L.T("Убрано: ", "Removed: ") + r.Done + (r.Freed > 0 ? " · " + L.Size(r.Freed) : "");
                    if (r.Reboot > 0) text += L.T(" · часть файлов занята и удалится после перезагрузки", " · some files are in use and go after a restart");
                    host.ShowToast(text, r.Errors.Count > 0 ? (r.Done > 0 ? ToastKind.Info : ToastKind.Error) : ToastKind.Success);
                    if (done != null) done();
                };
                try { owner.BeginInvoke(finish); } catch (InvalidOperationException) { }
            }) { IsBackground = false };   // closing the window does not cut a removal in half
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }
    }
}
