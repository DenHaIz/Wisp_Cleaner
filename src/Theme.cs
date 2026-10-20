using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Wisp
{
    class Palette
    {
        public bool IsDark;
        public Color Bg, Surface, SurfaceAlt, Hover, Pressed, Border;
        public Color Text, TextDim, Accent, AccentText, Selected, SelectedInactive;
        public Color Danger, Success, Warning;
    }

    static class Theme
    {
        public static readonly Palette Dark = new Palette
        {
            IsDark = true,
            Bg = Hex(0x1B1C1F), Surface = Hex(0x232428), SurfaceAlt = Hex(0x2B2D31),
            Hover = Hex(0x33353B), Pressed = Hex(0x3D4048), Border = Hex(0x393B41),
            Text = Hex(0xE6E7EA), TextDim = Hex(0x9A9DA6), Accent = Hex(0x8B5CF6), AccentText = Color.White,
            Selected = Hex(0x3B2E66), SelectedInactive = Hex(0x34373E),
            Danger = Hex(0xFF6B6B), Success = Hex(0x3DD68C), Warning = Hex(0xF5B83D),
        };

        public static readonly Palette Light = new Palette
        {
            IsDark = false,
            Bg = Hex(0xF3F4F6), Surface = Hex(0xFFFFFF), SurfaceAlt = Hex(0xEDEEF1),
            Hover = Hex(0xE4E6EB), Pressed = Hex(0xD7DAE0), Border = Hex(0xD9DCE2),
            Text = Hex(0x1F2328), TextDim = Hex(0x6B7280), Accent = Hex(0x7C3AED), AccentText = Color.White,
            Selected = Hex(0xEDE5FE), SelectedInactive = Hex(0xE6E8EC),
            Danger = Hex(0xD93636), Success = Hex(0x16A34A), Warning = Hex(0xD97706),
        };

        public static Palette Current = Dark;

        /// <summary>Pixels per design pixel: monitor DPI times the window zoom.</summary>
        public static float Scale = 1f;
        /// <summary>Monitor DPI scale (1 = 96 dpi).</summary>
        public static float Dpi = 1f;
        /// <summary>Interface zoom that follows the window size (1 = default window, up to MaxZoom).</summary>
        public static float Zoom = 1f;
        public const float MaxZoom = 1.6f;
        public static Font UiFont, UiBold, SmallFont, TitleFont, HeadFont, BigFont, Glyph, GlyphSmall, GlyphLarge, GlyphHuge;
        public static bool HasGlyphs;

        // One font set per zoom step, kept: controls switch between them and old ones stay valid.
        static readonly System.Collections.Generic.Dictionary<int, Font[]> fontSets = new System.Collections.Generic.Dictionary<int, Font[]>();

        public static void Init(float dpiScale)
        {
            Dpi = dpiScale;
            Zoom = 0;
            SetZoom(1f);
            HasGlyphs = Glyph.Name == "Segoe MDL2 Assets";
            Current = Settings.Theme == "light" ? Light : Dark;
        }

        /// <summary>Switches sizes and fonts to the given zoom; false when it is already in effect.</summary>
        public static bool SetZoom(float zoom)
        {
            zoom = (float)Math.Round(Math.Max(1f, Math.Min(MaxZoom, zoom)) * 20) / 20;
            if (Math.Abs(zoom - Zoom) < 0.001f) return false;
            Zoom = zoom;
            Scale = Dpi * zoom;
            int key = (int)Math.Round(zoom * 100);
            Font[] f;
            if (!fontSets.TryGetValue(key, out f))
            {
                // Font sizes are in points, which GDI already scales by DPI: only the zoom applies here.
                f = new[]
                {
                    new Font("Segoe UI", 9.5f * zoom), new Font("Segoe UI Semibold", 9.5f * zoom), new Font("Segoe UI", 8.5f * zoom),
                    new Font("Segoe UI Semibold", 13f * zoom), new Font("Segoe UI Semibold", 11f * zoom), new Font("Segoe UI Semibold", 24f * zoom),
                    new Font("Segoe MDL2 Assets", 10f * zoom), new Font("Segoe MDL2 Assets", 8f * zoom),
                    new Font("Segoe MDL2 Assets", 14f * zoom), new Font("Segoe MDL2 Assets", 26f * zoom),
                };
                fontSets[key] = f;
            }
            UiFont = f[0]; UiBold = f[1]; SmallFont = f[2]; TitleFont = f[3]; HeadFont = f[4]; BigFont = f[5];
            Glyph = f[6]; GlyphSmall = f[7]; GlyphLarge = f[8]; GlyphHuge = f[9];
            return true;
        }

        /// <summary>The same font role (body, bold, small...) at the current zoom; other fonts are returned as they are.</summary>
        public static Font Remap(Font font)
        {
            Font[] now = fontSets[(int)Math.Round(Zoom * 100)];
            foreach (var set in fontSets.Values)
            {
                int i = Array.IndexOf(set, font);
                if (i >= 0) return now[i];
            }
            return font;
        }

        public static int S(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        /// <summary>Dark is the default; the choice is remembered in WispCleaner.ini.</summary>
        public static void Save()
        {
            Settings.Theme = Current.IsDark ? "dark" : "light";
            Settings.Save();
        }

        static Color Hex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var pen = new Pen(color))
                g.DrawPath(pen, path);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Dark or light window title bar (Windows 10 20H1+ / 11).</summary>
        public static void ApplyTitleBar(IntPtr hwnd)
        {
            int on = Current.IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
            catch { }
        }
    }
}
