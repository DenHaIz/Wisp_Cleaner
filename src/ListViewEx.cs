using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Wisp
{
    enum RowKind { Normal, Dim, Danger, Header }

    /// <summary>Rows whose columns sort by a value rather than by their text (sizes, dates).</summary>
    interface ISortable
    {
        /// <summary>A comparable key for the column, or null to sort by the text.</summary>
        IComparable SortKey(int column);
    }

    /// <summary>Rows of a list with check boxes (CheckBoxesEx) implement this on their Tag.</summary>
    interface ICheckRow
    {
        bool Checked { get; set; }
        bool Checkable { get; }
    }

    /// <summary>What the list needs to draw a row: text color, icon and status dot.</summary>
    interface IRowInfo
    {
        RowKind RowKind { get; }
        string IconPath { get; }
        /// <summary>0 = none, 1 = on (green), 2 = off (gray), 3 = problem (red).</summary>
        int Dot { get; }
    }

    /// <summary>
    /// Owner-drawn details ListView: themed header and rows, status dots, file icons,
    /// animated hover/selection, row flash after an action and fade-out on removal.
    /// </summary>
    class ThemedListView : ListView, IScalable
    {
        /// <summary>Row height and columns follow the zoom (the font is switched by the window).</summary>
        public void ApplyScale()
        {
            var old = SmallImageList;
            SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(30)) };
            if (old != null) old.Dispose();
            FitLastColumn();
            if (skeleton != null && skeleton.Visible) PlaceSkeleton();
            Invalidate();
        }

        public Dictionary<string, Bitmap> Icons;
        public int DotColumn = -1;
        public string EmptyText = "";
        /// <summary>Optional large glyph drawn above EmptyText.</summary>
        public string EmptyGlyph = "";
        public int SortColumn;
        public bool SortDesc;
        public bool[] Numeric = new bool[0];
        public bool QuietSelection;
        /// <summary>Own animated check boxes in the first column; a click on a row toggles it.</summary>
        public bool CheckBoxesEx;
        public bool IconColumn = true;
        public bool SortEnabled = true;
        /// <summary>Glyph drawn when a row has no icon.</summary>
        public string DefaultGlyph = "\uE7C3";
        /// <summary>When set, columns share the width in these proportions instead of only stretching the last one.</summary>
        public float[] ColumnWeights;
        /// <summary>Optional minimum widths (unscaled px) for weighted columns.</summary>
        public int[] ColumnMins;
        public event EventHandler CheckChanged;
        SkeletonRows skeleton;
        HeaderFiller headerFiller;
        bool loading;
        readonly Dictionary<ListViewItem, float> chk = new Dictionary<ListViewItem, float>();

        readonly Dictionary<ListViewItem, float> hover = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, float> sel = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, float> flash = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, Color> flashColor = new Dictionary<ListViewItem, Color>();
        readonly Dictionary<ListViewItem, float> fade = new Dictionary<ListViewItem, float>();
        ListViewItem hoverItem;
        bool fitting;

        const TextFormatFlags CellFlags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

        public ThemedListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = true;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            DoubleBuffered = true;
            Font = Theme.UiFont;
            // Row height comes from the small image list; icons are drawn by hand.
            SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(30)) };
            ListViewItemSorter = new Sorter(this);
        }

        static int S(int v) { return Theme.S(v); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            IntPtr header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) headerFiller = new HeaderFiller(header);
            ApplyTheme();
            FitLastColumn();
            UpdateSkeleton();
        }

        /// <summary>While true and empty, shimmering placeholder rows stand in for the items.</summary>
        public bool Loading
        {
            get { return loading; }
            set
            {
                // Always re-checked: items may have arrived since the last call.
                loading = value;
                UpdateSkeleton();
            }
        }

        void UpdateSkeleton()
        {
            bool show = loading && Items.Count == 0 && IsHandleCreated;
            if (show && skeleton == null)
            {
                skeleton = new SkeletonRows(this);
                Controls.Add(skeleton);
            }
            if (skeleton == null) return;
            if (show)
            {
                PlaceSkeleton();
                skeleton.Visible = true;
                Shimmer.Run(skeleton, skeleton, () => skeleton.Visible && loading && Items.Count == 0);
            }
            else skeleton.Visible = false;
        }

        void PlaceSkeleton()
        {
            int top = HeaderBottom();
            skeleton.SetBounds(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top));
        }

        /// <summary>Placeholder rows that follow the list's columns.</summary>
        class SkeletonRows : Control
        {
            readonly ThemedListView lv;

            public SkeletonRows(ThemedListView lv)
            {
                this.lv = lv;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.Selectable, false);
                Visible = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                var g = e.Graphics;
                g.Clear(p.Surface);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int rowH = lv.SmallImageList != null ? lv.SmallImageList.ImageSize.Height + S(1) : S(31);
                using (var b = Shimmer.Make(Width, p.Surface))
                {
                    for (int row = 0, y = 0; y < Height; row++, y += rowH)
                    {
                        float cy = y + rowH / 2f;
                        int x0 = 0;
                        for (int c = 0; c < lv.Columns.Count; c++)
                        {
                            int cw = lv.Columns[c].Width;
                            int x = x0 + S(12);
                            if (c == 0)
                            {
                                if (lv.CheckBoxesEx)
                                {
                                    Shimmer.Tile(g, b, new RectangleF(x, cy - S(8), S(16), S(16)), S(4));
                                    x += S(28);
                                }
                                if (lv.IconColumn)
                                {
                                    Shimmer.Tile(g, b, new RectangleF(x, cy - S(8), S(16), S(16)), S(4));
                                    x += S(26);
                                }
                            }
                            float room = x0 + cw - S(14) - x;
                            float len = room * (c == 0 ? 0.45f + 0.4f * Shimmer.Vary(row, c) : 0.3f + 0.45f * Shimmer.Vary(row, c));
                            if (len > S(8)) Shimmer.Bar(g, b, new RectangleF(x, cy - S(5), len, S(10)));
                            x0 += cw;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Paints the header strip to the right of the last column, which the system would leave
        /// in its own (light) colour, also in snapshots taken for transitions.
        /// </summary>
        class HeaderFiller : NativeWindow
        {
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref RECT r);
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);

            public HeaderFiller(IntPtr header) { AssignHandle(header); }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0014 /* WM_ERASEBKGND */)
                {
                    using (var g = Graphics.FromHdc(m.WParam)) Fill(g, 0);
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
                if (m.Msg == 0x000F /* WM_PAINT */)
                {
                    using (var g = Graphics.FromHwnd(Handle)) Fill(g, LastRight());
                }
                else if (m.Msg == 0x0318 /* WM_PRINTCLIENT */)
                {
                    using (var g = Graphics.FromHdc(m.WParam)) Fill(g, LastRight());
                }
            }

            int LastRight()
            {
                int n = SendMessage(Handle, 0x1200 /* HDM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (n <= 0) return 0;
                var r = new RECT();
                SendMessage(Handle, 0x1207 /* HDM_GETITEMRECT */, (IntPtr)(n - 1), ref r);
                return r.Right;
            }

            void Fill(Graphics g, int from)
            {
                RECT rc;
                if (!GetClientRect(Handle, out rc) || rc.Right <= from) return;
                var p = Theme.Current;
                using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, from, 0, rc.Right - from, rc.Bottom);
                using (var pen = new Pen(p.Border)) g.DrawLine(pen, from, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
            }
        }

        public void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Surface;
            ForeColor = p.Text;
            if (!IsHandleCreated) return;
            // Dark scrollbars on Windows 10 1809+; the regular Explorer look otherwise.
            Native.SetWindowTheme(Handle, p.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            IntPtr header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) InvalidateRect(header, IntPtr.Zero, true);
            Invalidate();
        }

        /// <summary>Stretches the last column so the header has no unpainted gap on the right.</summary>
        public void FitLastColumn()
        {
            if (fitting || Columns.Count == 0 || !IsHandleCreated) return;
            fitting = true;
            try
            {
                if (ColumnWeights != null && ColumnWeights.Length == Columns.Count)
                {
                    int n = Columns.Count, avail = ClientSize.Width - 1;
                    var mins = new int[n];
                    var ws = new int[n];
                    int total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        mins[i] = S(ColumnMins != null && ColumnMins.Length == n ? ColumnMins[i] : 50);
                        ws[i] = Math.Max(mins[i], (int)(avail * ColumnWeights[i]));
                        total += ws[i];
                    }
                    // Minimums pushed the sum past the width: take it back from the columns that have room.
                    int over = total - avail, slack = 0;
                    for (int i = 0; i < n; i++) slack += ws[i] - mins[i];
                    if (over > 0 && slack > 0)
                        for (int i = 0; i < n; i++)
                            ws[i] -= (int)Math.Ceiling(over * (ws[i] - mins[i]) / (double)slack);
                    // The last column takes the rounding remainder, so the header ends at the edge.
                    int used = 0;
                    for (int i = 0; i < n - 1; i++) used += ws[i];
                    ws[n - 1] = Math.Max(mins[n - 1], avail - used);
                    for (int i = 0; i < n; i++)
                        if (Columns[i].Width != ws[i]) Columns[i].Width = ws[i];
                    return;
                }
                int sum = 0;
                for (int i = 0; i < Columns.Count - 1; i++) sum += Columns[i].Width;
                int w = Math.Max(S(140), ClientSize.Width - sum - 1);
                var last = Columns[Columns.Count - 1];
                if (last.Width != w) last.Width = w;
            }
            finally { fitting = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitLastColumn();
            if (skeleton != null && skeleton.Visible) PlaceSkeleton();
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); FitLastColumn(); }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            if (e.ColumnIndex != Columns.Count - 1) FitLastColumn();
        }

        protected override void OnColumnClick(ColumnClickEventArgs e)
        {
            base.OnColumnClick(e);
            if (!SortEnabled) return;
            Transition.CrossFade(this, () =>
            {
                if (e.Column == SortColumn) SortDesc = !SortDesc;
                else { SortColumn = e.Column; SortDesc = Numeric.Length > e.Column && Numeric[e.Column]; }
                Sort();
            }, 180);
        }

        // ---------- animated states ----------

        void InvalidateRow(ListViewItem it)
        {
            if (it.ListView != this) return;
            try
            {
                var b = it.Bounds;
                Invalidate(new Rectangle(0, b.Y, ClientSize.Width, b.Height));
            }
            catch { }
        }

        void Animate(Dictionary<ListViewItem, float> map, ListViewItem it, string tag, float target, int ms)
        {
            float from;
            if (!map.TryGetValue(it, out from)) from = tag == "s" ? (it.Selected ? 0 : 1) : 0;
            Anim.Run(Tuple.Create(this, it, tag), ms, t =>
            {
                float v = from + (target - from) * t;
                if (t >= 1) map.Remove(it); else map[it] = v;
                InvalidateRow(it);
            });
        }

        /// <summary>Sets a row's check state with the tick animation.</summary>
        public void SetChecked(ListViewItem it, bool value)
        {
            var cr = it.Tag as ICheckRow;
            if (cr == null || !cr.Checkable || cr.Checked == value) return;
            cr.Checked = value;
            float from;
            if (!chk.TryGetValue(it, out from)) from = value ? 0 : 1;
            float to = value ? 1 : 0;
            Anim.Run(Tuple.Create(this, it, "c"), value ? 260 : 160, t =>
            {
                if (t >= 1) chk.Remove(it); else chk[it] = from + (to - from) * t;
                InvalidateRow(it);
            });
        }

        public void SetAllChecked(bool value)
        {
            foreach (ListViewItem it in Items) SetChecked(it, value);
            if (CheckChanged != null) CheckChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!CheckBoxesEx || e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Shift | Keys.Control)) != 0) return;
            var it = GetItemAt(e.X, e.Y);
            var cr = it != null ? it.Tag as ICheckRow : null;
            if (cr == null || !cr.Checkable) return;
            SetChecked(it, !cr.Checked);
            if (CheckChanged != null) CheckChanged(this, EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!CheckBoxesEx) return;
            if (e.KeyCode == Keys.Space)
            {
                var rows = new List<ListViewItem>();
                foreach (ListViewItem it in SelectedItems) rows.Add(it);
                bool allOn = rows.TrueForAll(r => { var c = r.Tag as ICheckRow; return c == null || !c.Checkable || c.Checked; });
                foreach (var it in rows) SetChecked(it, !allOn);
                if (CheckChanged != null) CheckChanged(this, EventArgs.Empty);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.A && e.Control)
            {
                bool allOn = true;
                foreach (ListViewItem it in Items) { var c = it.Tag as ICheckRow; if (c != null && c.Checkable && !c.Checked) allOn = false; }
                SetAllChecked(!allOn);
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(GetItemAt(e.X, e.Y));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
        }

        void SetHover(ListViewItem it)
        {
            if (it == hoverItem) return;
            var old = hoverItem;
            hoverItem = it;
            if (old != null) AnimateHover(old, 0, 240);
            if (it != null) AnimateHover(it, 1, 110);
        }

        void AnimateHover(ListViewItem it, float target, int ms)
        {
            float from;
            hover.TryGetValue(it, out from);
            Anim.Run(Tuple.Create(this, it, "h"), ms, t =>
            {
                float v = from + (target - from) * t;
                if (v <= 0.001f) hover.Remove(it); else hover[it] = v;
                InvalidateRow(it);
            });
        }

        protected override void OnItemSelectionChanged(ListViewItemSelectionChangedEventArgs e)
        {
            base.OnItemSelectionChanged(e);
            // Range selections (Shift+click) change many rows at once: animating each would only cost frames.
            if (QuietSelection || sel.Count > 24) return;
            Animate(sel, e.Item, "s", e.IsSelected ? 1 : 0, e.IsSelected ? 140 : 220);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        /// <summary>Briefly tints rows to confirm an action on them.</summary>
        public void Flash(IEnumerable<ListViewItem> items, Color color)
        {
            foreach (var row in items)
            {
                var it = row;
                flashColor[it] = color;
                Anim.Run(Tuple.Create(this, it, "f"), 1100, t =>
                {
                    if (t >= 1) { flash.Remove(it); flashColor.Remove(it); }
                    else flash[it] = 1 - t;
                    InvalidateRow(it);
                }, null, t => t * t);
            }
        }

        /// <summary>Fades rows out, then removes them with a short cross-fade so the rest settles smoothly.</summary>
        public void FadeOutAndRemove(List<ListViewItem> items, Action done)
        {
            if (items.Count == 0) { if (done != null) done(); return; }
            Anim.Run(new object(), 300, t =>
            {
                foreach (var it in items) { fade[it] = t; InvalidateRow(it); }
            }, () =>
            {
                Transition.CrossFade(this, () =>
                {
                    BeginUpdate();
                    foreach (var it in items)
                    {
                        fade.Remove(it);
                        hover.Remove(it);
                        sel.Remove(it);
                        if (it == hoverItem) hoverItem = null;
                        if (it.ListView == this) Items.Remove(it);
                    }
                    EndUpdate();
                    FitLastColumn();
                }, 200);
                if (done != null) done();
            });
        }

        /// <summary>Clears per-row animation state before the list is repopulated.</summary>
        public void ResetRowStates()
        {
            hover.Clear();
            sel.Clear();
            flash.Clear();
            flashColor.Clear();
            fade.Clear();
            chk.Clear();
            hoverItem = null;
        }

        // ---------- drawing ----------

        Color RowBack(ListViewItem it)
        {
            var p = Theme.Current;
            Color bg = p.Surface;
            float h;
            if (hover.TryGetValue(it, out h)) bg = Anim.Blend(bg, p.Hover, h * 0.85f);
            float s;
            if (!sel.TryGetValue(it, out s)) s = it.Selected ? 1 : 0;
            if (s > 0) bg = Anim.Blend(bg, Focused ? p.Selected : p.SelectedInactive, s);
            float f;
            if (flash.TryGetValue(it, out f)) bg = Anim.Blend(bg, flashColor[it], f * 0.35f);
            float d;
            if (fade.TryGetValue(it, out d)) bg = Anim.Blend(bg, p.Surface, d);
            return bg;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var r = e.Bounds;
            using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, r);
            bool sorted = SortEnabled && e.ColumnIndex == SortColumn;
            TextRenderer.DrawText(g, e.Header.Text, Theme.UiBold,
                new Rectangle(r.X + S(10), r.Y, r.Width - S(34), r.Height), sorted ? p.Text : p.TextDim, p.Surface, CellFlags);
            if (sorted && Theme.HasGlyphs)
                TextRenderer.DrawText(g, SortDesc ? "\uE70D" : "\uE70E", Theme.GlyphSmall,
                    new Rectangle(r.Right - S(24), r.Y, S(16), r.Height), p.Accent, p.Surface,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var pen = new Pen(p.Border))
            {
                g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
                if (e.ColumnIndex < Columns.Count - 1)
                    g.DrawLine(pen, r.Right - 1, r.Top + S(7), r.Right - 1, r.Bottom - S(7));
            }
        }

        /// <summary>
        /// The whole row is drawn here in one pass: shapes and icons with GDI+, then every cell's
        /// text through one device context. Drawing per cell (OnDrawSubItem + TextRenderer) cost
        /// about 2 ms a row, which made page transitions and scrolling hitch.
        /// </summary>
        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            DrawRow(e.Graphics, IntPtr.Zero, e.Item, e.Bounds);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR
        {
            public IntPtr hwndFrom, idFrom;
            public int code;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW
        {
            public NMHDR hdr;   // a nested struct keeps the native padding after 'code' on 64-bit
            public int dwDrawStage;
            public IntPtr hdc;
            public RECT rc;
            public IntPtr dwItemSpec;
            public int uItemState;
            public IntPtr lItemlParam;
        }

        /// <summary>
        /// Custom draw handled here rather than by WinForms: WinForms asks for every cell separately
        /// and wraps each call in new objects, which cost as much as the drawing itself. Here each row
        /// is drawn once and the list is told to skip its own drawing and the per-cell callbacks.
        /// </summary>
        bool CustomDraw(ref Message m)
        {
            var nm = (NMCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMCUSTOMDRAW));
            if (nm.hdr.code != -12 /* NM_CUSTOMDRAW */ || nm.hdr.hwndFrom != Handle) return false;
            if (nm.dwDrawStage == 0x1 /* CDDS_PREPAINT */)
            {
                m.Result = (IntPtr)0x20; // CDRF_NOTIFYITEMDRAW
                return true;
            }
            if (nm.dwDrawStage != 0x10001 /* CDDS_ITEMPREPAINT */) return false;
            int index = nm.dwItemSpec.ToInt32();
            if (index < 0 || index >= Items.Count) return false;
            var it = Items[index];
            var b = it.Bounds;
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, b.Height);
            if (rowBuf == null || rowBuf.Width != w || rowBuf.Height != h)
            {
                if (rowBuf != null) rowBuf.Dispose();
                rowBuf = new RowBuffer(w, h);
            }
            DrawRow(rowBuf.G, rowBuf.Dc, it, new Rectangle(b.X, 0, b.Width, h));
            GdiBitmap.BitBlt(nm.hdc, 0, b.Y, w, h, rowBuf.Dc, 0, 0, GdiBitmap.SRCCOPY);
            m.Result = (IntPtr)0x4; // CDRF_SKIPDEFAULT
            return true;
        }

        RowBuffer rowBuf;

        protected override void Dispose(bool disposing)
        {
            if (disposing && rowBuf != null)
            {
                rowBuf.Dispose();
                rowBuf = null;
            }
            base.Dispose(disposing);
        }

        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        /// <summary>
        /// One row's pixels in memory, reachable both by GDI+ (shapes, icons) and by GDI (text):
        /// GDI+ drawing straight onto a window DC has to read the screen back for every shape.
        /// </summary>
        sealed class RowBuffer : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            struct BITMAPINFOHEADER
            {
                public int biSize, biWidth, biHeight;
                public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            }

            [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

            public readonly int Width, Height;
            public readonly IntPtr Dc;
            public readonly Graphics G;
            readonly IntPtr dib, old;
            readonly Bitmap bmp;

            public RowBuffer(int w, int h)
            {
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                dib = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) throw new OutOfMemoryException();
                Dc = CreateCompatibleDC(IntPtr.Zero);
                old = SelectObject(Dc, dib);
                bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, bits);
                G = Graphics.FromImage(bmp);
                Width = w;
                Height = h;
            }

            public void Dispose()
            {
                G.Dispose();
                bmp.Dispose();
                SelectObject(Dc, old);
                DeleteDC(Dc);
                DeleteObject(dib);
            }
        }

        /// <summary>Draws a row; text goes to textDc when the caller has one for the same pixels.</summary>
        void DrawRow(Graphics g, IntPtr textDc, ListViewItem it, Rectangle bounds)
        {
            var p = Theme.Current;
            var info = it.Tag as IRowInfo;
            Color bg = RowBack(it);
            var r = new Rectangle(0, bounds.Y, Math.Max(ClientSize.Width, bounds.Right), bounds.Height);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);

            float s;
            if (!sel.TryGetValue(it, out s)) s = it.Selected ? 1 : 0;
            if (s > 0.01f)
            {
                // Accent bar on the left grows with the selection animation.
                int barH = (int)((r.Height - S(12)) * s);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, Anim.Blend(bg, p.Accent, s), new RectangleF(S(2), r.Y + (r.Height - barH) / 2f, S(3), barH), S(1));
                g.SmoothingMode = SmoothingMode.None;
            }

            float fd;
            fade.TryGetValue(it, out fd);
            int n = Math.Min(Columns.Count, it.SubItems.Count);
            var textX = new int[n];
            int left = bounds.Left;
            for (int c = 0; c < n; c++)
            {
                textX[c] = DrawCellParts(g, it, info, c, left, r.Y, r.Height, bg, fd);
                left += Columns[c].Width;
            }

            if (textDc != IntPtr.Zero) g.Flush(FlushIntention.Sync);
            IntPtr hdc = textDc != IntPtr.Zero ? textDc : g.GetHdc();
            try
            {
                foreach (var op in glyphOps) GdiText.Draw(hdc, op.Glyph, Theme.Glyph, op.Bounds, op.Color, GdiText.Centered);
                left = bounds.Left;
                for (int c = 0; c < n; c++)
                {
                    int right = left + Columns[c].Width;
                    Color fg = c == 0 ? p.Text : Anim.Blend(p.Text, p.TextDim, 0.35f);
                    if (info != null && info.RowKind == RowKind.Dim) fg = p.TextDim;
                    else if (info != null && info.RowKind == RowKind.Header) fg = c == 0 ? p.Text : p.TextDim;
                    else if (info != null && info.RowKind == RowKind.Danger) fg = p.Danger;
                    if (fd > 0) fg = Anim.Blend(fg, bg, fd);
                    var font = info != null && info.RowKind == RowKind.Header && c == 0 ? Theme.UiBold : Font;
                    GdiText.Draw(hdc, it.SubItems[c].Text, font, new Rectangle(textX[c], r.Y, Math.Max(0, right - textX[c] - S(8)), r.Height), fg);
                    left = right;
                }
            }
            finally
            {
                glyphOps.Clear();
                if (textDc == IntPtr.Zero) g.ReleaseHdc(hdc);
                else GdiFlush();
            }
        }

        /// <summary>Check box, icon and status dot of one cell; returns where its text starts.</summary>
        int DrawCellParts(Graphics g, ListViewItem it, IRowInfo info, int column, int cellX, int y, int h, Color bg, float fd)
        {
            var p = Theme.Current;
            int x = cellX + S(12);
            if (column == 0)
            {
                var cr = it.Tag as ICheckRow;
                if (CheckBoxesEx && cr != null && cr.Checkable)
                {
                    float hv;
                    hover.TryGetValue(it, out hv);
                    float cv;
                    if (!chk.TryGetValue(it, out cv)) cv = cr.Checked ? 1 : 0;
                    CheckMark.Draw(g, new RectangleF(x, y + (h - S(16)) / 2f, S(16), S(16)), cv, false, hv, true, bg);
                    x += S(16) + S(12);
                }
                else if (CheckBoxesEx) x += S(16) + S(12);
                if (IconColumn && (info == null || info.RowKind != RowKind.Header))
                {
                    DrawIcon(g, info, new Rectangle(x, y + (h - S(16)) / 2, S(16), S(16)), bg, fd);
                    x += S(16) + S(10);
                }
            }
            if (column == DotColumn && info != null && info.Dot > 0)
            {
                Color dc = info.Dot == 1 ? p.Success : (info.Dot == 2 ? p.TextDim : p.Danger);
                int d = S(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Anim.Blend(dc, bg, fd)))
                    g.FillEllipse(b, x, y + (h - d) / 2f, d, d);
                g.SmoothingMode = SmoothingMode.None;
                x += d + S(8);
            }
            return x;
        }

        /// <summary>Cells are drawn whole by OnDrawItem.</summary>
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { }

        /// <summary>Plain GDI text output: one selected font and colour per call, no per-call wrappers.</summary>
        static class GdiText
        {
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
            [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string text, int len, ref RECT r, uint flags);

            static readonly Dictionary<Font, IntPtr> fonts = new Dictionary<Font, IntPtr>();
            // DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS
            public const uint CellFlags = 0x0 | 0x4 | 0x20 | 0x800 | 0x8000;
            // DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX
            public const uint Centered = 0x1 | 0x4 | 0x20 | 0x800;

            public static void Draw(IntPtr hdc, string text, Font font, Rectangle r, Color c, uint flags = CellFlags)
            {
                if (string.IsNullOrEmpty(text) || r.Width <= 0) return;
                IntPtr hf;
                if (!fonts.TryGetValue(font, out hf)) fonts[font] = hf = font.ToHfont();
                IntPtr old = SelectObject(hdc, hf);
                SetTextColor(hdc, ColorTranslator.ToWin32(c));
                SetBkMode(hdc, 1 /* TRANSPARENT */);
                var rc = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
                DrawTextW(hdc, text, text.Length, ref rc, flags);
                SelectObject(hdc, old);
            }
        }

        readonly Dictionary<Bitmap, Bitmap> scaledIcons = new Dictionary<Bitmap, Bitmap>();

        /// <summary>
        /// Icons are scaled once, in high quality, to the size they are drawn at (premultiplied, the
        /// fastest format to draw): rescaling every icon on every paint made long lists slow to paint.
        /// </summary>
        Bitmap Scaled(Bitmap src, int size)
        {
            Bitmap b;
            if (scaledIcons.TryGetValue(src, out b) && b.Width == size) return b;
            b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            scaledIcons[src] = b;
            return b;
        }

        struct GlyphOp
        {
            public string Glyph;
            public Rectangle Bounds;
            public Color Color;
        }

        /// <summary>Glyph icons of the row being drawn; they go out with its text, through GDI.</summary>
        readonly List<GlyphOp> glyphOps = new List<GlyphOp>();

        void DrawIcon(Graphics g, IRowInfo info, Rectangle r, Color bg, float fadeOut)
        {
            var p = Theme.Current;
            Bitmap bmp = null;
            string path = info != null ? info.IconPath : null;
            if (!string.IsNullOrEmpty(path) && Icons != null) Icons.TryGetValue(path, out bmp);
            if (bmp != null)
            {
                bmp = Scaled(bmp, r.Width);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                if (fadeOut <= 0)
                {
                    g.DrawImage(bmp, r);
                }
                else
                {
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = 1 - fadeOut });
                        g.DrawImage(bmp, r, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                return;
            }
            if (!Theme.HasGlyphs) return;
            bool missing = info != null && info.RowKind == RowKind.Danger;
            Color c = Anim.Blend(missing ? p.Danger : p.TextDim, bg, fadeOut);
            glyphOps.Add(new GlyphOp { Glyph = missing ? "\uE7BA" : DefaultGlyph, Bounds = r, Color = c });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x204E /* WM_REFLECT | WM_NOTIFY */ && CustomDraw(ref m)) return;
            double t0 = Anim.FrameLog != null && (m.Msg == 0x000F || m.Msg == 0x0318) ? Anim.Now : 0;
            base.WndProc(ref m);
            if (t0 > 0)
            {
                Anim.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "P\t{0:F1}\t{1}\t{2} rows\t{3:F1} ms",
                    t0, m.Msg == 0x000F ? "paint" : "print", Items.Count, Anim.Now - t0));
            }
            // Below the last row the light theme draws column lines: cover them, and draw the empty
            // hint. WM_PRINTCLIENT too, so transition snapshots look the same as the screen.
            if ((m.Msg == 0x000F /* WM_PAINT */ || m.Msg == 0x0318 /* WM_PRINTCLIENT */) && (skeleton == null || !skeleton.Visible))
            {
                try
                {
                    int top = HeaderBottom();
                    if (Items.Count > 0) top = Math.Max(top, Items[Items.Count - 1].Bounds.Bottom);
                    if (top < ClientSize.Height)
                        using (var g = m.Msg == 0x000F ? CreateGraphics() : Graphics.FromHdc(m.WParam))
                        {
                            using (var b = new SolidBrush(Theme.Current.Surface))
                                g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
                            if (Items.Count == 0 && !string.IsNullOrEmpty(EmptyText)) DrawEmptyText(g);
                        }
                }
                catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        int HeaderBottom()
        {
            IntPtr header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
            RECT r;
            if (header == IntPtr.Zero || !GetWindowRect(header, out r)) return 0;
            return r.Bottom - r.Top;
        }

        void DrawEmptyText(Graphics g)
        {
            var p = Theme.Current;
            // Upper third of the list: close enough to the header to read as "this list", not lost in the middle.
            int y = Math.Max(HeaderBottom() + S(28), (ClientSize.Height - S(120)) / 3);
            if (EmptyGlyph.Length > 0 && Theme.HasGlyphs)
            {
                int gh = S(44);
                float ink = S(36);
                GlyphShape.Draw(g, EmptyGlyph, new PointF(ClientSize.Width / 2f, y + gh / 2f), ink,
                    Anim.Blend(p.Surface, p.TextDim, 0.6f), ink * 0.035f);
                y += gh + S(12);
            }
            TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(S(24), y, ClientSize.Width - S(48), S(60)),
                p.TextDim, p.Surface,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        }

        class Sorter : IComparer
        {
            readonly ThemedListView lv;
            public Sorter(ThemedListView lv) { this.lv = lv; }

            public int Compare(object a, object b)
            {
                var x = (ListViewItem)a;
                var y = (ListViewItem)b;
                int c = lv.SortColumn;
                var kx = x.Tag as ISortable;
                var ky = y.Tag as ISortable;
                if (kx != null && ky != null)
                {
                    IComparable a1 = kx.SortKey(c), b1 = ky.SortKey(c);
                    if (a1 != null && b1 != null)
                    {
                        int rk = a1.CompareTo(b1);
                        return lv.SortDesc ? -rk : rk;
                    }
                }
                string sx = c < x.SubItems.Count ? x.SubItems[c].Text : "";
                string sy = c < y.SubItems.Count ? y.SubItems[c].Text : "";
                int r;
                if (c < lv.Numeric.Length && lv.Numeric[c])
                {
                    double dx, dy;
                    double.TryParse(sx, NumberStyles.Any, CultureInfo.CurrentCulture, out dx);
                    double.TryParse(sy, NumberStyles.Any, CultureInfo.CurrentCulture, out dy);
                    r = dx.CompareTo(dy);
                }
                else
                {
                    r = string.Compare(sx, sy, StringComparison.CurrentCultureIgnoreCase);
                }
                return lv.SortDesc ? -r : r;
            }
        }
    }
}
