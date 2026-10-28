using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Themed replacement for MessageBox that fades and slides in/out.</summary>
    class ThemedDialog : Form
    {
        readonly bool danger;
        bool closingDone, closing;
        DialogResult pending = DialogResult.Cancel;
        int finalTop;

        ThemedDialog(string title, string message, string okText, string okGlyph, string cancelText, string iconGlyph, Color iconColor, bool danger)
        {
            this.danger = danger;
            var p = Theme.Current;
            Text = title;
            Font = Theme.UiFont;
            BackColor = p.Bg;
            ForeColor = p.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            Icon = Logo.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Opacity = 0;

            int S16 = Theme.S(16), pad = Theme.S(24), iconBox = Theme.S(40);
            // A fixed box for the icon: an auto-sized glyph label grows with DPI and covered the first letters.
            var icon = new SmoothLabel
            {
                Text = Theme.HasGlyphs ? iconGlyph : "",
                Font = new Font("Segoe MDL2 Assets", 20f * Theme.Zoom),
                ForeColor = iconColor,
                BackColor = p.Bg,
                AutoSize = false,
                Size = new Size(iconBox, iconBox),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(pad, pad - Theme.S(4)),
            };
            var msg = new SmoothLabel
            {
                Text = message,
                AutoSize = true,
                MaximumSize = new Size(Theme.S(460), 0),
                ForeColor = p.Text,
                BackColor = p.Bg,
                Location = new Point(pad + iconBox + S16, pad + Theme.S(4)),
                UseMnemonic = false,
            };
            Controls.Add(msg);
            Controls.Add(icon);

            var footer = new Panel { BackColor = p.Surface, Height = Theme.S(64), Dock = DockStyle.Bottom };
            footer.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            Controls.Add(footer);

            var ok = new FlatButton(okText, okGlyph, danger ? ButtonKind.Danger : ButtonKind.Primary);
            ok.Width = Math.Max(ok.PreferredWidth, Theme.S(110));
            ok.Click += (s, e) => Finish(DialogResult.OK);
            footer.Controls.Add(ok);
            FlatButton cancel = null;
            if (cancelText != null)
            {
                cancel = new FlatButton(cancelText, "");
                cancel.Width = Math.Max(cancel.PreferredWidth, Theme.S(110));
                cancel.Click += (s, e) => Finish(DialogResult.Cancel);
                footer.Controls.Add(cancel);
            }

            // A long message (a list of risky settings) scrolls inside the dialog instead of running off the screen.
            Size text = msg.PreferredSize;
            var screen = Screen.FromPoint(Control.MousePosition).WorkingArea;
            int maxText = Math.Max(Theme.S(160), screen.Height - footer.Height - pad * 2 - Theme.S(96));
            Control body = msg;
            if (text.Height > maxText)
            {
                var host = new Panel { AutoScroll = true, BackColor = p.Bg, Location = msg.Location, Size = new Size(text.Width + SystemInformation.VerticalScrollBarWidth + Theme.S(8), maxText) };
                host.HandleCreated += (s, e) => Native.SetWindowTheme(host.Handle, Theme.Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
                Controls.Remove(msg);
                msg.Location = Point.Empty;
                host.Controls.Add(msg);
                Controls.Add(host);
                body = host;
                text = host.Size;
                Shown += (s, e) => host.Focus();
                MouseWheel += (s, e) => host.AutoScrollPosition = new Point(0, -host.AutoScrollPosition.Y - e.Delta);
            }
            int contentW = body.Left + text.Width + pad;
            int contentH = Math.Max(icon.Bottom, body.Top + text.Height) + pad;
            ClientSize = new Size(Math.Max(contentW, Theme.S(400)), contentH + footer.Height);

            int bx = ClientSize.Width - pad;
            int by = (footer.Height - ok.Height) / 2;
            if (cancel != null)
            {
                cancel.Location = new Point(bx - cancel.Width, by);
                bx = cancel.Left - S16 / 2;
            }
            ok.Location = new Point(bx - ok.Width, by);
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
            int offset = Theme.S(14);
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
            else if (e.KeyCode == Keys.Enter && !danger) Finish(DialogResult.OK);
        }

        void Finish(DialogResult result)
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
                // Title-bar close or Alt+F4: fade out first, then close for real.
                e.Cancel = true;
                Finish(DialogResult.Cancel);
                return;
            }
            base.OnFormClosing(e);
        }

        public static bool Confirm(IWin32Window owner, string title, string message, string okText, string okGlyph, bool danger)
        {
            var p = Theme.Current;
            using (var d = new ThemedDialog(title, message, okText, okGlyph, L.T("Отмена", "Cancel"), "\uE7BA", danger ? p.Danger : p.Warning, danger))
                return d.ShowDialog(owner) == DialogResult.OK;
        }

        public static void Info(IWin32Window owner, string title, string message, bool error)
        {
            var p = Theme.Current;
            using (var d = new ThemedDialog(title, message, "OK", "", null, error ? "\uE783" : "\uE946", error ? p.Danger : p.Accent, false))
            {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                d.ShowDialog(owner);
            }
        }
    }

    class MenuTag
    {
        public string Glyph;
        public bool Danger;
    }

    /// <summary>Context menu renderer that follows the current theme; glyphs come from the item's MenuTag.</summary>
    class ThemedRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Current.SurfaceAlt)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var r = new Rectangle(Point.Empty, e.Item.Size);
            if (e.Item.Selected && e.Item.Enabled)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, p.Hover, new RectangleF(Theme.S(4), 1, r.Width - Theme.S(8), r.Height - 2), Theme.S(4));
            }
            var tag = e.Item.Tag as MenuTag;
            if (tag != null && Theme.HasGlyphs)
            {
                Color c = !e.Item.Enabled ? Anim.Blend(p.TextDim, p.SurfaceAlt, 0.5f) : (tag.Danger ? p.Danger : p.TextDim);
                TextDraw.DrawText(g, tag.Glyph, Theme.Glyph, new Rectangle(Theme.S(8), 0, Theme.S(20), r.Height), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var p = Theme.Current;
            var tag = e.Item.Tag as MenuTag;
            e.TextColor = !e.Item.Enabled ? Anim.Blend(p.TextDim, p.SurfaceAlt, 0.5f)
                : (tag != null && tag.Danger && e.Item.Selected ? p.Danger : p.Text);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Theme.Current.Border))
                e.Graphics.DrawLine(pen, Theme.S(8), y, e.Item.Width - Theme.S(8), y);
        }
    }
}
