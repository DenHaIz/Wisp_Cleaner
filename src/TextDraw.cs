using System;
using System.Drawing;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>
    /// All drawn text goes through here: GDI (TextRenderer) with the system's ClearType, crisp like the
    /// rest of Windows. Positions are rounded to whole pixels, where GDI draws text sharp. One place,
    /// so drawing and measuring always agree and the renderer can be changed for the whole app.
    /// </summary>
    static class TextDraw
    {
        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color)
        {
            TextRenderer.DrawText(g, text, font, r, color);
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags f)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 0 || r.Height <= 0) return;
            TextRenderer.DrawText(g, text, font, r, color, f);
        }

        /// <summary>The background is already painted under the text everywhere here.</summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, Color back, TextFormatFlags f)
        {
            DrawText(g, text, font, r, color, f);
        }

        /// <summary>For rows that move: snapped to the nearest pixel (not cut down), so text sits where its row is.</summary>
        public static void DrawText(Graphics g, string text, Font font, RectangleF r, Color color, TextFormatFlags f)
        {
            DrawText(g, text, font, Rectangle.Round(r), color, f);
        }

        public static void DrawText(Graphics g, string text, Font font, Point pt, Color color, TextFormatFlags f)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, pt, color, f);
        }

        public static Size MeasureText(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font);
        }

        public static Size MeasureText(string text, Font font, Size proposed, TextFormatFlags f)
        {
            return TextRenderer.MeasureText(text, font, proposed, f);
        }

        public static Size MeasureText(IDeviceContext dc, string text, Font font, Size proposed, TextFormatFlags f)
        {
            return TextRenderer.MeasureText(dc, text, font, proposed, f);
        }
    }

    /// <summary>A plain label drawn with <see cref="TextDraw"/> (headings, dialog text).</summary>
    class SmoothLabel : Label
    {
        public SmoothLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UseMnemonic = false;
        }

        TextFormatFlags Flags
        {
            get
            {
                var f = TextFormatFlags.NoPrefix | (AutoSize && MaximumSize.Width > 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                switch (TextAlign)
                {
                    case ContentAlignment.MiddleLeft: f |= TextFormatFlags.VerticalCenter; break;
                    case ContentAlignment.MiddleCenter: f |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter; break;
                    case ContentAlignment.MiddleRight: f |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right; break;
                    case ContentAlignment.TopCenter: f |= TextFormatFlags.HorizontalCenter; break;
                    case ContentAlignment.TopRight: f |= TextFormatFlags.Right; break;
                }
                return f;
            }
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int max = MaximumSize.Width > 0 ? MaximumSize.Width : proposed.Width;
            Size s = TextDraw.MeasureText(Text, Font, new Size(Math.Max(0, max - Padding.Horizontal), 0), Flags);
            s = new Size(s.Width + Padding.Horizontal, Math.Max(s.Height, Font.Height) + Padding.Vertical);
            if (MaximumSize.Width > 0) s.Width = Math.Min(s.Width, MaximumSize.Width);
            return s;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var r = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            TextDraw.DrawText(e.Graphics, Text, Font, r, Enabled ? ForeColor : Theme.Current.TextDim, Flags);
        }
    }
}
