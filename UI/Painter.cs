using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace QuadDock.UI
{
    internal static class Painter
    {
        public static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int limit = Math.Min(rect.Width, rect.Height) / 2;
            int r = Math.Max(0, Math.Min(radius, limit));
            if (r <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            int d = r * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle rect, int radius, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (GraphicsPath path = RoundedPath(rect, radius))
                using (var brush = new SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }
            }
            finally
            {
                g.SmoothingMode = previous;
            }
        }

        /// <summary>描边。矩形向内收半个线宽，保证线画在给定的矩形之内。</summary>
        public static void StrokeRounded(Graphics g, Rectangle rect, int radius, Color color, int width)
        {
            int stroke = Math.Max(1, width);
            int inset = (stroke + 1) / 2;
            Rectangle inner = Rectangle.Inflate(rect, -inset, -inset);
            if (inner.Width <= 0 || inner.Height <= 0)
            {
                return;
            }

            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (GraphicsPath path = RoundedPath(inner, Math.Max(0, radius - inset)))
                using (var pen = new Pen(color, stroke))
                {
                    g.DrawPath(pen, path);
                }
            }
            finally
            {
                g.SmoothingMode = previous;
            }
        }
    }
}
