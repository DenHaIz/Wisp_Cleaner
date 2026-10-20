using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    class MainForm : Form, IHost
    {
        public static readonly int ShowMessage = RegisterWindowMessage(Program.Instance + ".Show");

        readonly Panel root = new BufferedPanel(), header = new BufferedPanel(), content = new BufferedPanel();
        readonly Panel pageHost = new BufferedPanel(), footer = new BufferedPanel();
        readonly TabBar tabBar = new TabBar();
        readonly Segmented themeSwitch;
        readonly FlatButton btnLang, btnHistory;
        readonly ToolTip tip = new ToolTip();
        readonly LoadingBar loadBar = new LoadingBar();
        readonly StatusText status = new StatusText();
        readonly Toast toast = new Toast();
        readonly CleanPage cleanPage;
        readonly AppsPage appsPage;
        readonly BrowsersPage browsersPage;
        readonly FilesPage filesPage;
        readonly Page[] pages;
        readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem miOpen, miCleanNow, miExit;
        NotifyIcon tray;
        int currentTab, busy;
        bool allowShow, exiting, autoRunning, fading;
        int titleWidth;
        string[] themeTexts = { "", "" };
        bool compactTheme, instantTab;
        public int StartTab;

        static int S(int v) { return Theme.S(v); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);

        public MainForm(bool startHidden)
        {
            allowShow = !startHidden;
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(720));
            MinimumSize = new Size(S(980), S(620));
            KeyPreview = true;
            DoubleBuffered = true;
            Icon = Logo.AppIcon;
            Opacity = 0;

            // ----- header: brand, tabs, language and theme switches -----
            header.Dock = DockStyle.Top;
            header.Height = S(60);
            header.Paint += PaintHeader;
            tabBar.Height = header.Height;
            tabBar.AddTab("", "\uE74D");
            tabBar.AddTab("", "\uE71D");
            tabBar.AddTab("", "\uE909");
            tabBar.AddTab("", "\uED25");
            tabBar.SelectedIndexChanged += (s, e) => OnTabChanged();
            themeSwitch = new Segmented(new[] { "", "" }, new[] { "\uE708", "\uE706" }, Theme.Current.IsDark ? 0 : 1);
            themeSwitch.SelectedIndexChanged += (s, e) => OnThemeChanged();
            btnLang = new FlatButton("", "\uE774");
            btnLang.Click += (s, e) => OnLanguageClick();
            header.Controls.Add(tabBar);
            header.Controls.Add(btnLang);
            header.Controls.Add(themeSwitch);
            header.Resize += (s, e) => LayoutHeader();

            loadBar.Dock = DockStyle.Top;
            loadBar.Height = S(2);

            // ----- pages: not docked (docking skips hidden controls); sized by hand -----
            cleanPage = new CleanPage(this);
            appsPage = new AppsPage(this);
            browsersPage = new BrowsersPage(this);
            filesPage = new FilesPage(this);
            pages = new Page[] { cleanPage, appsPage, browsersPage, filesPage };
            foreach (var p in pages)
            {
                p.Visible = p == cleanPage;
                pageHost.Controls.Add(p);
            }
            cleanPage.AutoCleanChanged += (s, e) => UpdateTray();
            pageHost.Dock = DockStyle.Fill;
            pageHost.Resize += (s, e) =>
            {
                foreach (var p in pages) p.Bounds = pageHost.ClientRectangle;
            };
            content.Dock = DockStyle.Fill;
            content.Controls.Add(pageHost);

            // ----- footer -----
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(40);
            status.SetBounds(S(18), 0, S(700), footer.Height);
            btnHistory = new FlatButton("", "\uE81C", ButtonKind.Ghost);
            btnHistory.SetBaseHeight(30);
            btnHistory.Click += (s, e) => HistoryForm.ShowFor(this);
            footer.Controls.Add(status);
            footer.Controls.Add(btnHistory);
            footer.Resize += (s, e) => LayoutFooter();

            // Docking: later-added controls are docked first (outermost).
            root.Dock = DockStyle.Fill;
            root.Controls.Add(content);
            root.Controls.Add(footer);
            root.Controls.Add(loadBar);
            root.Controls.Add(header);
            root.Controls.Add(toast);
            root.Resize += (s, e) => toast.Reposition(ToastAnchor());
            Controls.Add(root);

            // ----- tray (only while auto-clean is on) -----
            miOpen = TrayItem("\uE8A7", (s, e) => ShowFromTray());
            miCleanNow = TrayItem("\uE74D", (s, e) => RunAutoClean(true));
            trayMenu.Items.Add(new ToolStripSeparator());
            miExit = TrayItem("\uE711", (s, e) => ExitApp());
            trayMenu.Renderer = new ThemedRenderer();
            trayMenu.Font = Theme.UiFont;
            trayMenu.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in trayMenu.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(S(20), S(4), 0, S(4));

            autoTimer.Tick += (s, e) => CheckAutoClean();
            autoTimer.Start();

            ApplyTheme();
            ApplyLanguage();
            UpdateTray();
        }

        ToolStripMenuItem TrayItem(string glyph, EventHandler click)
        {
            var mi = new ToolStripMenuItem { Tag = new MenuTag { Glyph = glyph } };
            mi.Click += click;
            trayMenu.Items.Add(mi);
            return mi;
        }

        public Form Window { get { return this; } }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int size = S(32);
            Logo.Draw(g, new RectangleF(S(18), (header.Height - size) / 2, size, size), true);
            TextRenderer.DrawText(g, "Wisp Cleaner", Theme.TitleFont, new Rectangle(S(60), 0, titleWidth, header.Height), Theme.Current.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------- language ----------

        static void SetButton(FlatButton b, string text)
        {
            b.Text = text;
            b.Width = b.PreferredWidth;
        }

        void ApplyLanguage()
        {
            Text = L.T("Wisp Cleaner — очистка диска", "Wisp Cleaner — disk cleanup");
            tabBar.SetTabText(0, L.T("Очистка", "Clean"));
            tabBar.SetTabText(1, L.T("Программы", "Programs"));
            tabBar.SetTabText(2, L.T("Браузеры", "Browsers"));
            tabBar.SetTabText(3, L.T("Файлы", "Files"));
            themeTexts = new[] { L.T("Тёмная", "Dark"), L.T("Светлая", "Light") };
            themeSwitch.SetTexts(themeTexts);
            compactTheme = false;
            tip.SetToolTip(themeSwitch, L.T("Тема оформления", "Theme"));
            SetButton(btnLang, L.En ? "EN" : "RU");
            tip.SetToolTip(btnLang, L.T("Язык интерфейса: русский. Switch to English", "Interface language: English. Переключить на русский"));
            SetButton(btnHistory, L.T("История очисток", "Cleaning history"));
            miOpen.Text = L.T("Открыть Wisp Cleaner", "Open Wisp Cleaner");
            miCleanNow.Text = L.T("Очистить сейчас", "Clean now");
            miExit.Text = L.T("Выход", "Exit");
            if (tray != null) tray.Text = "Wisp Cleaner";
            foreach (var p in pages) p.ApplyLanguage();
            LayoutHeader();
            LayoutFooter();
            UpdateStatus();
        }

        void OnLanguageClick()
        {
            Transition.CrossFade(root, () =>
            {
                L.En = !L.En;
                L.Save();
                ApplyLanguage();
            }, 340);
        }

        // ---------- theme ----------

        void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Bg;
            foreach (Control c in new Control[] { root, header, content, pageHost, footer, loadBar })
                c.BackColor = p.Bg;
            foreach (var page in pages) page.ApplyTheme();
            if (IsHandleCreated) Theme.ApplyTitleBar(Handle);
            root.Invalidate(true);
        }

        void OnThemeChanged()
        {
            // Let the switch finish sliding, then cross-fade the whole window into the new palette.
            Anim.Delay(280, () => Transition.CrossFade(root, () =>
            {
                Theme.Current = themeSwitch.SelectedIndex == 0 ? Theme.Dark : Theme.Light;
                Theme.Save();
                ApplyTheme();
            }, 380));
        }

        // ---------- window lifetime ----------

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow && value)
            {
                // Started with /tray: create the window (for BeginInvoke and messages) but keep it hidden.
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Create every page's window now: background loads post back to them even while hidden.
            foreach (var p in pages) p.CreateControl();
            foreach (var p in pages)
            {
                IntPtr unused = p.Handle;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (StartTab > 0)
            {
                // Opened on another tab (/tab:N): switch before the window appears, no transition.
                instantTab = true;
                tabBar.SelectedIndex = StartTab;
                instantTab = false;
            }
            // The first tab starts loading once the window is in: work landing on the UI thread
            // mid-fade (results arriving, the list fading over to them) made the fade stutter.
            FadeIn(() => pages[currentTab].Activate());
            SchedulePreload();
        }

        bool preloadScheduled;

        /// <summary>
        /// Loads the other tabs quietly once the window has settled, so switching to them shows
        /// finished lists instead of a loading state (each tab still refreshes on F5).
        /// </summary>
        void SchedulePreload()
        {
            if (preloadScheduled) return;
            preloadScheduled = true;
            Anim.Delay(1200, () => appsPage.Preload());
            Anim.Delay(2000, () => browsersPage.Preload());
        }

        void FadeIn(Action done = null)
        {
            // The first opacity change composes the layered window once, which is slow: pay for it
            // (and for the first paint) before the clock starts.
            Opacity = 0.01;
            WindowFx.PaintNow(this);
            Anim.Run(this, 280, t => Opacity = 0.01 + 0.99 * t, () =>
            {
                WindowFx.EndFade(this);
                if (done != null) done();
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && Settings.AutoClean)
            {
                // Auto-clean needs the app running: closing the window only hides it to the tray.
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (tray != null) tray.Visible = false;
            base.OnFormClosed(e);
        }

        void HideToTray()
        {
            if (fading) return;
            fading = true;
            Anim.Run(this, 200, t => Opacity = 1 - t, () =>
            {
                Hide();
                fading = false;
                if (!Settings.TrayHintShown && tray != null)
                {
                    Settings.TrayHintShown = true;
                    Settings.Save();
                    tray.ShowBalloonTip(5000, "Wisp Cleaner",
                        L.T("Работает в трее для автоочистки. Выйти можно из меню значка.",
                            "Running in the tray for auto-clean. Exit from the icon's menu."), ToolTipIcon.Info);
                }
            });
        }

        public void ShowFromTray()
        {
            allowShow = true;
            if (!Visible)
            {
                Opacity = 0;
                Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                pages[currentTab].Activate();
                FadeIn();
                SchedulePreload();
            }
            else if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            Close();
            Application.Exit();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMessage && ShowMessage != 0)
            {
                // wParam 1..4 also opens that tab (WispCleaner.exe /tab:N on a running instance).
                ShowFromTray();
                int tab = m.WParam.ToInt32();
                if (tab >= 1 && tab <= pages.Length) tabBar.SelectedIndex = tab - 1;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- layout ----------

        /// <summary>
        /// The interface grows with the window: zoom 1 at the default size, up to Theme.MaxZoom
        /// (a maximized window on a 1080p screen gets about 1.4). Steps of 0.1 with a little
        /// hysteresis, so dragging the border does not flip back and forth between two steps.
        /// </summary>
        float ZoomFor(Size client)
        {
            float raw = Math.Min(client.Width / (1180f * Theme.Dpi), client.Height / (720f * Theme.Dpi));
            float step = (float)Math.Floor(raw * 10 + 0.0001f) / 10;
            step = Math.Max(1f, Math.Min(Theme.MaxZoom, step));
            float now = Theme.Zoom;
            if (step > now && raw < step + 0.02f && step - now < 0.11f) return now;
            if (step < now && raw > now - 0.03f) return now;
            return step;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized || ClientSize.Width < 10 || pages == null) return;
            float z = ZoomFor(ClientSize);
            if (Math.Abs(z - Theme.Zoom) > 0.001f) ApplyZoom(z);
        }

        /// <summary>Switches every control to the new zoom: fonts, sizes, then the layout.</summary>
        void ApplyZoom(float zoom)
        {
            if (!Theme.SetZoom(zoom)) return;
            double t0 = Anim.Now;
            // Running transitions hold pictures of the old layout.
            Transition.FinishAll();
            root.SuspendLayout();
            Rescale(root);
            header.Height = S(60);
            tabBar.Height = header.Height;
            loadBar.Height = S(2);
            footer.Height = S(40);
            trayMenu.Font = Theme.UiFont;
            root.ResumeLayout(true);
            foreach (var p in pages) p.Bounds = pageHost.ClientRectangle;
            LayoutHeader();
            LayoutFooter();
            foreach (var p in pages) p.ApplyScale();
            toast.Reposition(ToastAnchor());
            root.Invalidate(true);
            Anim.Log("Z " + zoom + " " + (Anim.Now - t0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        static void Rescale(Control c)
        {
            foreach (Control child in c.Controls) Rescale(child);
            var f = Theme.Remap(c.Font);
            if (f != c.Font) c.Font = f;
            var s = c as IScalable;
            if (s != null && !(c is Page)) s.ApplyScale();
        }

        void LayoutHeader()
        {
            titleWidth = TextRenderer.MeasureText("Wisp Cleaner", Theme.TitleFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(4);
            tabBar.Location = new Point(S(60) + titleWidth + S(28), 0);
            // Narrow window: the theme switch keeps only its icons so nothing overlaps the tabs.
            bool compact = header.Width < tabBar.Right + S(24) + btnLang.Width + S(8) + themeSwitch.Width + S(16) + (compactTheme ? S(200) : 0);
            if (compact != compactTheme)
            {
                compactTheme = compact;
                themeSwitch.SetTexts(compact ? new[] { "", "" } : themeTexts);
            }
            int y = (header.Height - themeSwitch.Height) / 2;
            themeSwitch.Location = new Point(header.Width - themeSwitch.Width - S(16), y);
            btnLang.Location = new Point(themeSwitch.Left - btnLang.Width - S(8), y);
            header.Invalidate();
        }

        void LayoutFooter()
        {
            status.SetBounds(S(18), 0, status.Width, footer.Height);
            btnHistory.Location = new Point(footer.Width - btnHistory.Width - S(16), (footer.Height - btnHistory.Height) / 2);
            status.Width = Math.Max(S(100), btnHistory.Left - status.Left - S(12));
        }

        Point ToastAnchor()
        {
            return new Point(root.ClientSize.Width - S(28), footer.Top - S(14));
        }

        public void ShowToast(string text, ToastKind kind)
        {
            toast.Show(text, kind, ToastAnchor());
        }

        public void UpdateStatus()
        {
            status.SetText(pages[tabBar.SelectedIndex].Status);
        }

        public void BeginBusy()
        {
            if (busy++ == 0) loadBar.Start();
        }

        public void EndBusy()
        {
            if (busy > 0 && --busy == 0) loadBar.Stop();
        }

        // ---------- tabs ----------

        void OnTabChanged()
        {
            int idx = tabBar.SelectedIndex;
            int dir = idx > currentTab ? 1 : -1;
            currentTab = idx;
            Anim.Log("TAB " + idx);
            Action show = () =>
            {
                for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == idx;
            };
            if (instantTab) show();
            else Transition.CrossFade(pageHost, show, 260, dir * S(36));
            pages[idx].Activate();
            UpdateStatus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) { pages[currentTab].RefreshData(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { pages[currentTab].FocusSearch(); e.Handled = true; }
            else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
            {
                tabBar.SelectedIndex = e.KeyCode - Keys.D1;
                e.Handled = true;
            }
        }

        // ---------- tray and auto-clean ----------

        void UpdateTray()
        {
            if (Settings.AutoClean && tray == null)
            {
                tray = new NotifyIcon
                {
                    Icon = new Icon(Logo.AppIcon, SystemInformation.SmallIconSize),
                    Text = "Wisp Cleaner",
                    ContextMenuStrip = trayMenu,
                    Visible = true,
                };
                tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            }
            else if (!Settings.AutoClean && tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
        }

        bool AutoCleanDue()
        {
            if (!Settings.AutoClean) return false;
            var now = DateTime.Now;
            switch (Settings.Schedule)
            {
                case Schedule.Daily: return now - Settings.LastAuto >= TimeSpan.FromDays(1);
                case Schedule.Weekly: return now - Settings.LastAuto >= TimeSpan.FromDays(7);
                default:
                    if (now - Settings.LastLowSpace < TimeSpan.FromHours(6)) return false;
                    try
                    {
                        var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                        return drive.AvailableFreeSpace < Settings.ThresholdGb * 1073741824L;
                    }
                    catch { return false; }
            }
        }

        void CheckAutoClean()
        {
            if (AutoCleanDue()) RunAutoClean(false);
        }

        void RunAutoClean(bool manual)
        {
            if (autoRunning || cleanPage.Busy) return;
            autoRunning = true;
            if (Visible) BeginBusy();
            var th = new Thread(() =>
            {
                CleanResult r = null;
                try { r = Cleaner.RunUnattended(); }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { AutoCleanDone(r, manual); });
                }
                catch (InvalidOperationException) { }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void AutoCleanDone(CleanResult r, bool manual)
        {
            autoRunning = false;
            if (Visible) EndBusy();
            if (Settings.Schedule == Schedule.LowSpace) Settings.LastLowSpace = DateTime.Now;
            Settings.LastAuto = DateTime.Now;
            Settings.Save();
            long freed = r != null ? r.Freed : 0;
            History.Add(HistoryKind.Auto, freed, r != null ? string.Join(", ", r.Items) : "");
            string text = freed > 0
                ? L.T("Автоочистка: освобождено ", "Auto-clean: freed ") + L.Size(freed)
                : L.T("Автоочистка: мусора не найдено", "Auto-clean: no junk found");
            if (tray != null && (!Visible || manual)) tray.ShowBalloonTip(4000, "Wisp Cleaner", text, ToolTipIcon.Info);
            if (Visible) ShowToast(text, ToastKind.Success);
            cleanPage.AfterAutoClean();
        }
    }
}
