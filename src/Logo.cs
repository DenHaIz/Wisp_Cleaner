using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Wisp
{
    /// <summary>
    /// The Wisp mark: a broom drawn as a single-weight outline inside Lighter's open violet ring,
    /// on the dark gray tile. Used for the header, the window/taskbar/tray icon and (via
    /// tools\MakeIcon.cs) the .exe icon; the big clean button draws the ring alone (DrawMark).
    /// </summary>
    static class Logo
    {
        public static readonly Color Violet = Color.FromArgb(0x9D, 0x6B, 0xFF);
        public static readonly Color Tile = Color.FromArgb(0x2B, 0x2D, 0x31);

        public const float SolidStart = 270f;   // 12 o'clock (GDI angles grow clockwise from 3 o'clock)
        public const float SolidSweep = 236f;   // ends at ~8 o'clock
        public const float DustSpan = 360f - SolidSweep;

        static Icon appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                    appIcon = new Icon(new MemoryStream(BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 256 })));
                return appIcon;
            }
        }

        static readonly Color TileTop = Color.FromArgb(0x33, 0x35, 0x3B);
        static readonly Color TileBottom = Color.FromArgb(0x27, 0x28, 0x2C);

        public static void Draw(Graphics g, RectangleF r, bool tile)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float size = Math.Min(r.Width, r.Height);
            var box = new RectangleF(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            if (tile)
            {
                using (var path = Theme.RoundRect(box, size * 0.22f))
                using (var b = new LinearGradientBrush(new PointF(0, box.Top - 1), new PointF(0, box.Bottom + 1), TileTop, TileBottom))
                    g.FillPath(b, path);
            }
            // Everything below is drawn in a 256 x 256 box.
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(size / 256f, size / 256f);

            // Small icons (tray, title bar) get heavier lines so the mark survives 16 px.
            float ringW = size < 24 ? 24 : (size < 40 ? 17 : 10);
            float broomW = size < 24 ? 32 : (size < 40 ? 25 : 18);

            // Lighter's ring: open from about 8 o'clock back up to 12.
            using (var pen = new Pen(Violet, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, 128 - 88, 128 - 88, 176, 176, SolidStart, 247.4f);

            // The broom, upright in its own frame, tilted 40 degrees and scaled into the ring.
            g.TranslateTransform(128, 128);
            g.RotateTransform(40);
            // A touch smaller when small, so the thick broom keeps a gap to the ring.
            float k = size < 40 ? 0.56f : 0.62f;
            g.ScaleTransform(k, k);
            g.TranslateTransform(-128, -128);
            using (var pen = new Pen(Violet, broomW) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, 128, 30, 128, 128);
                using (var band = Theme.RoundRect(new RectangleF(100, 132, 56, 16), 6))
                    g.DrawPath(pen, band);
                using (var head = new GraphicsPath())
                {
                    head.AddLine(104, 156, 152, 156);
                    head.AddLine(152, 156, 180, 224);
                    head.AddBezier(180, 224, 145.33f, 234.67f, 110.67f, 234.67f, 76, 224);
                    head.CloseFigure();
                    g.DrawPath(pen, head);
                }
            }
            g.Restore(state);
        }

        /// <summary>
        /// The ring itself, also used at large size by the clean button: a solid arc of
        /// <paramref name="sweep"/> degrees from 12 o'clock, then dust specks thrown off its head
        /// and a sparkle in the cleared gap. Increasing <paramref name="phase"/> (0..1, wraps)
        /// carries the specks outward; with <paramref name="animated"/> they fade in at the head
        /// and fade out as they leave. <paramref name="sparkle"/> scales the sparkle (0 hides it).
        /// </summary>
        public static void DrawMark(Graphics g, PointF c, float ring, float stroke, Color color,
            float sweep, int specks, float phase, bool animated, float dustAlpha, float sparkle)
        {
            float radius = ring / 2 - stroke / 2;
            var arc = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
            if (sweep > 0.1f)
                using (var pen = new Pen(color, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                    g.DrawArc(pen, arc, SolidStart, Math.Min(360f, sweep));

            float head = SolidStart + sweep;
            if (specks > 0 && dustAlpha > 0.01f)
            {
                for (int k = 0; k < specks; k++)
                {
                    float u = (k + 0.5f + phase) / specks;
                    if (u >= 1f) u -= 1f;
                    float alpha = dustAlpha;
                    if (animated)
                    {
                        if (u < 0.15f) alpha *= u / 0.15f;
                        else if (u > 0.75f) alpha *= Math.Max(0, (1f - u) / 0.25f);
                    }
                    if (alpha <= 0.01f) continue;

                    // Specks leave the head along the ring, shrinking and drifting outward.
                    float v = u * 0.7f;
                    double ang = (head + 90f * v) * Math.PI / 180.0;
                    float d = stroke * (1.0f - 0.85f * v);
                    float rr = radius + stroke * 2.4f * (float)Math.Pow(v, 1.3);
                    float x = c.X + rr * (float)Math.Cos(ang), y = c.Y + rr * (float)Math.Sin(ang);
                    using (var b = new SolidBrush(Color.FromArgb((int)(color.A * Math.Min(1f, alpha)), color)))
                        g.FillEllipse(b, x - d / 2, y - d / 2, d, d);
                }
            }

            if (sparkle > 0.01f)
            {
                // Four-point sparkle in the open part of the ring, at about half past ten.
                double sa = (SolidStart + SolidSweep + DustSpan * 0.72f) * Math.PI / 180.0;
                float sr = radius + stroke * 0.15f;
                DrawSparkle(g, new PointF(c.X + sr * (float)Math.Cos(sa), c.Y + sr * (float)Math.Sin(sa)),
                    stroke * 1.75f * sparkle, Color.FromArgb((int)(color.A * Math.Min(1f, sparkle)), color));
            }
        }

        /// <summary>A four-point star with concave sides; r is the tip distance from the center.</summary>
        public static void DrawSparkle(Graphics g, PointF c, float r, Color color)
        {
            if (r < 0.3f) return;
            var tips = new[]
            {
                new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y)
            };
            float k = r * 0.14f;
            using (var path = new GraphicsPath())
            {
                for (int i = 0; i < 4; i++)
                {
                    var a = tips[i];
                    var b = tips[(i + 1) % 4];
                    // Both control points sit near the center, toward the corner between the two tips.
                    float mx = (a.X + b.X) / 2 - c.X, my = (a.Y + b.Y) / 2 - c.Y;
                    float len = (float)Math.Sqrt(mx * mx + my * my);
                    var cp = new PointF(c.X + mx / len * k, c.Y + my / len * k);
                    path.AddBezier(a, cp, cp, b);
                }
                path.CloseFigure();
                using (var br = new SolidBrush(color)) g.FillPath(br, path);
            }
        }

        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, new RectangleF(0, 0, size, size), true);
            }
            return bmp;
        }

        /// <summary>Multi-size .ico: 32-bit DIB frames for small sizes, PNG for 256.</summary>
        public static byte[] BuildIco(int[] sizes)
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i]))
                    frames[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
                return ms.ToArray();
            }
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] Dib(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(n);
                w.Write(n * 2);     // XOR + AND masks
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);
                w.Write(n * n * 4 + maskStride * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskStride * n]); // alpha channel does the masking
                return ms.ToArray();
            }
        }
    }
}
