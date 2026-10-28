using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>
    /// A control whose size (not only its drawing) depends on Theme.Scale: called after the zoom
    /// changed, children first, so containers can lay out their already-resized children.
    /// </summary>
    interface IScalable
    {
        void ApplyScale();
    }

    /// <summary>Container that paints its background in one buffered pass (no flashes when children move).</summary>
    class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }

    /// <summary>Base for custom-painted, double-buffered controls that follow the current theme.</summary>
    abstract class SkinControl : Control
    {
        protected const TextFormatFlags TextLeft =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        protected const TextFormatFlags TextCenter =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        protected SkinControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Font = Theme.UiFont;
        }

        protected Color ParentBg
        {
            get { return Parent != null ? Parent.BackColor : Theme.Current.Bg; }
        }

        protected static int S(int v) { return Theme.S(v); }

        protected static int MeasureWidth(string text, Font font)
        {
            return string.IsNullOrEmpty(text) ? 0
                : TextDraw.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        protected static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color c, Font font = null)
        {
            if (!Theme.HasGlyphs || string.IsNullOrEmpty(glyph)) return;
            TextDraw.DrawText(g, glyph, font ?? Theme.Glyph, r, c, TextCenter);
        }
    }

    enum ButtonKind { Normal, Danger, Primary, Ghost }

    /// <summary>Rounded button with animated hover, press, enable/disable and appear transitions.</summary>
    class FlatButton : SkinControl, IScalable
    {
        public string Glyph;
        public ButtonKind Kind;
        /// <summary>Height in design pixels; the real height follows the zoom.</summary>
        int baseHeight = Theme.ControlH;

        public void SetBaseHeight(int h)
        {
            baseHeight = h;
            Height = S(h);
        }

        public void ApplyScale()
        {
            Height = S(baseHeight);
            Width = PreferredWidth;
        }
        readonly Tweened hover, press, enabledT, appear;

        public FlatButton(string text, string glyph, ButtonKind kind = ButtonKind.Normal)
        {
            Text = text;
            Glyph = glyph ?? "";
            Kind = kind;
            Cursor = Cursors.Hand;
            hover = new Tweened(Invalidate);
            press = new Tweened(Invalidate);
            enabledT = new Tweened(Invalidate, 1f);
            appear = new Tweened(Invalidate, 1f);
            Height = S(baseHeight);
            Width = PreferredWidth;
        }

        public int PreferredWidth
        {
            get
            {
                int w = S(12) * 2;
                if (Glyph.Length > 0 && Theme.HasGlyphs) w += S(16) + (Text.Length > 0 ? S(8) : 0);
                return w + MeasureWidth(Text, Font);
            }
        }

        /// <summary>Fades the button in (used when it becomes visible).</summary>
        public void Appear()
        {
            appear.Set(0);
            appear.To(1, 280);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1, 120); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 220); press.To(0, 150); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) press.To(1, 70);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.To(0, 200); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            enabledT.To(Enabled ? 1 : 0, 200);
            if (!Enabled) { hover.Set(0); press.Set(0); }
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        string shownText = "", oldText = "";
        float textFade = 1;
        readonly object textKey = new object();

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            // A visible button cross-fades its caption ("Check" → "Install (3)"); a hidden one just changes.
            if (Visible && IsHandleCreated && shownText.Length > 0 && Text.Length > 0)
            {
                oldText = shownText;
                textFade = 0;
                Anim.Run(textKey, 220, t => { textFade = t; Invalidate(); });
            }
            else
            {
                Anim.Cancel(textKey);
                textFade = 1;
            }
            shownText = Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float h = hover.Value, pr = press.Value;
            Color bg, fg, border, glyphColor;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    bg = Anim.Blend(Anim.Blend(p.Accent, Color.White, h * 0.12f), Color.Black, pr * 0.15f);
                    fg = glyphColor = p.AccentText;
                    border = bg;
                    break;
                case ButtonKind.Ghost:
                    bg = Anim.Blend(Anim.Blend(pb, p.Hover, h), p.Pressed, pr * 0.8f);
                    fg = glyphColor = Anim.Blend(p.TextDim, p.Text, h);
                    border = bg;
                    break;
                default:
                    bg = Anim.Blend(Anim.Blend(p.SurfaceAlt, p.Hover, h), p.Pressed, pr * 0.8f);
                    fg = Kind == ButtonKind.Danger ? Anim.Blend(p.Text, p.Danger, h) : p.Text;
                    glyphColor = Kind == ButtonKind.Danger ? p.Danger : Anim.Blend(p.TextDim, p.Accent, h);
                    border = Anim.Blend(p.Border, p.Hover, h);
                    break;
            }

            float en = enabledT.Value;
            if (en < 1)
            {
                Color dim = Anim.Blend(p.TextDim, pb, 0.45f);
                fg = Anim.Blend(dim, fg, en);
                glyphColor = Anim.Blend(dim, glyphColor, en);
                bg = Anim.Blend(Anim.Blend(bg, pb, 0.45f), bg, en);
                border = Anim.Blend(Anim.Blend(border, pb, 0.45f), border, en);
            }
            float ap = appear.Value;
            if (ap < 1)
            {
                fg = Anim.Blend(pb, fg, ap);
                glyphColor = Anim.Blend(pb, glyphColor, ap);
                bg = Anim.Blend(pb, bg, ap);
                border = Anim.Blend(pb, border, ap);
            }

            float inset = pr * S(1);
            var r = new RectangleF(0.5f + inset, 0.5f + inset, Width - 1 - 2 * inset, Height - 1 - 2 * inset);
            Theme.FillRound(g, bg, r, S(6));
            if (Kind == ButtonKind.Normal || Kind == ButtonKind.Danger) Theme.DrawRound(g, border, r, S(6));

            int content = PreferredWidth - S(24);
            int x = Math.Max(S(12), (Width - content) / 2);
            if (Glyph.Length > 0 && Theme.HasGlyphs)
            {
                DrawGlyph(g, Glyph, new Rectangle(x, 0, S(16), Height), glyphColor);
                x += S(16) + S(8);
            }
            if (Text.Length > 0)
            {
                var tr = new Rectangle(x, 0, Width - x - S(4), Height);
                if (textFade < 1)
                {
                    // Old caption slides up and out, the new one comes up from below.
                    int dy = (int)Math.Round(S(6) * textFade);
                    TextDraw.DrawText(g, oldText, Font, new Rectangle(tr.X, tr.Y - dy, tr.Width, tr.Height), Anim.Blend(bg, fg, 1 - textFade), TextLeft);
                    TextDraw.DrawText(g, Text, Font, new Rectangle(tr.X, tr.Y + S(6) - dy, tr.Width, tr.Height), Anim.Blend(bg, fg, textFade), TextLeft);
                }
                else TextDraw.DrawText(g, Text, Font, tr, fg, TextLeft);
            }
        }
    }

    /// <summary>Tab strip with an accent underline that slides between tabs.</summary>
    class TabBar : SkinControl, IScalable
    {
        public void ApplyScale()
        {
            Relayout();
            Anim.Cancel(indKey);
            var ind = IndicatorTarget(selected);
            indX = ind.X;
            indW = ind.Width;
            Invalidate();
        }

        readonly List<string> tabs = new List<string>();
        readonly List<string> glyphs = new List<string>();
        readonly List<Tweened> hovers = new List<Tweened>();
        readonly List<Tweened> sels = new List<Tweened>();
        Rectangle[] rects = new Rectangle[0];
        int selected, hoverIdx = -1;
        float indX, indW;
        readonly object indKey = new object();

        public event EventHandler SelectedIndexChanged;

        public TabBar()
        {
            Font = Theme.UiBold;
            Cursor = Cursors.Hand;
        }

        public void AddTab(string text, string glyph)
        {
            tabs.Add(text);
            glyphs.Add(glyph);
            hovers.Add(new Tweened(Invalidate));
            sels.Add(new Tweened(Invalidate, tabs.Count - 1 == selected ? 1 : 0));
            Relayout();
            var ind = IndicatorTarget(selected);
            indX = ind.X;
            indW = ind.Width;
        }

        public void SetTabText(int i, string text)
        {
            tabs[i] = text;
            Relayout();
            Anim.Cancel(indKey);
            var ind = IndicatorTarget(selected);
            indX = ind.X;
            indW = ind.Width;
            Invalidate();
        }

        int GlyphSpace { get { return Theme.HasGlyphs ? S(20) + S(8) : 0; } }

        static FontFamily glyphFamily;

        /// <summary>
        /// Tab icons: a little larger than the text and drawn as an outlined shape, so MDL2's hairline strokes
        /// (the font has no bold) read as heavy as the bold tab names.
        /// </summary>
        static void DrawBoldGlyph(Graphics g, string glyph, Rectangle r, Color c)
        {
            if (!Theme.HasGlyphs || string.IsNullOrEmpty(glyph)) return;
            if (glyphFamily == null) glyphFamily = new FontFamily("Segoe MDL2 Assets");
            using (var path = new GraphicsPath())
            {
                path.AddString(glyph, glyphFamily, (int)FontStyle.Regular, 17f * Theme.Scale, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate((float)Math.Round(r.X + (r.Width - b.Width) / 2f - b.X), (float)Math.Round(r.Y + (r.Height - b.Height) / 2f - b.Y));
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
                using (var pen = new Pen(c, 0.85f * Theme.Scale) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
            }
        }

        void Relayout()
        {
            rects = new Rectangle[tabs.Count];
            int x = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                int w = MeasureWidth(tabs[i], Font) + GlyphSpace + S(32);
                rects[i] = new Rectangle(x, 0, w, Height);
                x += w;
            }
            Width = x;
        }

        RectangleF IndicatorTarget(int i)
        {
            var r = rects[i];
            return new RectangleF(r.X + S(16), 0, r.Width - S(32), 0);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= tabs.Count) return;
                int old = selected;
                selected = value;
                sels[old].To(0, 220);
                sels[value].To(1, 220);
                float fx = indX, fw = indW;
                var target = IndicatorTarget(value);
                // As long as the page transition (MainForm): both end on the same frame, before the
                // new page's first real paint.
                Anim.Run(indKey, 260, t =>
                {
                    indX = Anim.Lerp(fx, target.X, t);
                    indW = Anim.Lerp(fw, target.Width, t);
                    Invalidate();
                }, null, Anim.EaseInOut);
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            for (int i = 0; i < rects.Length; i++) rects[i].Height = Height;
        }

        int HitTest(Point pt)
        {
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            if (idx == hoverIdx) return;
            if (hoverIdx >= 0) hovers[hoverIdx].To(0, 200);
            if (idx >= 0) hovers[idx].To(1, 120);
            hoverIdx = idx;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx >= 0) hovers[hoverIdx].To(0, 200);
            hoverIdx = -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx = HitTest(e.Location);
            if (idx >= 0) SelectedIndex = idx;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < tabs.Count; i++)
            {
                var r = rects[i];
                float h = hovers[i].Value, s = sels[i].Value;
                if (h > 0.01f)
                    Theme.FillRound(g, Anim.Blend(pb, p.Hover, h * 0.7f * (1 - s)),
                        new RectangleF(r.X + S(4), r.Y + S(8), r.Width - S(8), r.Height - S(16)), S(6));
                Color c = Anim.Blend(Anim.Blend(p.TextDim, p.Text, h * 0.7f), p.Text, s);
                int x = r.X + S(16);
                if (Theme.HasGlyphs)
                {
                    DrawBoldGlyph(g, glyphs[i], new Rectangle(x, 0, S(20), r.Height), Anim.Blend(c, p.Accent, s));
                    x += GlyphSpace;
                }
                TextDraw.DrawText(g, tabs[i], Font, new Rectangle(x, 0, r.Right - x, r.Height), c, TextLeft);
            }
            Theme.FillRound(g, p.Accent, new RectangleF(indX, Height - S(3), indW, S(3)), S(2));
        }
    }

    /// <summary>On/off switch with a sliding knob.</summary>
    class ToggleSwitch : SkinControl, IScalable
    {
        public void ApplyScale()
        {
            Height = S(32);
            Width = S(36) + S(10) + MeasureWidth(Text, Font) + S(2);
        }

        bool isChecked;
        readonly Tweened knob, hover, enabledT;
        public event EventHandler CheckedChanged;

        public ToggleSwitch(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            knob = new Tweened(Invalidate);
            hover = new Tweened(Invalidate);
            enabledT = new Tweened(Invalidate, 1f);
            Height = S(32);
            Width = S(36) + S(10) + MeasureWidth(text, Font) + S(2);
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (value == isChecked) return;
                isChecked = value;
                knob.To(value ? 1 : 0, 240);
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void Appear()
        {
            // Slide the knob in from "off" so the control visibly arrives.
            knob.Set(0);
            knob.To(isChecked ? 1 : 0, 240);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Width = S(36) + S(10) + MeasureWidth(Text, Font) + S(2);
            Invalidate();
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) hover.To(1, 120); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 220); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            enabledT.To(Enabled ? 1 : 0, 220);
            if (!Enabled) hover.Set(0);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = knob.Value, h = hover.Value, dim = (1 - enabledT.Value) * 0.55f;
            int tw = S(36), th = S(20);
            var tr = new RectangleF(0.5f, (Height - th) / 2f, tw, th);
            Color track = Anim.Blend(Anim.Blend(p.SurfaceAlt, p.Hover, h), p.Accent, k);
            Theme.FillRound(g, Anim.Blend(track, pb, dim), tr, th / 2f);
            Theme.DrawRound(g, Anim.Blend(Anim.Blend(p.Border, p.Accent, k), pb, dim), tr, th / 2f);
            float d = th - S(8);
            float kx = Anim.Lerp(tr.X + S(4), tr.Right - S(4) - d, k);
            using (var b = new SolidBrush(Anim.Blend(Anim.Blend(p.TextDim, Color.White, k), pb, dim)))
                g.FillEllipse(b, kx, tr.Y + S(4), d, d);
            TextDraw.DrawText(g, Text, Font, new Rectangle(tw + S(10), 0, Width - tw - S(10), Height),
                Anim.Blend(Anim.Blend(p.TextDim, p.Text, Math.Max(h, k)), pb, dim), TextLeft);
        }
    }

    /// <summary>Segmented selector with a sliding highlight (used for the theme choice).</summary>
    class Segmented : SkinControl, IScalable
    {
        public void ApplyScale() { FitSize(); }

        readonly string[] texts, glyphs;
        readonly Tweened[] hovers;
        int selected, hoverIdx = -1;
        float pill;
        readonly object pillKey = new object();
        public event EventHandler SelectedIndexChanged;

        readonly Tweened enabledT;

        public Segmented(string[] texts, string[] glyphs, int selected)
        {
            enabledT = new Tweened(Invalidate, 1f);
            this.texts = texts;
            this.glyphs = glyphs;
            this.selected = selected;
            pill = selected;
            hovers = new Tweened[texts.Length];
            for (int i = 0; i < texts.Length; i++) hovers[i] = new Tweened(Invalidate);
            Cursor = Cursors.Hand;
            FitSize();
        }

        public void SetTexts(string[] newTexts)
        {
            Array.Copy(newTexts, texts, texts.Length);
            FitSize();
            Invalidate();
        }

        /// <summary>Left edge and width of each segment: as wide as its own text, so short ones take no extra room.</summary>
        float[] segX = new float[0], segW = new float[0];

        void FitSize()
        {
            segX = new float[texts.Length];
            segW = new float[texts.Length];
            // Equal pads, but never narrower than a glyph-only segment.
            float x = S(3);
            for (int i = 0; i < texts.Length; i++)
            {
                segX[i] = x;
                segW[i] = Math.Max(S(40), MeasureWidth(texts[i], Font) + S(32) + (HasGlyphs ? S(22) : 0));
                x += segW[i];
            }
            Size = new Size((int)Math.Ceiling(x) + S(3), S(Theme.ControlH));
        }

        /// <summary>Where the selection pill is for a fractional position (it slides and resizes between segments).</summary>
        void PillAt(float at, out float x, out float w)
        {
            int a = Math.Max(0, Math.Min(texts.Length - 1, (int)Math.Floor(at)));
            int b = Math.Min(texts.Length - 1, a + 1);
            float t = Math.Max(0, Math.Min(1, at - a));
            x = Anim.Lerp(segX[a], segX[b], t);
            w = Anim.Lerp(segW[a], segW[b], t);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                selected = value;
                float from = pill;
                Anim.Run(pillKey, 300, t => { pill = Anim.Lerp(from, value, t); Invalidate(); }, null, Anim.EaseInOut);
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        bool HasGlyphs
        {
            get
            {
                if (!Theme.HasGlyphs) return false;
                foreach (string gl in glyphs) if (!string.IsNullOrEmpty(gl)) return true;
                return false;
            }
        }

        int HitTest(Point pt)
        {
            for (int i = 0; i < texts.Length; i++)
                if (pt.X >= segX[i] && pt.X < segX[i] + segW[i]) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            if (idx == hoverIdx) return;
            if (hoverIdx >= 0) hovers[hoverIdx].To(0, 200);
            if (idx >= 0) hovers[idx].To(1, 120);
            hoverIdx = idx;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx >= 0) hovers[hoverIdx].To(0, 200);
            hoverIdx = -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            int idx = HitTest(e.Location);
            if (idx >= 0) SelectedIndex = idx;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            enabledT.To(Enabled ? 1 : 0, 220);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float dim = (1 - enabledT.Value) * 0.55f;
            var outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, pb, dim), outer, S(8));
            Theme.DrawRound(g, Anim.Blend(p.Border, pb, dim), outer, S(8));
            float px, pw;
            PillAt(pill, out px, out pw);
            Theme.FillRound(g, Anim.Blend(Anim.Blend(p.Accent, p.Pressed, dim), pb, dim), new RectangleF(px, S(3), pw, Height - S(6)), S(6));
            for (int i = 0; i < texts.Length; i++)
            {
                float on = Math.Max(0, 1 - Math.Abs(pill - i));
                Color c = Anim.Blend(Anim.Blend(Anim.Blend(p.TextDim, p.Text, Enabled ? hovers[i].Value : 0), p.AccentText, on), pb, dim);
                float sw = segW[i];
                int content = MeasureWidth(texts[i], Font) + (HasGlyphs && texts[i].Length > 0 ? S(22) : 0) + (HasGlyphs && texts[i].Length == 0 ? S(16) : 0);
                int x = (int)Math.Round(segX[i] + (sw - content) / 2);
                if (HasGlyphs)
                {
                    DrawGlyph(g, glyphs[i], new Rectangle(x, 0, S(16), Height), c);
                    x += S(22);
                }
                TextDraw.DrawText(g, texts[i], Font, new RectangleF(x, 0, sw, Height), c, TextLeft);
            }
        }
    }

    /// <summary>Search field: borderless TextBox inside a rounded frame whose border lights up on focus.</summary>
    class SearchBox : SkinControl, IScalable
    {
        public void ApplyScale()
        {
            Height = S(Theme.ControlH);
            PerformLayout();
        }

        readonly TextBox box = new TextBox();
        readonly Tweened focus, hover;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        public SearchBox()
        {
            focus = new Tweened(Invalidate);
            hover = new Tweened(Invalidate);
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.UiFont;
            box.GotFocus += (s, e) => focus.To(1, 160);
            box.LostFocus += (s, e) => focus.To(0, 220);
            box.MouseEnter += (s, e) => hover.To(1, 120);
            box.MouseLeave += (s, e) => hover.To(0, 220);
            box.TextChanged += (s, e) => OnTextChanged(e);
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { box.Clear(); e.SuppressKeyPress = true; }
            };
            box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501, (IntPtr)1, cue);
            Controls.Add(box);
            Cursor = Cursors.IBeam;
            Size = new Size(S(300), S(Theme.ControlH));
        }

        public override string Text
        {
            get { return box == null ? "" : box.Text; }
            set { if (box != null) box.Text = value; }
        }

        string cue = "";

        /// <summary>Grey hint shown while the box is empty (EM_SETCUEBANNER).</summary>
        public void SetCue(string text)
        {
            cue = text;
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, (IntPtr)1, cue);
        }

        public void FocusBox()
        {
            box.Focus();
            box.SelectAll();
        }

        public void ApplyTheme()
        {
            box.BackColor = Theme.Current.Surface;
            box.ForeColor = Theme.Current.Text;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); box.Focus(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1, 120); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 220); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int left = S(34);
            box.SetBounds(left, (Height - box.PreferredHeight) / 2 + 1, Math.Max(10, Width - left - S(10)), box.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, p.Surface, r, S(6));
            Color border = Anim.Blend(Anim.Blend(p.Border, p.TextDim, hover.Value * 0.5f), p.Accent, focus.Value);
            Theme.DrawRound(g, border, r, S(6));
            if (focus.Value > 0.01f)
            {
                // Soft outer ring that fades in with focus.
                var ring = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
                Theme.DrawRound(g, Anim.Blend(p.Surface, p.Accent, focus.Value * 0.35f), ring, S(5));
            }
            DrawGlyph(g, "\uE721", new Rectangle(S(10), 0, S(16), Height), Anim.Blend(p.TextDim, p.Accent, focus.Value));
        }
    }

    /// <summary>Thin indeterminate progress line shown while data is loading.</summary>
    class LoadingBar : SkinControl
    {
        readonly Tweened alpha;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object frameKey = new object();
        bool active;

        public LoadingBar()
        {
            alpha = new Tweened(Invalidate);
        }

        public void Start()
        {
            active = true;
            alpha.To(1, 200);
            Anim.Frame(frameKey, () =>
            {
                Invalidate();
                return active || alpha.Value > 0.01f;
            });
        }

        public void Stop()
        {
            active = false;
            alpha.To(0, 400);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            if (alpha.Value <= 0.01f) return;
            float phase = (clock.ElapsedMilliseconds % 1400) / 1400f;
            float eased = Anim.EaseInOut(phase);
            float w = Width * 0.35f;
            float x = -w + eased * (Width + w);
            using (var b = new SolidBrush(Anim.Blend(pb, Theme.Current.Accent, alpha.Value)))
                g.FillRectangle(b, x, 0, w, Height);
        }
    }

    enum ToastKind { Success, Info, Error }

    /// <summary>Notification that slides up in the bottom-right corner and hides itself.</summary>
    class Toast : SkinControl
    {
        readonly Tweened show;
        readonly Timer hideTimer = new Timer();
        ToastKind kind;
        int targetTop;

        public Toast()
        {
            show = new Tweened(OnShowStep);
            hideTimer.Tick += (s, e) => Hide(true);
            Visible = false;
            Cursor = Cursors.Hand;
        }

        public void Show(string text, ToastKind kind, Point bottomRight)
        {
            Text = text;
            this.kind = kind;
            int w = Math.Min(S(520), MeasureWidth(text, Font) + S(60));
            Size = new Size(w, S(44));
            Left = bottomRight.X - w;
            targetTop = bottomRight.Y - Height;
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), S(8)))
                Region = new Region(path);
            if (!Visible) show.Set(0);
            Visible = true;
            BringToFront();
            show.To(1, 280);
            hideTimer.Stop();
            hideTimer.Interval = kind == ToastKind.Error ? 6000 : 3500;
            hideTimer.Start();
            Invalidate();
        }

        public void Reposition(Point bottomRight)
        {
            if (!Visible) return;
            Left = bottomRight.X - Width;
            targetTop = bottomRight.Y - Height;
            OnShowStep();
        }

        void Hide(bool animate)
        {
            hideTimer.Stop();
            if (!animate) { Visible = false; return; }
            show.To(0, 220, () => Visible = false);
        }

        void OnShowStep()
        {
            Top = targetTop + (int)((1 - show.Value) * S(18));
            Invalidate();
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Hide(true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color behind = p.Surface;
            float a = show.Value;
            // The toast is always a dark "elevated" card so it stands out on both themes.
            var d = Theme.Dark;
            Color bg = Anim.Blend(behind, p.IsDark ? d.Pressed : d.SurfaceAlt, a);
            Color fg = Anim.Blend(behind, d.Text, a);
            Color accent = kind == ToastKind.Error ? d.Danger : (kind == ToastKind.Success ? d.Success : d.Accent);
            accent = Anim.Blend(behind, accent, a);
            g.Clear(bg);
            Theme.FillRound(g, accent, new RectangleF(0, 0, S(4), Height), 0);
            string glyph = kind == ToastKind.Error ? "\uE783" : (kind == ToastKind.Success ? "\uE930" : "\uE946");
            DrawGlyph(g, glyph, new Rectangle(S(16), 0, S(18), Height), accent);
            TextDraw.DrawText(g, Text, Font, new Rectangle(S(44), 0, Width - S(56), Height), fg, TextLeft);
        }
    }
}
