using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Per-browser, per-profile clean-up (cache, cookies, history, sessions) and the extension list.</summary>
    class BrowsersPage : Page
    {
        class Target
        {
            public BrowserProfile Profile;
            public BrowserData What;
        }

        readonly Panel toolbar = new BufferedPanel();
        readonly FlatButton btnClean, btnRefresh, btnOpenExt;
        readonly StatusText note = new StatusText();
        readonly Card dataCard = new Card(), extCard = new Card();
        readonly CategoryList list = new CategoryList();
        readonly Panel extHost = new BufferedPanel();
        readonly ThemedListView lvExt = new ThemedListView();
        List<BrowserInfo> browsers = new List<BrowserInfo>();
        List<ExtensionInfo> extensions = new List<ExtensionInfo>();
        // The groups shown (or about to be shown: the list may still be fading over to them).
        List<CatGroup> groups = new List<CatGroup>();
        bool loading, cleaning, loaded;
        // Process lookups are slow: refreshed on worker threads, read by the UI.
        List<string> running = new List<string>();

        static List<string> RunningNames(IEnumerable<BrowserInfo> list)
        {
            return list.Where(b => b.IsRunning).Select(b => b.Name).Distinct().ToList();
        }

        public BrowsersPage(IHost host) : base(host)
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            btnClean = Button("\uE74D", ButtonKind.Primary);
            btnClean.Click += (s, e) => DoClean();
            btnRefresh = Button("\uE72C");
            btnRefresh.Click += (s, e) => RefreshData();
            foreach (Control c in new Control[] { btnClean, btnRefresh, note }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            dataCard.TitleBase = 48;
            list.CheckedChanged += (s, e) => UpdateButtons();
            dataCard.Controls.Add(list);
            dataCard.Resize += (s, e) => list.SetBounds(S(2), dataCard.TitleHeight, dataCard.Width - S(4), dataCard.Height - dataCard.TitleHeight - S(6));

            extCard.TitleBase = 48;
            btnOpenExt = Button("\uE8A7", ButtonKind.Ghost);
            btnOpenExt.SetBaseHeight(30);
            btnOpenExt.Click += (s, e) => OpenExtension();
            lvExt.MultiSelect = false;
            lvExt.Numeric = new[] { false, false, false, true };
            lvExt.SelectedIndexChanged += (s, e) => btnOpenExt.Enabled = lvExt.SelectedItems.Count > 0;
            lvExt.ItemActivate += (s, e) => OpenExtension();
            foreach (int w in new[] { 200, 150, 80, 80 }) lvExt.Columns.Add("", S(w));
            lvExt.ColumnWeights = new[] { 0.4f, 0.26f, 0.16f, 0.18f };
            lvExt.ColumnMins = new[] { 120, 100, 96, 96 };
            lvExt.DefaultGlyph = "\uEA86";
            lvExt.Dock = DockStyle.Fill;
            extHost.Padding = new Padding(0, 1, 0, 0);
            extHost.Controls.Add(lvExt);
            extCard.Controls.Add(extHost);
            extCard.Controls.Add(btnOpenExt);
            extCard.Resize += (s, e) => LayoutExtCard();

            Controls.Add(dataCard);
            Controls.Add(extCard);
            Controls.Add(toolbar);
            Resize += (s, e) => LayoutPage();
        }

        public override void ApplyScale()
        {
            toolbar.Height = S(60);
            list.SetBounds(S(2), dataCard.TitleHeight, dataCard.Width - S(4), dataCard.Height - dataCard.TitleHeight - S(6));
            LayoutToolbar();
            LayoutPage();
            LayoutExtCard();
        }

        void LayoutExtCard()
        {
            btnOpenExt.Location = new Point(extCard.Width - btnOpenExt.Width - S(12), (extCard.TitleHeight - btnOpenExt.Height) / 2);
            extHost.SetBounds(S(1), extCard.TitleHeight, extCard.Width - S(2), extCard.Height - extCard.TitleHeight - S(8));
        }

        void LayoutPage()
        {
            int pad = S(16), top = toolbar.Height;
            int w = Width - pad * 2;
            // Narrow windows split evenly so the extension columns keep room; wide ones favour the data.
            int leftW = (int)(w * (w < S(1100) ? 0.5f : 0.56f));
            dataCard.SetBounds(pad, top, leftW, Height - top - pad);
            extCard.SetBounds(pad + leftW + S(16), top, w - leftW - S(16), Height - top - pad);
        }

        void LayoutToolbar()
        {
            int x = S(16), y = (toolbar.Height - S(32)) / 2;
            btnClean.Location = new Point(x, y);
            btnRefresh.Location = new Point(btnClean.Right + S(8), y);
            note.SetBounds(btnRefresh.Right + S(20), y, Math.Max(S(50), toolbar.Width - btnRefresh.Right - S(36)), S(32));
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            toolbar.BackColor = Theme.Current.Bg;
            extHost.BackColor = Theme.Current.Border;
            lvExt.ApplyTheme();
            list.Invalidate();
        }

        public override void ApplyLanguage()
        {
            dataCard.Title = L.T("Данные браузеров", "Browser data");
            list.EmptyText = L.T("Браузеры не найдены", "No browsers found");
            extCard.Title = L.T("Расширения", "Extensions");
            SetButton(btnRefresh, L.T("Обновить", "Refresh"));
            SetButton(btnOpenExt, L.T("Открыть в браузере", "Open in browser"));
            LayoutExtCard();
            string[] cols = { L.T("Расширение", "Extension"), L.T("Браузер", "Browser"), L.T("Версия", "Version"), L.T("Размер", "Size") };
            for (int i = 0; i < cols.Length; i++) lvExt.Columns[i].Text = cols[i];
            if (loaded) RenderExtensions();
            else lvExt.EmptyText = L.T("Загрузка…", "Loading…");
            UpdateButtons();
            dataCard.Invalidate();
            extCard.Invalidate();
            list.Invalidate();
        }

        protected override void FirstActivate() { Load(); }

        public override void RefreshData()
        {
            if (!cleaning) Load();
        }

        // ---------- loading ----------

        static string DataName(BrowserData d)
        {
            switch (d)
            {
                case BrowserData.Cookies: return L.T("Файлы cookie", "Cookies");
                case BrowserData.History: return L.T("История посещений", "Browsing history");
                case BrowserData.Sessions: return L.T("Открытые вкладки и сеансы", "Open tabs and sessions");
                default: return L.T("Кэш", "Cache");
            }
        }

        static string DataNote(BrowserData d)
        {
            switch (d)
            {
                case BrowserData.Cookies: return L.T("вы выйдете из аккаунтов на сайтах", "you will be signed out of websites");
                case BrowserData.Sessions: return L.T("вкладки не восстановятся", "tabs will not be restored");
                case BrowserData.History: return L.T("пароли и закладки останутся", "passwords and bookmarks stay");
                default: return L.T("безопасно, сайты загрузят его снова", "safe, sites download it again");
            }
        }

        void Load()
        {
            if (loading) return;
            loading = true;
            if (groups.Count == 0) list.Loading = true;
            if (!loaded) lvExt.Loading = true;
            Host.BeginBusy();
            UpdateButtons();
            var th = new Thread(() =>
            {
                var found = Browsers.Detect();
                var made = new List<CatGroup>();
                var all = new List<Tuple<CatItem, Target>>();
                foreach (var b in found)
                    foreach (var p in b.Profiles)
                    {
                        var profile = p;
                        var g = new CatGroup { Tag = profile, Glyph = "\uE774", Name = () => profile.Title,
                            Desc = () => L.T("Профиль: ", "Profile: ") + profile.Name + (profile.Browser.Kind == BrowserKind.Firefox ? " · Firefox" : "") };
                        foreach (BrowserData d in Enum.GetValues(typeof(BrowserData)))
                        {
                            var what = d;
                            var t = new Target { Profile = profile, What = what };
                            var ci = new CatItem
                            {
                                Tag = t, Name = () => DataName(what), Note = () => DataNote(what),
                                Checked = what == BrowserData.Cache, Warn = what == BrowserData.Cookies || what == BrowserData.Sessions,
                                Disabled = !profile.Supports(what),
                                DisabledText = () => L.T("в Firefox хранится вместе с закладками — не трогаем", "in Firefox it is stored with bookmarks — left alone"),
                            };
                            g.Items.Add(ci);
                            all.Add(Tuple.Create(ci, t));
                        }
                        made.Add(g);
                    }
                if (made.Count > 0) made[0].Expanded = true;
                var run = RunningNames(found);
                RunUI(() =>
                {
                    browsers = found;
                    running = run;
                    groups = made;
                    Transition.CrossFade(dataCard, () => list.SetGroups(made), 240);
                });
                // Sizes arrive one by one.
                foreach (var pair in all)
                {
                    if (pair.Item1.Disabled) continue;
                    long size = Browsers.Measure(pair.Item2.Profile, pair.Item2.What);
                    var ci = pair.Item1;
                    Post(() => { ci.Size = size; list.SizeArrived(ci); UpdateButtons(); });
                }
                var ext = new List<ExtensionInfo>();
                foreach (var b in found)
                    foreach (var p in b.Profiles) ext.AddRange(Browsers.Extensions(p));
                Post(() =>
                {
                    extensions = ext;
                    loading = false;
                    loaded = true;
                    Host.EndBusy();
                    Transition.CrossFade(extHost, RenderExtensions, 240);
                    UpdateButtons();
                });
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        void RenderExtensions()
        {
            lvExt.BeginUpdate();
            lvExt.Items.Clear();
            lvExt.ResetRowStates();
            foreach (var e in extensions)
                lvExt.Items.Add(new ListViewItem(new[] { e.Name, e.Profile.Title, e.Version, e.Size > 0 ? L.Size(e.Size) : "" })
                {
                    Tag = new ExtRow { Ext = e }
                });
            lvExt.Sort();
            lvExt.EndUpdate();
            lvExt.Loading = false;
            lvExt.FitLastColumn();
            lvExt.EmptyGlyph = "\uEA86";
            lvExt.EmptyText = browsers.Count == 0 ? L.T("Браузеры не найдены", "No browsers found") : L.T("Расширений нет", "No extensions");
            btnOpenExt.Enabled = lvExt.SelectedItems.Count > 0;
        }

        class ExtRow : IRowInfo, ISortable
        {
            public ExtensionInfo Ext;
            public RowKind RowKind { get { return RowKind.Normal; } }
            public string IconPath { get { return null; } }
            public int Dot { get { return 0; } }
            public IComparable SortKey(int column) { return column == 3 ? (IComparable)Ext.Size : null; }
        }

        void OpenExtension()
        {
            var row = lvExt.SelectedItems.Count > 0 ? lvExt.SelectedItems[0] : null;
            var ext = row != null ? ((ExtRow)row.Tag).Ext : null;
            if (ext == null) return;
            try { Browsers.OpenExtensionPage(ext); }
            catch (Exception ex) { Host.ShowToast(L.T("Не удалось открыть браузер: ", "Could not open the browser: ") + ex.Message, ToastKind.Error); }
        }

        // ---------- cleaning ----------

        List<Target> CheckedTargets()
        {
            return groups.SelectMany(g => g.Items).Where(i => i.Checked && !i.Disabled).Select(i => (Target)i.Tag).ToList();
        }

        long CheckedBytes()
        {
            return groups.Sum(g => g.CheckedSize);
        }

        void UpdateButtons()
        {
            var t = CheckedTargets();
            long bytes = CheckedBytes();
            string text = L.T("Очистить выбранное", "Clean selected") + (bytes > 0 ? " · " + L.Size(bytes) : "");
            if (btnClean.Text != text) SetButton(btnClean, text);
            btnClean.Enabled = t.Count > 0 && !cleaning && !loading;
            btnRefresh.Enabled = !loading && !cleaning;
            note.SetText(running.Count > 0
                ? L.T("Запущены: ", "Running: ") + string.Join(", ", running) + L.T(" — перед очисткой их предложат закрыть", " — you will be asked to close them first")
                : L.T("Пароли и закладки не удаляются", "Passwords and bookmarks are never deleted"));
            LayoutToolbar();
            Host.UpdateStatus();
        }

        public override string Status
        {
            get
            {
                if (!loaded) return L.T("Поиск браузеров…", "Looking for browsers…");
                int profiles = browsers.Sum(b => b.Profiles.Count);
                return string.Format(L.T("Браузеров: {0}  ·  профилей: {1}  ·  расширений: {2}  ·  выбрано: {3}",
                    "Browsers: {0}  ·  profiles: {1}  ·  extensions: {2}  ·  selected: {3}"),
                    browsers.Count, profiles, extensions.Count, L.Size(CheckedBytes()));
            }
        }

        void DoClean()
        {
            var targets = CheckedTargets();
            if (targets.Count == 0 || cleaning) return;
            var runningNow = targets.Select(t => t.Profile.Browser).Distinct().Where(b => b.IsRunning).ToList();
            bool sensitive = targets.Any(t => t.What != BrowserData.Cache);
            if (runningNow.Count > 0)
            {
                string names = string.Join(", ", runningNow.Select(b => b.Name).Distinct());
                if (!ThemedDialog.Confirm(Host.Window, L.T("Закрыть браузеры", "Close browsers"),
                        L.T("Сейчас запущены: " + names + ".\nОни будут закрыты, иначе часть данных не удалится.\nНесохранённые формы на открытых страницах пропадут.",
                            "Running now: " + names + ".\nThey will be closed, otherwise some data cannot be removed.\nUnsaved forms on open pages will be lost."),
                        L.T("Закрыть и очистить", "Close and clean"), "\uE74D", false))
                    return;
            }
            else if (sensitive && !ThemedDialog.Confirm(Host.Window, L.T("Очистка браузеров", "Clean browsers"),
                         L.T("Выбраны cookie, история или сеансы. Восстановить их будет нельзя.\nПродолжить?",
                             "Cookies, history or sessions are selected. They cannot be restored.\nContinue?"),
                         L.T("Очистить", "Clean"), "\uE74D", true))
                return;

            cleaning = true;
            Host.BeginBusy();
            UpdateButtons();
            var items = groups.SelectMany(g => g.Items).Where(i => !i.Disabled).ToList();
            var all = browsers;
            var th = new Thread(() =>
            {
                foreach (var b in runningNow)
                {
                    try { Browsers.Close(b); } catch { }
                }
                long freed = 0;
                int failed = 0;
                foreach (var t in targets)
                {
                    try { freed += Browsers.Clean(t.Profile, t.What, ref failed); } catch { failed++; }
                }
                var sizes = new List<Tuple<CatItem, long>>();
                foreach (var ci in items)
                {
                    var t = (Target)ci.Tag;
                    if (targets.Any(x => x.Profile == t.Profile && x.What == t.What))
                        sizes.Add(Tuple.Create(ci, Browsers.Measure(t.Profile, t.What)));
                }
                var run = RunningNames(all);
                History.Add(HistoryKind.Browsers, freed, string.Join(", ", targets.Select(t => t.Profile.Title + ": " + DataName(t.What))));
                Post(() =>
                {
                    foreach (var s in sizes) { s.Item1.Size = s.Item2; list.SizeArrived(s.Item1); }
                    running = run;
                    cleaning = false;
                    Host.EndBusy();
                    UpdateButtons();
                    string text = L.T("Браузеры очищены: ", "Browsers cleaned: ") + L.Size(freed);
                    if (failed > 0) text += L.T(" · занятых файлов пропущено: ", " · files in use skipped: ") + failed;
                    Host.ShowToast(text, ToastKind.Success);
                });
            }) { IsBackground = true };
            th.Start();
        }
    }
}
