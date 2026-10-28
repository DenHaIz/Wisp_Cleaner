using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>Rounded surface panel (8 px corners, 1 px border); children sit on the Surface color.</summary>
    class Card : BufferedPanel, IScalable
    {
        public string Title = "";
        /// <summary>Title strip height in design pixels.</summary>
        public int TitleBase;
        public int TitleHeight { get { return Theme.S(TitleBase); } }

        public void ApplyScale()
        {
            Padding = new Padding(Theme.S(1));
            Invalidate();
        }

        public Card()
        {
            Padding = new Padding(Theme.S(1));
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Current.Surface;
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, p.Surface, r, Theme.S(8));
            Theme.DrawRound(g, p.Border, r, Theme.S(8));
            if (Title.Length > 0 && TitleHeight > 0)
                TextDraw.DrawText(g, Title, Theme.HeadFont, new Rectangle(Theme.S(18), 0, Width - Theme.S(36), TitleHeight), p.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A line of text that fades in whenever it changes (footer status, hints).</summary>
    class StatusText : SkinControl
    {
        readonly Tweened alpha;
        public Color? ForeOverride;
        public TextFormatFlags Flags = TextLeft;
        string previous = "";

        public StatusText()
        {
            alpha = new Tweened(Invalidate, 1f);
        }

        /// <summary>Changes the text: the old one fades out as the new one fades in.</summary>
        public void SetText(string text)
        {
            text = text ?? "";
            if (text == Text) return;
            // Mid-fade the line still mostly shows the old text: keep fading that one out.
            if (alpha.Value >= 0.5f) previous = Text;
            Text = text;
            alpha.Set(0);
            alpha.To(1, 320);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color pb = ParentBg;
            e.Graphics.Clear(pb);
            Color fg = ForeOverride ?? Theme.Current.TextDim;
            float a = alpha.Value;
            // Out, then in: the two texts never overlap.
            if (a < 0.5f && previous.Length > 0)
                TextDraw.DrawText(e.Graphics, previous, Font, ClientRectangle, Anim.Blend(pb, fg, 1 - a * 2), Flags);
            if (a > 0.5f)
                TextDraw.DrawText(e.Graphics, Text, Font, ClientRectangle, Anim.Blend(pb, fg, (a - 0.5f) * 2), Flags);
        }
    }

    /// <summary>Animated check box drawing shared by the category list and the lists.</summary>
    static class CheckMark
    {
        /// <summary>value 0..1 = how checked (animates the tick being drawn); partial = some children checked.</summary>
        public static void Draw(Graphics g, RectangleF r, float value, bool partial, float hover, bool enabled, Color bg)
        {
            var p = Theme.Current;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float on = partial ? 1f : value;
            Color border = Anim.Blend(Anim.Blend(Anim.Blend(p.Border, p.TextDim, 0.45f), p.TextDim, hover), p.Accent, on);
            Color fill = Anim.Blend(Anim.Blend(p.Surface, p.Hover, hover * 0.5f), p.Accent, on);
            if (!enabled)
            {
                border = Anim.Blend(border, bg, 0.55f);
                fill = Anim.Blend(fill, bg, 0.55f);
            }
            Theme.FillRound(g, fill, r, Theme.S(4));
            Theme.DrawRound(g, border, r, Theme.S(4));
            using (var pen = new Pen(enabled ? Color.White : Anim.Blend(Color.White, bg, 0.4f), Math.Max(1.6f, r.Width * 0.13f))
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (partial)
                {
                    float y = r.Y + r.Height / 2;
                    g.DrawLine(pen, r.X + r.Width * 0.28f, y, r.Right - r.Width * 0.28f, y);
                }
                else if (value > 0.01f)
                {
                    // The tick is drawn on as the box fills: first stroke, then the long one.
                    var a = new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.53f);
                    var b = new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f);
                    var c = new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.32f);
                    float t1 = Math.Min(1, value / 0.4f), t2 = Math.Max(0, (value - 0.4f) / 0.6f);
                    var b1 = new PointF(Anim.Lerp(a.X, b.X, t1), Anim.Lerp(a.Y, b.Y, t1));
                    if (t2 <= 0) g.DrawLine(pen, a, b1);
                    else g.DrawLines(pen, new[] { a, b, new PointF(Anim.Lerp(b.X, c.X, t2), Anim.Lerp(b.Y, c.Y, t2)) });
                }
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>
    /// Icon glyphs drawn as shapes: centred on their actual ink (a glyph's text box is not centred
    /// on its drawing), scaled to a given size and made heavier by an outline of the same colour.
    /// Segoe MDL2 has a single thin weight, which looked faint inside the category tiles.
    /// </summary>
    static class GlyphShape
    {
        // Shapes centred on (0,0), keyed by glyph and size.
        static readonly Dictionary<string, GraphicsPath> cache = new Dictionary<string, GraphicsPath>();

        static GraphicsPath Get(string glyph, float ink)
        {
            string key = glyph + "|" + Math.Round(ink * 4);
            GraphicsPath path;
            if (cache.TryGetValue(key, out path)) return path;
            path = new GraphicsPath(FillMode.Winding);
            using (var family = new FontFamily("Segoe MDL2 Assets"))
                path.AddString(glyph, family, 0, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width > 0 && b.Height > 0)
                using (var m = new Matrix())
                {
                    float k = ink / Math.Max(b.Width, b.Height);
                    m.Scale(k, k);
                    m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                    path.Transform(m);
                }
            cache[key] = path;
            return path;
        }

        /// <summary>Draws the glyph so its drawing fits a square of size ink centred on c; weight thickens the lines.</summary>
        public static void Draw(Graphics g, string glyph, PointF c, float ink, Color color, float weight)
        {
            if (!Theme.HasGlyphs || string.IsNullOrEmpty(glyph)) return;
            var path = Get(glyph, ink);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(c.X, c.Y);
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            if (weight > 0)
                using (var pen = new Pen(color, weight) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            g.Restore(state);
        }
    }

    /// <summary>
    /// Placeholder bars with a soft light band sweeping across them, shown where a list is still
    /// loading. All placeholders share one clock, so bands on the same screen move in step.
    /// </summary>
    static class Shimmer
    {
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        const int Period = 1500;

        public static Brush Make(int width, Color bg)
        {
            var p = Theme.Current;
            Color bar = Anim.Blend(bg, p.Pressed, p.IsDark ? 0.5f : 0.55f);
            Color hi = Anim.Blend(bar, p.IsDark ? p.TextDim : Color.White, p.IsDark ? 0.16f : 0.75f);
            float w = Math.Max(1, width);
            float band = Math.Max(Theme.S(140), w * 0.3f);
            float left = -2 * band, span = w + 4 * band;
            float phase = Anim.EaseInOut((clock.ElapsedMilliseconds % Period) / (float)Period);
            float c = -band + phase * (w + 2 * band);
            var brush = new LinearGradientBrush(new RectangleF(left, 0, span, 1), bar, bar, 0f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { bar, bar, hi, bar, bar },
                Positions = new[] { 0f, (c - band / 2 - left) / span, (c - left) / span, (c + band / 2 - left) / span, 1f },
            };
            return brush;
        }

        public static void Bar(Graphics g, Brush b, RectangleF r)
        {
            using (var path = Theme.RoundRect(r, Math.Min(r.Height, r.Width) / 2))
                g.FillPath(b, path);
        }

        public static void Tile(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = Theme.RoundRect(r, radius))
                g.FillPath(b, path);
        }

        /// <summary>A stable pseudo-random 0..1 per (row, column), so placeholder bars vary in length.</summary>
        public static float Vary(int row, int col)
        {
            unchecked
            {
                uint h = (uint)(row * 73856093) ^ (uint)(col * 19349663 + 7);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h % 1000) / 1000f;
            }
        }

        /// <summary>Repaints the control every frame while loading() holds.</summary>
        public static void Run(Control c, object key, Func<bool> loading)
        {
            Anim.Frame(key, () =>
            {
                if (c.IsDisposed || !loading()) return false;
                if (c.Visible) c.Invalidate();
                return true;
            });
        }
    }

    // ---------------------------------------------------------------------------------------------

    class CatItem
    {
        public object Tag;
        public Func<string> Name, Note;
        public long Size = -1;        // -1 = not measured yet
        public bool Checked, Warn, Disabled;
        public Func<string> DisabledText;
        /// <summary>
        /// A link row instead of a check row: no check box, an "open" mark; a click calls this
        /// (e.g. a newer driver on the vendor's site). Link rows never count as ticked.
        /// </summary>
        public Action Open;
        /// <summary>Shown in the size column instead of a size (link rows: the vendor).</summary>
        public string Badge;
        /// <summary>
        /// Link rows: the badge becomes a button that calls this (e.g. "download and install"); the rest
        /// of the row still calls Open.
        /// </summary>
        public Action Act;
    }

    class CatGroup
    {
        public object Tag;
        public Func<string> Name, Desc;
        public string Glyph = "";
        public bool Expanded;
        public readonly List<CatItem> Items = new List<CatItem>();

        public long CheckedSize
        {
            get
            {
                long s = 0;
                foreach (var i in Items) if (i.Checked && !i.Disabled && i.Open == null && i.Size > 0) s += i.Size;
                return s;
            }
        }

        public long TotalSize
        {
            get
            {
                long s = 0;
                foreach (var i in Items) if (i.Size > 0) s += i.Size;
                return s;
            }
        }

        public bool Measured
        {
            get
            {
                foreach (var i in Items) if (i.Size < 0 && !i.Disabled && i.Open == null) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// Expandable list of groups with check boxes and sizes (the "what to clean" list). Custom
    /// painted: expanding, checking, hovering, sizes arriving and scrolling are all animated.
    /// </summary>
    class CategoryList : SkinControl
    {
        public readonly List<CatGroup> Groups = new List<CatGroup>();
        public event EventHandler CheckedChanged;

        readonly Dictionary<object, float> hover = new Dictionary<object, float>();
        readonly Dictionary<object, float> check = new Dictionary<object, float>();
        readonly Dictionary<object, float> expand = new Dictionary<object, float>();
        readonly Dictionary<object, float> sizeIn = new Dictionary<object, float>();
        object hoverObj;
        float scroll, scrollTarget;
        readonly object scrollKey = new object();
        readonly Tweened barHover;
        bool dragging;
        int dragStartY;
        float dragStartScroll;

        int GroupH { get { return S(58); } }
        int ItemH { get { return S(38); } }

        bool loading;
        readonly object shimmerKey = new object();
        /// <summary>Shown when there are no groups and the list is not loading.</summary>
        public string EmptyText = "";
        /// <summary>Optional glyph drawn large above EmptyText, both centred (the "all done" look).</summary>
        public string EmptyGlyph = "";
        /// <summary>While true, clicks do not change the ticks (an operation is running on them).</summary>
        public bool Locked;

        /// <summary>While true and empty, placeholder rows shimmer in place of the groups.</summary>
        public bool Loading
        {
            get { return loading; }
            set
            {
                if (loading == value) return;
                loading = value;
                if (value) Shimmer.Run(this, shimmerKey, () => loading && Groups.Count == 0);
                Invalidate();
            }
        }

        public CategoryList()
        {
            barHover = new Tweened(Invalidate);
            Cursor = Cursors.Hand;
        }

        public void SetGroups(IEnumerable<CatGroup> groups)
        {
            Groups.Clear();
            Groups.AddRange(groups);
            hover.Clear();
            check.Clear();
            expand.Clear();
            sizeIn.Clear();
            hoverObj = null;
            hoverAct = null;
            foreach (var g in Groups)
            {
                expand[g] = g.Expanded ? 1 : 0;
                foreach (var i in g.Items) check[i] = i.Checked ? 1 : 0;
            }
            scroll = scrollTarget = 0;
            loading = false;
            Invalidate();
        }

        /// <summary>Call after an item's Size changed; the size fades in.</summary>
        public void SizeArrived(CatItem item)
        {
            var it = item;
            Anim.Run(Tuple.Create(this, (object)it, "sz"), 420, t => { sizeIn[it] = t; Invalidate(); });
        }

        public void RefreshChecks()
        {
            foreach (var g in Groups)
                foreach (var i in g.Items) AnimateCheck(i);
        }

        void AnimateCheck(CatItem i)
        {
            float from;
            if (!check.TryGetValue(i, out from)) from = 0;
            float to = i.Checked ? 1 : 0;
            if (Math.Abs(from - to) < 0.001f) return;
            var it = i;
            Anim.Run(Tuple.Create(this, (object)it, "c"), to > from ? 260 : 160, t => { check[it] = from + (to - from) * t; Invalidate(); });
        }

        void Animate(Dictionary<object, float> map, object key, string tag, float to, int ms, Func<float, float> ease = null)
        {
            float from;
            if (!map.TryGetValue(key, out from)) from = 0;
            Anim.Run(Tuple.Create(this, key, tag), ms, t => { map[key] = from + (to - from) * t; ClampScroll(); Invalidate(); }, null, ease);
        }

        float Get(Dictionary<object, float> map, object key)
        {
            float v;
            return map.TryGetValue(key, out v) ? v : 0;
        }

        float ContentHeight
        {
            get
            {
                float h = 0;
                foreach (var g in Groups) h += GroupH + g.Items.Count * ItemH * Get(expand, g);
                return h + S(4);
            }
        }

        float MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        void ClampScroll()
        {
            float max = MaxScroll;
            if (scrollTarget > max) scrollTarget = max;
            if (scroll > max) scroll = max;
        }

        void ScrollTo(float target)
        {
            scrollTarget = Math.Max(0, Math.Min(MaxScroll, target));
            float from = scroll, to = scrollTarget;
            Anim.Run(scrollKey, 220, t => { scroll = from + (to - from) * t; Invalidate(); });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scrollTarget - e.Delta / 120f * ItemH * 2.5f);
        }

        // ---------- hit testing ----------

        enum Part { None, GroupCheck, GroupBody, Item, Act }

        Part HitTest(Point pt, out object obj)
        {
            obj = null;
            float y = -scroll;
            foreach (var g in Groups)
            {
                if (pt.Y >= y && pt.Y < y + GroupH)
                {
                    obj = g;
                    return pt.X < S(48) ? Part.GroupCheck : Part.GroupBody;
                }
                y += GroupH;
                float block = g.Items.Count * ItemH * Get(expand, g);
                if (pt.Y >= y && pt.Y < y + block)
                {
                    int idx = (int)((pt.Y - y) / ItemH);
                    if (idx >= 0 && idx < g.Items.Count)
                    {
                        var item = g.Items[idx];
                        obj = item;
                        if (item.Act != null && !item.Disabled && ActRect(item, y + idx * ItemH, Width - S(10)).Contains(pt)) return Part.Act;
                        return Part.Item;
                    }
                }
                y += block;
            }
            return Part.None;
        }

        /// <summary>The button of a row with an Act: its caption (Badge) in a pill at the right end of the row.</summary>
        RectangleF ActRect(CatItem item, float y, int w)
        {
            float bw = MeasureWidth(item.Badge ?? "", Font) + S(24);
            return new RectangleF(w - S(32) - bw, y + S(6), bw, ItemH - S(12));
        }

        object hoverAct;

        void SetHoverAct(object item)
        {
            if (item == hoverAct) return;
            if (hoverAct != null) Animate(hover, Tuple.Create(hoverAct, "act"), "ha", 0, 220);
            if (item != null) Animate(hover, Tuple.Create(item, "act"), "ha", 1, 110);
            hoverAct = item;
        }

        Rectangle ThumbRect()
        {
            float content = ContentHeight;
            if (content <= Height + 1) return Rectangle.Empty;
            int h = Math.Max(S(32), (int)(Height * Height / content));
            int y = (int)((Height - h) * (scroll / Math.Max(1, content - Height)));
            return new Rectangle(Width - S(8), y, S(6), h);
        }

        void SetHover(object o)
        {
            if (o == hoverObj) return;
            if (hoverObj != null) Animate(hover, hoverObj, "h", 0, 220);
            if (o != null) Animate(hover, o, "h", 1, 110);
            hoverObj = o;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                var tr = ThumbRect();
                float range = Height - tr.Height;
                if (range > 0)
                {
                    scroll = scrollTarget = Math.Max(0, Math.Min(MaxScroll, dragStartScroll + (e.Y - dragStartY) * MaxScroll / range));
                    Anim.Cancel(scrollKey);
                    Invalidate();
                }
                return;
            }
            bool onBar = ThumbRect().Width > 0 && e.X >= Width - S(12);
            barHover.To(onBar ? 1 : 0, onBar ? 120 : 260);
            object o = null;
            var part = onBar ? Part.None : HitTest(e.Location, out o);
            var item = o as CatItem;
            if (item != null && item.Disabled) o = null;
            SetHover(o);
            SetHoverAct(part == Part.Act ? o : null);
            Cursor = o != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
            SetHoverAct(null);
            if (!dragging) barHover.To(0, 260);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var tr = ThumbRect();
            if (tr.Width > 0 && e.X >= Width - S(12))
            {
                if (tr.Contains(new Point(tr.X, e.Y)))
                {
                    dragging = true;
                    dragStartY = e.Y;
                    dragStartScroll = scroll;
                    Capture = true;
                }
                else ScrollTo(scrollTarget + (e.Y < tr.Y ? -Height : Height) * 0.9f);
                return;
            }
            object o;
            var part = HitTest(e.Location, out o);
            var g = o as CatGroup;
            var item = o as CatItem;
            if (part == Part.Act && item != null)
            {
                item.Act();
                return;
            }
            if (part == Part.Item && item != null && item.Open != null)
            {
                item.Open();
                return;
            }
            if (part == Part.GroupCheck && g != null && !g.Items.Exists(i => i.Open == null && !i.Disabled)) part = Part.GroupBody;
            if (Locked && (part == Part.GroupCheck || part == Part.Item)) return;
            if (part == Part.GroupCheck && g != null)
            {
                bool all = true;
                foreach (var i in g.Items) if (!i.Disabled && !i.Checked) all = false;
                foreach (var i in g.Items) if (!i.Disabled) { i.Checked = !all; AnimateCheck(i); }
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
            else if (part == Part.GroupBody && g != null)
            {
                g.Expanded = !g.Expanded;
                Animate(expand, g, "e", g.Expanded ? 1 : 0, 320, Anim.EaseInOut);
                if (g.Expanded)
                {
                    // Bring the opened group into view once it has grown.
                    var grp = g;
                    Anim.Delay(330, () =>
                    {
                        float top = -scroll, y = 0;
                        foreach (var x in Groups) { if (x == grp) break; y += GroupH + x.Items.Count * ItemH * Get(expand, x); }
                        float bottom = y + GroupH + grp.Items.Count * ItemH;
                        if (bottom - scrollTarget > Height) ScrollTo(Math.Min(y, bottom - Height + S(8)));
                    });
                }
            }
            else if (part == Part.Item && item != null && !item.Disabled)
            {
                item.Checked = !item.Checked;
                AnimateCheck(item);
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
            }
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            if (Groups.Count == 0)
            {
                if (loading) PaintSkeleton(g, bg);
                else if (EmptyGlyph.Length > 0 && Theme.HasGlyphs)
                {
                    // Accent tile with the glyph, the text under it, both in the middle of the list.
                    int tile = S(64), top = (Height - tile - S(16) - S(40)) / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var box = new RectangleF((Width - tile) / 2f, top, tile, tile);
                    Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.16f), box, S(16));
                    GlyphShape.Draw(g, EmptyGlyph, new PointF(box.X + tile / 2f, box.Y + tile / 2f), S(28), p.Accent, Math.Max(0.8f, S(1) * 1.1f));
                    TextDraw.DrawText(g, EmptyText, Font, new Rectangle(S(16), top + tile + S(16), Width - S(32), S(40)), p.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    g.SmoothingMode = SmoothingMode.None;
                }
                else if (EmptyText.Length > 0)
                    TextDraw.DrawText(g, EmptyText, Font, new Rectangle(0, S(24), Width, S(40)), p.TextDim, TextCenter);
                return;
            }
            // Whole pixels everywhere: text snaps to pixels, so shapes must too or rows shimmer while moving.
            float y = -(float)Math.Round(scroll);
            int w = Width - S(10);
            for (int gi = 0; gi < Groups.Count; gi++)
            {
                var grp = Groups[gi];
                float ex = Get(expand, grp);
                float block = (float)Math.Round(grp.Items.Count * ItemH * ex);
                if (y + GroupH + block >= 0 && y < Height) PaintGroup(g, grp, y, w, bg, ex);
                y += GroupH;
                if (block > 0.5f && y < Height && y + block > 0)
                {
                    var state = g.Save();
                    g.SetClip(new RectangleF(0, y, Width, block));
                    for (int i = 0; i < grp.Items.Count; i++)
                    {
                        float iy = (float)Math.Round(y + i * ItemH - (1 - ex) * S(10));
                        if (iy + ItemH < 0 || iy > Height) continue;
                        PaintItem(g, grp.Items[i], iy, w, bg, ex);
                    }
                    g.Restore(state);
                }
                y += block;
                if (gi < Groups.Count - 1)
                    using (var pen = new Pen(p.Border)) g.DrawLine(pen, S(16), (int)y, w - S(6), (int)y);
            }

            var tr = ThumbRect();
            if (tr.Width > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float bh = barHover.Value;
                var r = new RectangleF(tr.X + (1 - bh) * S(2), tr.Y, tr.Width - (1 - bh) * S(2), tr.Height);
                Theme.FillRound(g, Anim.Blend(p.Border, p.TextDim, bh * 0.6f), r, r.Width / 2);
            }
        }

        void PaintSkeleton(Graphics g, Color bg)
        {
            var p = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width - S(10);
            using (var b = Shimmer.Make(Width, bg))
            using (var pen = new Pen(p.Border))
            {
                int y = 0;
                for (int i = 0; y < Height; i++)
                {
                    float cy = y + GroupH / 2f;
                    Shimmer.Tile(g, b, new RectangleF(S(18), cy - S(9), S(18), S(18)), S(4));
                    Shimmer.Tile(g, b, new RectangleF(S(48), cy - S(17), S(34), S(34)), S(8));
                    float room = Math.Max(S(60), w - S(96) - S(170));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(14), room * (0.28f + 0.3f * Shimmer.Vary(i, 0)), S(12)));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(35), room * (0.45f + 0.4f * Shimmer.Vary(i, 1)), S(9)));
                    Shimmer.Bar(g, b, new RectangleF(w - S(40) - S(64), cy - S(6), S(64), S(12)));
                    y += GroupH;
                    if (y < Height) g.DrawLine(pen, S(16), y, w - S(6), y);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        string SizeText(long size)
        {
            return size < 0 ? "…" : (size == 0 ? "0" : L.Size(size));
        }

        void PaintGroup(Graphics g, CatGroup grp, float y, int w, Color bg, float ex)
        {
            var p = Theme.Current;
            float h = Get(hover, grp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.55f), new RectangleF(S(6), y + S(4), w - S(6), GroupH - S(8)), S(6));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.55f);

            // group check box: all / some / none of the enabled items
            int on = 0, total = 0;
            float sum = 0;
            bool links = false;
            foreach (var i in grp.Items)
            {
                if (i.Open != null) links = true;
                if (i.Disabled || i.Open != null) continue;
                total++;
                if (i.Checked) on++;
                sum += Get(check, i);
            }
            bool partial = on > 0 && on < total;
            float value = total > 0 ? sum / total : 0;
            bool linkGroup = links && total == 0;
            if (linkGroup) value = 0.6f;
            else CheckMark.Draw(g, new RectangleF(S(18), y + (GroupH - S(18)) / 2f, S(18), S(18)), partial ? 1 : value, partial, h, total > 0, rowBg);

            // icon tile
            var tile = new RectangleF(S(48), y + (GroupH - S(36)) / 2f, S(36), S(36));
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.12f + 0.1f * value), tile, S(9));
            float ink = S(18);
            GlyphShape.Draw(g, grp.Glyph, new PointF(tile.X + tile.Width / 2, tile.Y + tile.Height / 2), ink,
                Anim.Blend(p.TextDim, p.Accent, 0.35f + 0.65f * value), Math.Max(0.8f, ink * 0.055f));

            int tx = S(96);
            int sizeW = S(110);
            var titleR = new Rectangle(tx, (int)y + S(10), w - tx - sizeW - S(40), S(20));
            TextDraw.DrawText(g, grp.Name(), Theme.UiBold, titleR, p.Text, TextLeft);
            TextDraw.DrawText(g, grp.Desc(), Theme.SmallFont, new RectangleF(tx, y + S(30), titleR.Width, S(18)), p.TextDim, TextLeft);

            bool measured = grp.Measured;
            string size = linkGroup ? "" : (measured ? SizeText(grp.CheckedSize) : "…");
            TextDraw.DrawText(g, size, Theme.UiBold, new RectangleF(w - sizeW - S(40), y, sizeW, GroupH),
                on == 0 ? p.TextDim : p.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            // chevron rotates from "right" to "down" as the group opens
            var c = new PointF(w - S(18), y + GroupH / 2f);
            float k = S(4);
            double ang = (ex * 90 - 90) * Math.PI / 180;
            Func<float, float, PointF> rot = (dx, dy) => new PointF(
                c.X + (float)(dx * Math.Cos(ang) - dy * Math.Sin(ang)), c.Y + (float)(dx * Math.Sin(ang) + dy * Math.Cos(ang)));
            using (var pen = new Pen(Anim.Blend(p.TextDim, p.Text, h), Math.Max(1.4f, S(2) * 0.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { rot(-k, -k / 2), rot(0, k / 2), rot(k, -k / 2) });
            g.SmoothingMode = SmoothingMode.None;
        }

        void PaintItem(Graphics g, CatItem item, float y, int w, Color bg, float ex)
        {
            var p = Theme.Current;
            float h = Get(hover, item);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.5f), new RectangleF(S(40), y + S(3), w - S(40), ItemH - S(6)), S(6));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.5f);
            Color text = item.Disabled ? Anim.Blend(p.TextDim, bg, 0.3f) : p.Text;
            text = Anim.Blend(bg, text, ex);

            if (item.Open != null)
                GlyphShape.Draw(g, "\uE8A7", new PointF(S(60), y + ItemH / 2f), S(14),
                    Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, 0.55f + 0.45f * h), ex), Math.Max(0.6f, S(1) * 0.8f));
            else
                CheckMark.Draw(g, new RectangleF(S(52), y + (ItemH - S(16)) / 2f, S(16), S(16)), Get(check, item), false, h, !item.Disabled, rowBg);
            int x = S(82);
            string name = item.Name();
            int sizeW = S(110);
            RectangleF act = RectangleF.Empty;
            if (item.Act != null && !item.Disabled)
            {
                act = ActRect(item, y, w);
                sizeW = Math.Max(sizeW, (int)(w - act.X) - S(40) + S(8));
            }
            int maxName = w - x - sizeW - S(48);
            TextDraw.DrawText(g, name, Font, new RectangleF(x, y, maxName, ItemH), text, TextLeft);
            x += Math.Min(maxName, MeasureWidth(name, Font)) + S(10);
            if (item.Warn)
            {
                int d = S(7);
                using (var b = new SolidBrush(Anim.Blend(bg, p.Warning, ex)))
                    g.FillEllipse(b, x, y + (ItemH - d) / 2f, d, d);
                x += d + S(8);
            }
            string note = item.Disabled && item.DisabledText != null ? item.DisabledText() : (item.Note != null ? item.Note() : "");
            if (!string.IsNullOrEmpty(note) && x < w - sizeW - S(48))
                TextDraw.DrawText(g, note, Theme.SmallFont, new RectangleF(x, y, w - sizeW - S(48) - x, ItemH),
                    Anim.Blend(bg, p.TextDim, ex * 0.9f), TextLeft);

            if (!act.IsEmpty)
            {
                // The button: accent outline, filled more as the mouse comes over it.
                float ha = Get(hover, Tuple.Create((object)item, "act"));
                using (var path = Theme.RoundRect(act, act.Height / 2))
                {
                    using (var b = new SolidBrush(Anim.Blend(bg, Anim.Blend(rowBg, p.Accent, 0.10f + 0.22f * ha), ex)))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Anim.Blend(bg, Anim.Blend(p.Border, p.Accent, 0.55f + 0.45f * ha), ex), Math.Max(1f, S(1))))
                        g.DrawPath(pen, path);
                }
                TextDraw.DrawText(g, item.Badge ?? "", Font, Rectangle.Round(act), Anim.Blend(bg, Anim.Blend(p.Accent, p.Text, ha * 0.5f), ex),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            else if (!item.Disabled)
            {
                float a = item.Size < 0 ? 0.6f : (sizeIn.ContainsKey(item) ? sizeIn[item] : 1f);
                Color sc = item.Checked ? Anim.Blend(p.TextDim, p.Text, 0.6f) : p.TextDim;
                TextDraw.DrawText(g, item.Badge ?? SizeText(item.Size), Font, new RectangleF(w - sizeW - S(40), y, sizeW, ItemH),
                    Anim.Blend(bg, sc, a * ex), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
    }

    // ---------------------------------------------------------------------------------------------

    enum RingState { Scanning, Ready, Cleaning, Done }

    /// <summary>
    /// The big round "clean" button: the Wisp mark drawn large.
    /// <para>
    /// As in DriveUP, the ring is always one arc (where it starts and how long it is). A state only says
    /// where the arc should be; on a change the arc travels there from wherever it is drawn now, so nothing
    /// ever jumps: the spinning arc slows down and settles into the logo, the logo's gap opens into the
    /// cleaning progress, a full ring unwinds back into the spinner. Dust keeps flying off the arc's head
    /// (frozen in flight while idle), sparkles grow and shrink, the number and the caption cross-fade.
    /// </para>
    /// </summary>
    class CleanRing : SkinControl, IScalable
    {
        public void ApplyScale()
        {
            if (numFont != null) numFont.Dispose();
            if (capFont != null) capFont.Dispose();
            numFont = capFont = null;
            FitFonts(RingSize);
            Invalidate();
        }

        RingState state = RingState.Scanning;
        bool introPending = true;

        // Arc travel: from the arc shown when the state changed to the new state's arc.
        float fromStart = Logo.SolidStart, fromSweep = 0, startOffset, travel = 1;
        Func<float, float> travelEase = Anim.EaseInOut;
        float lastStart = Logo.SolidStart, lastSweep;

        float shownBytes, progress, dust, sparkleReady, sparkleDone, numFade = 1, capFade = 1, phase;
        double lastPhaseAt = -1;
        long targetBytes;
        string caption = "", prevCaption = "", prevNumber = "", lastNumber = "";
        readonly Tweened hover, press;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object frameKey = new object(), numKey = new object(), travelKey = new object(), progKey = new object(),
            dustKey = new object(), readyKey = new object(), doneKey = new object(), numFadeKey = new object(), capKey = new object();
        bool running;
        public bool Clickable;
        Font numFont, capFont;

        float RingSize { get { return (Math.Min(Width, Height) - S(24)) * 0.84f; } }
        float Sec { get { return clock.ElapsedMilliseconds / 1000f; } }

        /// <summary>The ring grows with the window; its number and caption grow with it.</summary>
        void FitFonts(float ring)
        {
            float k = Math.Max(0.85f, Math.Min(1.8f, ring / S(276)));
            float numPt = (float)Math.Round(Theme.BigFont.SizeInPoints * k * 2) / 2;
            float capPt = (float)Math.Round(Theme.UiFont.SizeInPoints * Math.Max(1f, Math.Min(1.35f, k)) * 2) / 2;
            if (numFont == null || Math.Abs(numFont.SizeInPoints - numPt) > 0.01f)
            {
                if (numFont != null) numFont.Dispose();
                numFont = new Font(Theme.BigFont.FontFamily, numPt, Theme.BigFont.Style);
            }
            if (capFont == null || Math.Abs(capFont.SizeInPoints - capPt) > 0.01f)
            {
                if (capFont != null) capFont.Dispose();
                capFont = new Font(Theme.UiFont.FontFamily, capPt, Theme.UiFont.Style);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitFonts(RingSize);
        }

        public CleanRing()
        {
            hover = new Tweened(Invalidate);
            press = new Tweened(Invalidate);
        }

        public RingState State { get { return state; } }

        /// <summary>The line under the number; a new text cross-fades with the old one (out, then in).</summary>
        public string Caption
        {
            get { return caption; }
            set
            {
                value = value ?? "";
                if (value == caption) return;
                // Only the numbers changed ("cleaning… 34%" → "35%"): no new fade (one running goes on), it
                // would flicker on every step.
                if (Regex.Replace(value, @"\d", "") == Regex.Replace(caption, @"\d", ""))
                {
                    caption = value;
                    Invalidate();
                    return;
                }
                prevCaption = capFade > 0.5f ? caption : prevCaption;
                caption = value;
                if (!IsHandleCreated || !Visible) { Anim.Cancel(capKey); capFade = 1; Invalidate(); return; }
                capFade = 0;
                Anim.Run(capKey, 320, t => { capFade = t; Invalidate(); });
            }
        }

        public void SetState(RingState s)
        {
            if (s == state && !introPending) return;
            introPending = false;
            var prev = state;
            state = s;

            // The arc sets off from exactly where it is drawn now.
            fromStart = lastStart;
            fromSweep = lastSweep;
            float targetStart, targetSweep;
            Target(s, Sec, out targetStart, out targetSweep);
            // Keep turning clockwise: the target is taken a whole number of turns ahead of the arc.
            startOffset = 360f * (float)Math.Ceiling((fromStart - targetStart) / 360f - 0.001f);
            int ms;
            if (prev == RingState.Scanning && s != RingState.Scanning)
            {
                ms = 1000;
                travelEase = EaseOutCubic;   // a spinning arc slows down into place
            }
            else
            {
                ms = s == RingState.Scanning ? 650 : (s == RingState.Cleaning ? 550 : 850);
                travelEase = Anim.EaseInOut;
            }
            travel = 0;
            Anim.Run(travelKey, ms, t => { travel = travelEase(t); Invalidate(); }, null, x => x);

            // Dust flies while scanning, cleaning and when ready; a clean ring has none.
            To(dustKey, () => dust, v => dust = v, s == RingState.Done ? 0 : 1, 450, 0);
            // The logo's sparkle pops in once the arc has settled; the "done" sparkles likewise.
            To(readyKey, () => sparkleReady, v => sparkleReady = v, s == RingState.Ready ? 1 : 0, s == RingState.Ready ? 650 : 260, s == RingState.Ready ? ms / 2 : 0);
            To(doneKey, () => sparkleDone, v => sparkleDone = v, s == RingState.Done ? 1 : 0, s == RingState.Done ? 700 : 260, s == RingState.Done ? ms / 3 : 0);
            if (s == RingState.Cleaning && prev != RingState.Cleaning)
            {
                Anim.Cancel(progKey);
                progress = 0;
            }

            bool needsFrames = s == RingState.Scanning || s == RingState.Cleaning;
            if (needsFrames && !running)
            {
                running = true;
                Anim.Frame(frameKey, () =>
                {
                    if (IsDisposed) return running = false;
                    if (Visible) Invalidate();
                    running = state == RingState.Scanning || state == RingState.Cleaning;
                    return running;
                });
            }
            Invalidate();
        }

        /// <summary>Animates a value from where it is to a target (with an optional delay); growing ones overshoot a little.</summary>
        void To(object key, Func<float> get, Action<float> set, float target, int ms, int delay)
        {
            float from = get();
            if (Math.Abs(from - target) < 0.001f) { Anim.Cancel(key); return; }
            var expect = state;
            Action run = () =>
            {
                if (state != expect) return;   // the state moved on during the delay
                float f = get();
                bool grow = target > f;
                Anim.Run(key, ms, t => { set(f + (target - f) * (grow ? Back(t) : t)); Invalidate(); }, null, grow ? (Func<float, float>)(x => x) : null);
            };
            if (delay > 0) Anim.Delay(delay, run);
            else run();
        }

        static float EaseOutCubic(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            float u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>Overshooting ease for the sparkle "pop".</summary>
        static float Back(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            const float c1 = 1.70158f, c3 = c1 + 1;
            return 1 + c3 * (float)Math.Pow(t - 1, 3) + c1 * (float)Math.Pow(t - 1, 2);
        }

        /// <summary>Where the arc of a state is at time <paramref name="sec"/> (start may run past 360).</summary>
        void Target(RingState s, float sec, out float start, out float sweep)
        {
            switch (s)
            {
                case RingState.Scanning:
                {
                    // Loader: the head runs ahead during the first half of a cycle, the tail catches up in the
                    // second; the whole arc also turns slowly. Seamless from cycle to cycle.
                    const float period = 1.5f, reach = 250f, min = 22f;
                    float cyc = sec / period;
                    float k = (float)Math.Floor(cyc), u = cyc - k;
                    float head = reach * Anim.EaseInOut(Math.Min(1, u * 2));
                    float tail = reach * Anim.EaseInOut(Math.Max(0, u * 2 - 1));
                    start = Logo.SolidStart + sec * 140 + k * reach + tail;
                    sweep = min + head - tail;
                    return;
                }
                case RingState.Cleaning:
                    start = Logo.SolidStart;
                    sweep = Math.Max(4, 360 * progress);
                    return;
                case RingState.Done:
                    start = Logo.SolidStart;
                    sweep = 360;
                    return;
                default:
                    start = Logo.SolidStart;
                    sweep = Logo.SolidSweep;
                    return;
            }
        }

        /// <summary>The number in the middle counts to the new value.</summary>
        public void SetBytes(long bytes, bool animate = true)
        {
            targetBytes = bytes;
            float from = shownBytes;
            if (!animate)
            {
                // A new count (cleaning starts from zero): the old number leaves upwards, the new one comes in.
                string before = NumberText();
                Anim.Cancel(numKey);
                shownBytes = bytes;
                if (IsHandleCreated && Visible && before != NumberText() && numFade >= 1)
                {
                    prevNumber = before;
                    numFade = 0;
                    Anim.Run(numFadeKey, 380, t => { numFade = t; Invalidate(); });
                }
                Invalidate();
                return;
            }
            Anim.Run(numKey, 700, t => { shownBytes = from + (bytes - from) * t; Invalidate(); });
        }

        /// <summary>Cleaning progress 0..1; the arc glides to it (it never moves backwards).</summary>
        public void SetProgress(float value)
        {
            float from = progress;
            if (value < from) value = from;
            Anim.Run(progKey, 200, t => { progress = from + (value - from) * t; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Clickable) hover.To(1, 140); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 260); press.To(0, 160); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Clickable && e.Button == MouseButtons.Left) press.To(1, 80); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.To(0, 220); }

        static float Smooth(float a, float b, float t)
        {
            float x = Math.Max(0, Math.Min(1, (t - a) / (b - a)));
            return x * x * (3 - 2 * x);
        }

        string NumberText()
        {
            return state == RingState.Scanning && targetBytes == 0 ? "…" : L.Size((long)Math.Round(shownBytes));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Cursor = Clickable ? Cursors.Hand : Cursors.Default;
            if (!Clickable && hover.Value > 0.001f && hover.Value < 0.999f) hover.To(0, 200);

            float scale = 1 + hover.Value * 0.02f - press.Value * 0.03f;
            float ring = RingSize * scale;
            if (ring < 4) return;
            float stroke = Math.Max(S(8), ring * 0.055f);
            var c = new PointF(Width / 2f, Height / 2f);
            float radius = ring / 2 - stroke / 2;
            float sec = Sec;
            Color violet = Logo.Violet;

            // Dust drifts while the ring works and stays where it is while idle (no jump either way).
            if (lastPhaseAt >= 0 && (state == RingState.Scanning || state == RingState.Cleaning))
                phase = (phase + (float)(sec - lastPhaseAt) * (state == RingState.Scanning ? 1.6f : 1.4f)) % 1f;
            lastPhaseAt = sec;

            // soft glow behind the ring on hover
            Color inner = Anim.Blend(bg, violet, 0.06f * hover.Value);
            if (hover.Value > 0.01f)
                using (var b = new SolidBrush(inner))
                    g.FillEllipse(b, c.X - ring / 2 - S(6), c.Y - ring / 2 - S(6), ring + S(12), ring + S(12));

            // track
            using (var pen = new Pen(Anim.Blend(bg, p.IsDark ? p.SurfaceAlt : p.Pressed, 0.9f), stroke))
                g.DrawEllipse(pen, c.X - radius, c.Y - radius, radius * 2, radius * 2);

            // ----- the arc -----
            float ts, tw;
            Target(state, sec, out ts, out tw);
            float start = Anim.Lerp(fromStart, ts + startOffset, travel);
            float sweep = Math.Max(0, Anim.Lerp(fromSweep, tw, travel));
            if (travel >= 1)
            {
                // Settled: forget the extra turns so the numbers stay small.
                startOffset = 0;
                start = ts;
            }
            lastStart = start % 360f;
            if (lastStart < 0) lastStart += 360;
            lastSweep = sweep;

            // The mark is drawn from 12 o'clock: turned to where the arc starts now.
            float dustNow = dust * (state == RingState.Cleaning ? Math.Min(1, (1 - progress) * 8) : 1) * Math.Min(1, sweep / 40f);
            var st = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(lastStart - Logo.SolidStart);
            g.TranslateTransform(-c.X, -c.Y);
            if (sweep >= 359.6f)
            {
                using (var pen = new Pen(violet, stroke)) g.DrawEllipse(pen, c.X - radius, c.Y - radius, radius * 2, radius * 2);
                if (dustNow > 0.01f) Logo.DrawMark(g, c, ring, stroke, violet, 360, 3, phase, true, dustNow, 0);
            }
            else Logo.DrawMark(g, c, ring, stroke, violet, sweep, 3, phase, true, dustNow, 0);
            g.Restore(st);

            // The logo's sparkle sits in the gap at half past ten; the "done" pair outside the full ring.
            if (sparkleReady > 0.01f) Logo.DrawMark(g, c, ring, stroke, violet, 0, 0, 0, false, 0, sparkleReady);
            if (sparkleDone > 0.01f)
            {
                double a1 = 225 * Math.PI / 180, a2 = 318 * Math.PI / 180;
                float r1 = radius + stroke * 2.2f, r2 = radius + stroke * 1.6f;
                Logo.DrawSparkle(g, new PointF(c.X + r1 * (float)Math.Cos(a1), c.Y + r1 * (float)Math.Sin(a1)), stroke * 1.6f * sparkleDone, violet);
                Logo.DrawSparkle(g, new PointF(c.X + r2 * (float)Math.Cos(a2), c.Y + r2 * (float)Math.Sin(a2)), stroke * 0.95f * Math.Max(0, sparkleDone - 0.15f), violet);
            }

            // ----- the middle: number and caption, each cross-fading out, then in -----
            string number = NumberText();
            bool dots = number == "…";
            // "…" turning into a size (or back): the old one leaves upwards, once per change.
            if (lastNumber.Length > 0 && (lastNumber == "…") != dots && numFade >= 1)
            {
                prevNumber = lastNumber;
                numFade = 0;
                Anim.Run(numFadeKey, 380, t => { numFade = t; Invalidate(); });
            }
            lastNumber = number;

            if (numFont == null) FitFonts(RingSize);
            int bh = numFont.Height, ch = capFont.Height;
            // Number and caption are centred as one block inside the ring.
            int top = (int)Math.Round(c.Y - (bh + S(2) + ch) / 2f);
            float shift = S(8);
            if (numFade < 1 && prevNumber.Length > 0)
            {
                float outA = 1 - Smooth(0, 0.55f, numFade), inA = Smooth(0.4f, 1, numFade);
                TextDraw.DrawText(g, prevNumber, numFont, new RectangleF(0, top - shift * numFade, Width, bh), Anim.Blend(inner, p.Text, outA), TextCenter);
                TextDraw.DrawText(g, number, numFont, new RectangleF(0, top + shift * (1 - numFade), Width, bh), Anim.Blend(inner, p.Text, inA), TextCenter);
            }
            else TextDraw.DrawText(g, number, numFont, new Rectangle(0, top, Width, bh), p.Text, TextCenter);

            int innerW = (int)(radius * 2 - stroke * 2);
            var capRect = new Rectangle((Width - innerW) / 2, top + bh + S(2), innerW, ch);
            if (capFade < 1 && prevCaption.Length > 0)
                TextDraw.DrawText(g, prevCaption, capFont, capRect, Anim.Blend(inner, p.TextDim, 1 - Smooth(0, 0.5f, capFade)), TextCenter | TextFormatFlags.EndEllipsis);
            TextDraw.DrawText(g, caption, capFont, capRect, Anim.Blend(inner, p.TextDim, Smooth(0.5f, 1, capFade)), TextCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (numFont != null) numFont.Dispose();
                if (capFont != null) capFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
