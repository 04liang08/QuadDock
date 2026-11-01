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
    }
}
