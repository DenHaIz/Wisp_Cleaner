using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>
    /// Main tab: the big ring (one-button clean), the "what to clean" categories (semi-automatic)
    /// and the auto-clean settings card.
    /// </summary>
    class CleanPage : Page
    {
        readonly CleanRing ring = new CleanRing();
        readonly FlatButton btnClean, btnRescan;
        readonly StatusText hint = new StatusText { Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding };
        readonly Card catCard = new Card(), autoCard = new Card();
        readonly CategoryList list = new CategoryList();
        readonly ToggleSwitch swAuto = new ToggleSwitch(""), swAutostart = new ToggleSwitch("");
        readonly Segmented segSchedule, segThreshold;
        readonly StatusText autoInfo = new StatusText();
        readonly ToolTip tip = new ToolTip();
        static readonly int[] Thresholds = { 5, 10, 20 };

        List<CleanGroup> groups = new List<CleanGroup>();
        // The groups shown (or about to be shown: the list may still be fading over to them).
        List<CatGroup> cats = new List<CatGroup>();
        bool scanning, cleaning, done, syncing;
        int scanGeneration;
        CleanResult lastResult;

        public event EventHandler AutoCleanChanged;

        public CleanPage(IHost host) : base(host)
        {
            ring.Click += (s, e) => { if (ring.Clickable) DoClean(); };
            btnClean = new FlatButton("", "\uE74D", ButtonKind.Primary);
            btnClean.SetBaseHeight(44);
            btnClean.Font = Theme.UiBold;
            btnClean.Click += (s, e) => { if (done) StartScan(); else DoClean(); };
            btnRescan = new FlatButton("", "\uE72C", ButtonKind.Ghost);
            btnRescan.Click += (s, e) => StartScan();
            Controls.Add(ring);
            Controls.Add(btnClean);
            Controls.Add(hint);

            catCard.TitleBase = 52;
            list.Loading = true;   // placeholders until the first scan has the categories
            list.CheckedChanged += (s, e) => OnChecksChanged();
            catCard.Controls.Add(list);
            catCard.Controls.Add(btnRescan);
            catCard.Resize += (s, e) => LayoutCatCard();
            Controls.Add(catCard);

            // ----- auto-clean card: off by default, everything below the switch waits for it -----
            segSchedule = new Segmented(new[] { "", "", "" }, new[] { "\uE787", "\uE8BF", "\uEDA2" }, (int)Settings.Schedule);
            int thIdx = Array.IndexOf(Thresholds, Settings.ThresholdGb);
            segThreshold = new Segmented(new[] { "5", "10", "20" }, new[] { "", "", "" }, thIdx < 0 ? 1 : thIdx);
            swAuto.Checked = Settings.AutoClean;
            swAutostart.Checked = Settings.Autostart;
            swAuto.CheckedChanged += (s, e) => OnAutoToggled();
            swAutostart.CheckedChanged += (s, e) => OnAutostartToggled();
            segSchedule.SelectedIndexChanged += (s, e) =>
            {
                Settings.Schedule = (Schedule)segSchedule.SelectedIndex;
                Settings.Save();
                LayoutAutoCard(true);
                UpdateAutoInfo();
            };
            segThreshold.SelectedIndexChanged += (s, e) =>
            {
                Settings.ThresholdGb = Thresholds[segThreshold.SelectedIndex];
                Settings.Save();
                UpdateAutoInfo();
            };
            foreach (Control c in new Control[] { swAuto, autoInfo, segSchedule, segThreshold, swAutostart }) autoCard.Controls.Add(c);
            autoCard.Resize += (s, e) => LayoutAutoCard(false);
            Controls.Add(autoCard);
            UpdateAutoEnabled();

            Resize += (s, e) => LayoutPage();
        }

        // ---------- layout ----------

        void LayoutPage()
        {
            // Everything scales with the window: the left column takes ~38 % of the width and the
            // ring fills it (up to 600 px), the button grows a little with the ring.
            int pad = S(16);
            int leftW = Math.Max(S(320), Math.Min(S(640), (int)(Width * 0.38)));
            btnClean.SetBaseHeight(Height > S(760) ? 48 : 44);
            int below = S(16) + btnClean.Height + S(10) + S(24);
            int ringSize = Math.Max(S(160), Math.Min(S(600), Math.Min(leftW, Height - pad * 2 - below - S(20))));
            int blockH = ringSize + below;
            int top = Math.Max(pad, (Height - blockH) / 2);
            ring.SetBounds(pad + (leftW - ringSize) / 2, top, ringSize, ringSize);
            btnClean.Top = ring.Bottom + S(16);
            PlaceCleanButton(false);
            hint.SetBounds(pad, btnClean.Bottom + S(10), leftW, S(24));

            int x = pad + leftW + S(20);
            int w = Math.Max(S(300), Width - x - pad);
            int autoH = S(130);
            autoCard.SetBounds(x, Height - pad - autoH, w, autoH);
            catCard.SetBounds(x, pad, w, Math.Max(S(120), autoCard.Top - S(12) - pad));
        }

        readonly object cleanKey = new object();

        /// <summary>Centres the clean button under the ring; a new caption changes its width smoothly (as in DriveUP).</summary>
        void PlaceCleanButton(bool animate)
        {
            int w = CleanButtonWidth(), x = ring.Left + (ring.Width - w) / 2;
            if (!animate || !Visible || (w == btnClean.Width && x == btnClean.Left))
            {
                Anim.Cancel(cleanKey);
                btnClean.SetBounds(x, btnClean.Top, w, btnClean.Height);
                return;
            }
            int w0 = btnClean.Width, x0 = btnClean.Left;
            Anim.Run(cleanKey, 240, t => btnClean.SetBounds((int)Math.Round(Anim.Lerp(x0, x, t)), btnClean.Top, (int)Math.Round(Anim.Lerp(w0, w, t)), btnClean.Height));
        }

        int CleanButtonWidth()
        {
            return Math.Max(Math.Max(S(220), (int)(ring.Width * 0.55f)), btnClean.PreferredWidth + S(24));
        }

        void LayoutCatCard()
        {
            btnRescan.SetBaseHeight(32);
            btnRescan.Location = new Point(catCard.Width - btnRescan.Width - S(12), (catCard.TitleHeight - btnRescan.Height) / 2);
            list.SetBounds(S(2), catCard.TitleHeight, catCard.Width - S(4), catCard.Height - catCard.TitleHeight - S(6));
        }

        /// <summary>
        /// Three rows: the two switches; when to clean (+ the free-space threshold, which slides in);
        /// a one-line explanation of what will happen.
        /// </summary>
        void LayoutAutoCard(bool animate)
        {
            int x = S(18), y1 = S(10), y2 = S(52), y3 = S(92);
            swAuto.Location = new Point(x, y1);
            int far = autoCard.Width - swAutostart.Width - S(18);
            swAutostart.Location = new Point(Math.Max(swAuto.Right + S(16), Math.Min(far, x + S(560))), y1);
            segSchedule.Location = new Point(x, y2);
            bool low = segSchedule.SelectedIndex == (int)Schedule.LowSpace;
            int target = segSchedule.Right + S(10);
            if (low && !segThreshold.Visible)
            {
                segThreshold.Location = new Point(animate ? target - S(24) : target, y2);
                segThreshold.Visible = true;
                if (animate) Anim.Run(Tuple.Create(segThreshold, "x"), 260, t => segThreshold.Left = (int)Anim.Lerp(target - S(24), target, t));
            }
            else if (low) segThreshold.Location = new Point(target, y2);
            else segThreshold.Visible = false;
            int infoX = x + S(2);
            if (low && target + segThreshold.Width > autoCard.Width - S(12))
            {
                // Narrow window: the threshold goes to the explanation row.
                Anim.Cancel(Tuple.Create(segThreshold, "x"));
                segThreshold.Location = new Point(x, y3 - S(2));
                infoX = segThreshold.Right + S(12);
            }
            autoInfo.SetBounds(infoX, y3, Math.Max(S(50), autoCard.Width - infoX - x), S(28));
        }

        public override void ApplyScale()
        {
            LayoutAutoCard(false);
            LayoutCatCard();
            LayoutPage();
            ring.Invalidate();
        }

        // ---------- language / theme ----------

        public override void ApplyLanguage()
        {
            catCard.Title = L.T("Что очищать", "What to clean");
            SetButton(btnRescan, L.T("Повторить анализ", "Scan again"));
            swAuto.Text = L.T("Автоочистка", "Auto-clean");
            swAutostart.Text = L.T("Запускать с Windows", "Start with Windows");
            segSchedule.SetTexts(new[] { L.T("Каждый день", "Daily"), L.T("Раз в неделю", "Weekly"), L.T("Мало места", "Low space") });
            segThreshold.SetTexts(new[] { L.T("5 ГБ", "5 GB"), L.T("10 ГБ", "10 GB"), L.T("20 ГБ", "20 GB") });
            tip.SetToolTip(segThreshold, L.T("Чистить, когда на системном диске свободно меньше", "Clean when free space on the system drive drops below"));
            tip.SetToolTip(swAutostart, L.T("Wisp Cleaner будет запускаться в трее при входе в Windows, чтобы автоочистка работала.\n" +
                "Для этого копия программы хранится в защищённой папке Program Files; при выключении она удаляется.",
                "Wisp Cleaner starts in the tray when you sign in, so auto-clean can run.\n" +
                "For that a copy is kept in the protected Program Files folder; it is removed when you turn this off."));
            UpdateRingTexts();
            UpdateAutoInfo();
            LayoutAutoCard(false);
            LayoutCatCard();
            LayoutPage();
            list.Invalidate();
            catCard.Invalidate();
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            list.Invalidate();
            ring.Invalidate();
        }

        void UpdateRingTexts()
        {
            long sel = SelectedBytes();
            if (cleaning)
            {
                btnClean.Text = L.T("Очистка…", "Cleaning…");
            }
            else if (done)
            {
                ring.Caption = lastResult != null && lastResult.Freed > 0 ? L.T("освобождено", "freed") : L.T("уже чисто", "already clean");
                btnClean.Text = L.T("Проверить снова", "Check again");
                btnClean.Glyph = "\uE72C";
            }
            else if (scanning)
            {
                ring.Caption = L.T("анализ…", "scanning…");
                btnClean.Text = L.T("Очистить", "Clean");
                btnClean.Glyph = "\uE74D";
            }
            else
            {
                ring.Caption = sel > 0 ? L.T("можно освободить", "can be freed") : L.T("мусора не найдено", "no junk found");
                btnClean.Text = L.T("Очистить", "Clean");
                btnClean.Glyph = "\uE74D";
            }
            PlaceCleanButton(true);
            btnClean.Enabled = !cleaning && !scanning && (done || sel > 0);
            btnRescan.Enabled = !cleaning && !scanning;
            ring.Clickable = !cleaning && !scanning && !done && sel > 0;
            ring.Invalidate();
            UpdateHint();
        }

        void UpdateHint()
        {
            if (scanning) hint.SetText(L.T("Ищем ненужные файлы…", "Looking for junk files…"));
            else if (cleaning) hint.SetText(L.T("Удаляем выбранное…", "Removing the selection…"));
            else if (done && lastResult != null)
            {
                string t = string.Format(L.T("Удалено файлов: {0}", "Files removed: {0}"), lastResult.Files.ToString("N0"));
                if (lastResult.Failed > 0) t += L.T(" · пропущено занятых: ", " · skipped (in use): ") + lastResult.Failed.ToString("N0");
                hint.SetText(t);
            }
            else
            {
                int n = AllCatItems().Count(i => i.Checked && i.Size > 0);
                hint.SetText(n > 0 ? string.Format(L.T("Выбрано пунктов: {0}", "Items selected: {0}"), n) : L.T("Отметьте, что очищать, справа", "Choose what to clean on the right"));
            }
        }

        // ---------- data ----------

        IEnumerable<CatItem> AllCatItems()
        {
            return cats.SelectMany(g => g.Items);
        }

        long SelectedBytes()
        {
            return cats.Sum(g => g.CheckedSize);
        }

        public bool Busy { get { return scanning || cleaning; } }

        protected override void FirstActivate()
        {
            StartScan();
        }

        public override void RefreshData()
        {
            if (!Busy) StartScan();
        }

        public void StartScan()
        {
            if (Busy) return;
            scanning = true;
            done = false;
            lastResult = null;
            int gen = ++scanGeneration;
            ring.SetBytes(0, false);
            ring.SetState(RingState.Scanning);
            if (cats.Count == 0) list.Loading = true;
            UpdateRingTexts();
            Host.BeginBusy();
            Host.UpdateStatus();

            var th = new Thread(() =>
            {
                var built = CleanCatalog.Build();
                var made = built.Select(MakeCat).ToList();
                RunUI(() =>
                {
                    groups = built;
                    cats = made;
                    // Keep the expanded state of groups the user opened before.
                    var open = new HashSet<string>(list.Groups.Where(g => g.Expanded).Select(g => ((CleanGroup)g.Tag).Id));
                    foreach (var c in made) c.Expanded = open.Contains(((CleanGroup)c.Tag).Id);
                    Transition.CrossFade(list, () => list.SetGroups(made), 220);
                });
                var map = made.SelectMany(c => c.Items).ToDictionary(i => (CleanItem)i.Tag);
                Cleaner.Scan(built, item => RunUI(() =>
                {
                    if (gen != scanGeneration) return;
                    CatItem ci;
                    if (!map.TryGetValue(item, out ci)) return;
                    ci.Size = item.Size;
                    list.SizeArrived(ci);
                    ring.SetBytes(SelectedBytes());
                }), null);
                RunUI(() => ScanDone(gen));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        CatGroup MakeCat(CleanGroup g)
        {
            var cg = new CatGroup { Tag = g, Glyph = g.Glyph, Name = () => g.Name, Desc = () => g.Desc };
            foreach (var it in g.Items)
            {
                var item = it;
                cg.Items.Add(new CatItem
                {
                    Tag = item, Name = () => item.Name, Note = () => item.Note, Checked = item.Checked, Warn = item.Risky,
                });
            }
            return cg;
        }

        void ScanDone(int gen)
        {
            if (gen != scanGeneration) return;
            scanning = false;
            Host.EndBusy();
            ring.SetBytes(SelectedBytes());
            ring.SetState(RingState.Ready);
            UpdateRingTexts();
            Host.UpdateStatus();
        }

        void OnChecksChanged()
        {
            foreach (var ci in AllCatItems()) ((CleanItem)ci.Tag).Checked = ci.Checked;
            Settings.Save();
            if (!scanning && !cleaning)
            {
                if (done)
                {
                    // Choosing something new after a clean goes back to the "ready" view.
                    done = false;
                    ring.SetState(RingState.Ready);
                }
                ring.SetBytes(SelectedBytes());
            }
            UpdateRingTexts();
            Host.UpdateStatus();
        }

        void DoClean()
        {
            if (Busy) return;
            var items = AllCatItems().Where(i => i.Checked && i.Size > 0).Select(i => (CleanItem)i.Tag).ToList();
            if (items.Count == 0)
            {
                Host.ShowToast(L.T("Нечего очищать: отметьте пункты в списке", "Nothing to clean: tick items in the list"), ToastKind.Info);
                return;
            }
            cleaning = true;
            ring.SetBytes(0, false);
            ring.SetState(RingState.Cleaning);
            UpdateRingTexts();
            Host.BeginBusy();
            Host.UpdateStatus();

            var th = new Thread(() =>
            {
                var result = Cleaner.Clean(items, (freed, total) => Post(() =>
                {
                    ring.SetProgress(total > 0 ? (float)Math.Min(1.0, freed / (double)total) : 1f);
                    ring.SetBytes(freed);
                    ring.Caption = L.T("очистка… ", "cleaning… ") + (total > 0 ? (int)(100.0 * freed / total) : 100) + "%";
                }));
                History.Add(HistoryKind.Manual, result.Freed, string.Join(", ", result.Items));
                // Re-measure quietly so the list shows what is left (files in use, etc.).
                Cleaner.Scan(groups.Where(g => g.Items.Any(items.Contains)), null, null);
                RunUI(() => CleanDone(result));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void CleanDone(CleanResult result)
        {
            cleaning = false;
            done = true;
            lastResult = result;
            Host.EndBusy();
            ring.SetProgress(1);
            Anim.Delay(220, () =>
            {
                ring.SetState(RingState.Done);
                ring.SetBytes(result.Freed);
                UpdateRingTexts();
            });
            foreach (var ci in AllCatItems())
            {
                var item = (CleanItem)ci.Tag;
                if (ci.Size != item.Size) { ci.Size = item.Size; list.SizeArrived(ci); }
            }
            UpdateRingTexts();
            Host.UpdateStatus();
            string text = result.Freed > 0
                ? L.T("Освобождено ", "Freed ") + L.Size(result.Freed)
                : L.T("Ничего не удалено: файлы заняты другими программами", "Nothing removed: the files are in use by other programs");
            if (result.Freed > 0 && result.Failed > 0)
                text += L.T(" · часть файлов занята и пропущена", " · some files were in use and skipped");
            Host.ShowToast(text, result.Freed > 0 ? ToastKind.Success : ToastKind.Info);
        }

        /// <summary>Called by the main window after a background auto-clean.</summary>
        public void AfterAutoClean()
        {
            if (!Busy && groups.Count > 0) StartScan();
        }

        public override string Status
        {
            get
            {
                if (scanning) return L.T("Анализ…", "Scanning…");
                if (cleaning) return L.T("Очистка…", "Cleaning…");
                long total = cats.Sum(g => g.TotalSize);
                return string.Format(L.T("Найдено: {0}  ·  выбрано к очистке: {1}  ·  всего освобождено Wisp Cleaner: {2}",
                    "Found: {0}  ·  selected: {1}  ·  freed by Wisp Cleaner so far: {2}"),
                    L.Size(total), L.Size(SelectedBytes()), L.Size(History.TotalFreed()));
            }
        }

        // ---------- auto-clean settings ----------

        void UpdateAutoEnabled()
        {
            bool on = swAuto.Checked;
            segSchedule.Enabled = segThreshold.Enabled = swAutostart.Enabled = on;
        }

        void UpdateAutoInfo()
        {
            if (!Settings.AutoClean)
            {
                autoInfo.SetText(L.T("Выключена. Включите, чтобы Wisp Cleaner сам чистил отмеченное в трее",
                    "Off. Turn on to let Wisp Cleaner clean the selection from the tray"));
                return;
            }
            string when;
            switch (Settings.Schedule)
            {
                case Schedule.Daily: when = L.T(" раз в день", " once a day"); break;
                case Schedule.LowSpace: when = string.Format(L.T(", когда на системном диске меньше {0} ГБ", " when the system drive has less than {0} GB free"), Settings.ThresholdGb); break;
                default: when = L.T(" раз в неделю", " once a week"); break;
            }
            string text = L.T("Чистит отмеченное", "Cleans the selection") + when;
            var last = History.Load(50).FirstOrDefault(e => e.Kind == HistoryKind.Auto);
            if (last != null)
                text += L.T("  ·  последняя: ", "  ·  last: ") + last.When.ToString("g") + ", " + L.Size(last.Bytes);
            autoInfo.SetText(text);
        }

        void OnAutoToggled()
        {
            if (syncing) return;
            Settings.AutoClean = swAuto.Checked;
            if (Settings.AutoClean && Settings.LastAuto == DateTime.MinValue) Settings.LastAuto = DateTime.Now;
            if (!Settings.AutoClean && swAutostart.Checked)
            {
                // Start-with-Windows only exists to serve auto-clean.
                swAutostart.Checked = false;
            }
            Settings.Save();
            UpdateAutoEnabled();
            UpdateAutoInfo();
            if (AutoCleanChanged != null) AutoCleanChanged(this, EventArgs.Empty);
            Host.ShowToast(Settings.AutoClean
                ? L.T("Автоочистка включена. Закрытое окно останется в трее", "Auto-clean is on. A closed window stays in the tray")
                : L.T("Автоочистка выключена", "Auto-clean is off"), ToastKind.Info);
        }

        void OnAutostartToggled()
        {
            if (syncing) return;
            bool want = swAutostart.Checked;
            var th = new Thread(() =>
            {
                string error = null;
                try { Autostart.Set(want); }
                catch (Exception ex) { error = ex.Message; }
                RunUI(() =>
                {
                    if (error != null)
                    {
                        syncing = true;
                        swAutostart.Checked = !want;
                        syncing = false;
                        Host.ShowToast(L.T("Не удалось изменить автозапуск: ", "Could not change start-up: ") + error, ToastKind.Error);
                        return;
                    }
                    Settings.Autostart = want;
                    Settings.Save();
                });
            }) { IsBackground = true };
            th.Start();
        }
    }
}
