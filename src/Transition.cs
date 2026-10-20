using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>
    /// Flicker-free content changes. The "before" picture is put on an overlay first, the change
    /// happens underneath it, then the overlay blends to the "after" picture (optionally sliding).
    /// Pictures are GDI bitmaps and blending uses AlphaBlend, so a frame costs a couple of blits.
    /// A new transition on the same target continues from the frame currently on screen.
    /// </summary>
    static class Transition
    {
        static readonly List<Overlay> active = new List<Overlay>();

        /// <summary>Ends every running transition at its final picture (before a layout-wide change).</summary>
        public static void FinishAll()
        {
            foreach (var o in active.ToArray()) o.Finish();
        }

        public static void CrossFade(Control target, Action change, int ms = 200, int slideX = 0)
        {
            if (target == null || target.Parent == null || !target.IsHandleCreated || !target.Visible ||
                target.Width < 2 || target.Height < 2)
            {
                change();
                return;
            }

            Overlay ov = null;
            var inner = new List<Overlay>();
            foreach (var o in active.ToArray())
            {
                if (o.Target == target) ov = o;
                else if (target.Contains(o)) inner.Add(o);
                else if (o.Target.Contains(target))
                {
                    // An enclosing area is mid-transition toward a picture taken before this change.
                    // Changing now would pop in when that overlay lifts: run this one right after it.
                    o.Then(() => CrossFade(target, change, ms, slideX));
                    return;
                }
            }

            double t0 = Anim.Now;
            // The snapshot includes inner overlays mid-fade (they paint themselves), so ending them
            // afterwards is invisible.
            GdiBitmap before = ov != null ? ov.CaptureCurrent() : (GdiBitmap.CopyFromScreen(target) ?? GdiBitmap.Snap(target));
            foreach (var o in inner) o.Finish();
            if (before == null)
            {
                if (ov != null) ov.Finish();
                change();
                return;
            }
            if (ov == null)
            {
                ov = new Overlay(target);
                active.Add(ov);
                target.Parent.Controls.Add(ov);
                ov.BringToFront();
            }
            ov.Hold(before);   // the screen now shows "before", whatever happens below
            ov.Update();
            double t1 = Anim.Now;

            change();
            double t2 = Anim.Now;

            GdiBitmap after = GdiBitmap.Snap(target);
            if (after == null) { ov.Finish(); return; }
            ov.Start(after, slideX, ms);
            if (Anim.FrameLog != null)
                Anim.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "T\t{0:F1}\t{1}\tbefore {2:F1}\tchange {3:F1}\tafter {4:F1}",
                    t0, target.GetType().Name + " " + target.Width + "x" + target.Height, t1 - t0, t2 - t1, Anim.Now - t2));
        }

        class Overlay : Control
        {
            public readonly Control Target;
            GdiBitmap before, after;
            int slide;
            float t;
            bool finished;
            readonly object key = new object();
            List<Action> then;

            /// <summary>Runs once this overlay has lifted (on the next frame).</summary>
            public void Then(Action a)
            {
                if (then == null) then = new List<Action>();
                then.Add(a);
            }

            public Overlay(Control target)
            {
                Target = target;
                Bounds = target.Bounds;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.Selectable, false);
            }

            public void Hold(GdiBitmap picture)
            {
                Anim.Cancel(key);
                Swap(ref before, picture);
                Swap(ref after, null);
                t = 0;
                Invalidate();
            }

            public void Start(GdiBitmap picture, int slideX, int ms)
            {
                Swap(ref after, picture);
                slide = slideX;
                Anim.Run(key, ms, v => { t = v; Invalidate(); Update(); }, Finish);
            }

            /// <summary>The frame currently on screen, used as the starting point of an interrupting transition.</summary>
            public GdiBitmap CaptureCurrent()
            {
                if (before == null) return null;
                var bmp = GdiBitmap.Create(Width, Height);
                if (bmp == null) return null;
                Compose(bmp.Dc);
                return bmp;
            }

            static void Swap(ref GdiBitmap field, GdiBitmap value)
            {
                if (field != null && field != value) field.Dispose();
                field = value;
            }

            void Compose(IntPtr hdc)
            {
                if (before == null) return;
                GdiBitmap.BitBlt(hdc, 0, 0, Width, Height, before.Dc, 0, 0, GdiBitmap.SRCCOPY);
                if (after == null || t <= 0) return;
                int dx = (int)Math.Round(slide * (1 - t));
                int w = Width - Math.Abs(dx);
                if (w <= 0) return;
                GdiBitmap.Blend(hdc, Math.Max(0, dx), after.Dc, Math.Max(0, -dx), w, Height, (byte)Math.Round(255 * t));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                double t0 = Anim.FrameLog != null ? Anim.Now : 0;
                IntPtr hdc = e.Graphics.GetHdc();
                try { Compose(hdc); }
                finally { e.Graphics.ReleaseHdc(hdc); }
                if (t0 > 0) Anim.Log("O " + Width + "x" + Height + " " + (Anim.Now - t0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }

            public void Finish()
            {
                if (finished) return;
                finished = true;
                Anim.Cancel(key);
                active.Remove(this);
                if (Parent != null) Parent.Controls.Remove(this);
                Dispose();
                if (then != null)
                {
                    var queued = then;
                    then = null;
                    // Not inline: Finish may run in the middle of another transition's setup.
                    Anim.Delay(1, () => { foreach (var a in queued) a(); });
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Swap(ref before, null);
                    Swap(ref after, null);
                }
                base.Dispose(disposing);
            }
        }
    }

    static class WindowFx
    {
        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        /// <summary>
        /// Fade-in is done: drops the layered style (layered windows repaint slower, which shows in
        /// every later animation) and redraws the window whole.
        /// </summary>
        /// <summary>
        /// Paints the whole window now, while it is still transparent: the first paint (with code
        /// being compiled on first use) takes long enough to eat the start of a fade-in.
        /// </summary>
        public static void PaintNow(Form f)
        {
            // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW
            if (f.IsHandleCreated) RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        }

        public static void EndFade(Form f)
        {
            f.Opacity = 1;
            f.AllowTransparency = false;
            // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN
            if (f.IsHandleCreated) RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080);
        }
    }

    /// <summary>A screen-compatible GDI bitmap selected into its own memory DC.</summary>
    sealed class GdiBitmap : IDisposable
    {
        public IntPtr Dc { get; private set; }
        IntPtr bitmap, old;

        public const int SRCCOPY = 0x00CC0020;
        const int WM_PRINT = 0x0317;
        const int PRF_CHECKVISIBLE = 0x1, PRF_NONCLIENT = 0x2, PRF_CLIENT = 0x4, PRF_ERASEBKGND = 0x8, PRF_CHILDREN = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION bf);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        GdiBitmap() { }

        public static GdiBitmap Create(int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                var b = new GdiBitmap();
                b.Dc = CreateCompatibleDC(screen);
                b.bitmap = CreateCompatibleBitmap(screen, w, h);
                if (b.Dc == IntPtr.Zero || b.bitmap == IntPtr.Zero)
                {
                    b.Dispose();
                    return null;
                }
                b.old = SelectObject(b.Dc, b.bitmap);
                return b;
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>
        /// Copies what the control shows right now from the window's surface: a single blit, where
        /// re-rendering it (Snap) costs tens of milliseconds for a page full of lists. Null when the
        /// surface cannot be trusted (window hidden, minimized, still fading in, control clipped).
        /// </summary>
        public static GdiBitmap CopyFromScreen(Control c)
        {
            var form = c.FindForm();
            if (form == null || !form.Visible || form.WindowState == FormWindowState.Minimized || form.AllowTransparency) return null;
            var inForm = form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
            if (!form.ClientRectangle.Contains(inForm)) return null;
            for (var p = c.Parent; p != null && p != form; p = p.Parent)
                if (!p.ClientRectangle.Contains(p.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)))) return null;
            var b = Create(c.Width, c.Height);
            if (b == null) return null;
            IntPtr src = GetDC(c.Handle);
            bool ok;
            try { ok = BitBlt(b.Dc, 0, 0, c.Width, c.Height, src, 0, 0, SRCCOPY); }
            finally { ReleaseDC(c.Handle, src); }
            if (!ok) { b.Dispose(); return null; }
            return b;
        }

        /// <summary>Renders a control and its visible children off-screen (WM_PRINT), without touching the screen.</summary>
        public static GdiBitmap Snap(Control c)
        {
            var b = Create(c.Width, c.Height);
            if (b == null) return null;
            SendMessage(c.Handle, WM_PRINT, b.Dc, (IntPtr)(PRF_CHECKVISIBLE | PRF_NONCLIENT | PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN));
            return b;
        }

        public static void Blend(IntPtr dst, int x, IntPtr src, int sx, int w, int h, byte alpha)
        {
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };
            AlphaBlend(dst, x, 0, w, h, src, sx, 0, w, h, bf);
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (old != IntPtr.Zero) SelectObject(Dc, old);
                DeleteDC(Dc);
                Dc = IntPtr.Zero;
            }
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
                bitmap = IntPtr.Zero;
            }
        }
    }
}
