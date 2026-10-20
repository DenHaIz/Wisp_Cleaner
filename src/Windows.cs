using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Themed secondary window: fades and slides in, footer with buttons, dark/light title bar.</summary>
    class ThemedWindow : Form
    {
        protected readonly Panel Footer = new BufferedPanel();
        protected readonly Panel Body = new BufferedPanel();
        readonly List<FlatButton> buttons = new List<FlatButton>();
        bool closingDone, closing;
        DialogResult pending = DialogResult.Cancel;
        int finalTop;

        protected static int S(int v) { return Theme.S(v); }

        public ThemedWindow(string title, Size client)
        {
            var p = Theme.Current;
            Text = title;
            Font = Theme.UiFont;
            BackColor = p.Bg;
            ForeColor = p.Text;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Icon = Logo.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Opacity = 0;
            ClientSize = client;
            MinimumSize = new Size(S(520), S(360));

            Footer.Dock = DockStyle.Bottom;
            Footer.Height = S(64);
            Footer.BackColor = p.Surface;
            Footer.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, Footer.Width, 0);
            };
            Footer.Resize += (s, e) => LayoutButtons();
            Body.Dock = DockStyle.Fill;
            Body.BackColor = p.Bg;
            Body.Padding = new Padding(S(16), S(16), S(16), S(16));
            Controls.Add(Body);
            Controls.Add(Footer);
        }

        protected FlatButton AddButton(string text, string glyph, ButtonKind kind, DialogResult result)
        {
            var b = new FlatButton(text, glyph, kind);
            b.Width = Math.Max(b.PreferredWidth, S(110));
            b.Click += (s, e) => Finish(result);
            buttons.Add(b);
            Footer.Controls.Add(b);
            LayoutButtons();
            return b;
        }

        void LayoutButtons()
        {
            int x = Footer.Width - S(24);
            foreach (var b in buttons)
            {
                b.Location = new Point(x - b.Width, (Footer.Height - b.Height) / 2);
                x = b.Left - S(8);
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
            finalTop = Top;
            int offset = S(14);
            WindowFx.PaintNow(this);
            Anim.Run(this, 220, t =>
            {
                Opacity = t;
                Top = finalTop + (int)((1 - t) * offset);
            }, () => WindowFx.EndFade(this));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
        }

        protected void Finish(DialogResult result)
        {
            if (closing) return;
            closing = true;
            pending = result;
            float from = (float)Opacity;
            Anim.Run(this, 150, t => Opacity = from * (1 - t), () =>
            {
                closingDone = true;
                DialogResult = pending;
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingDone)
            {
                e.Cancel = true;
                Finish(DialogResult.Cancel);
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Simple row for lists that only show text.</summary>
    class TextRow : IRowInfo
    {
        public RowKind Kind;
        public string Icon;
        public RowKind RowKind { get { return Kind; } }
        public string IconPath { get { return Icon; } }
        public int Dot { get { return 0; } }
    }

    /// <summary>The cleaning log: when, what kind, how much was freed.</summary>
    class HistoryForm : ThemedWindow
    {
        HistoryForm() : base(L.T("История очисток", "Cleaning history"), new Size(Theme.S(820), Theme.S(520)))
        {
            var entries = History.Load();
            var frame = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Current.Border };
            var lv = new ThemedListView { Dock = DockStyle.Fill, MultiSelect = false };
            lv.Columns.Add(L.T("Когда", "When"), S(160));
            lv.Columns.Add(L.T("Тип", "Kind"), S(130));
            lv.Columns.Add(L.T("Освобождено", "Freed"), S(120));
            lv.Columns.Add(L.T("Что очищено", "What was cleaned"), S(360));
            lv.IconColumn = false;
            lv.Numeric = new[] { false, false, true, false };
            lv.ListViewItemSorter = null;
            lv.EmptyText = L.T("Очисток ещё не было", "No cleanups yet");
            lv.EmptyGlyph = "\uE81C";
            foreach (var e in entries)
            {
                var row = new ListViewItem(new[] { e.When.ToString("g"), e.KindText, L.Size(e.Bytes), e.Details })
                {
                    Tag = new TextRow { Kind = e.Bytes > 0 ? RowKind.Normal : RowKind.Dim }
                };
                lv.Items.Add(row);
            }
            frame.Controls.Add(lv);
            Body.Controls.Add(frame);

            long total = entries.Sum(e => e.Bytes);
            var summary = new StatusText { Text = L.T("Всего освобождено: ", "Freed in total: ") + L.Size(total) + "   ·   " + Settings.LogPath };
            summary.SetBounds(S(20), 0, S(560), Footer.Height);
            summary.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Footer.Controls.Add(summary);
            AddButton(L.T("Закрыть", "Close"), "", ButtonKind.Normal, DialogResult.OK);
        }

        public static void ShowFor(IWin32Window owner)
        {
            using (var f = new HistoryForm()) f.ShowDialog(owner);
        }
    }
}
