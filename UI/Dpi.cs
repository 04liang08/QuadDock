using System;
using System.Drawing;
using QuadDock.Core;

namespace QuadDock.UI
{
    internal static class Dpi
    {
        public static int Scale(int logical, int dpi)
        {
            return (int)Math.Round(logical * dpi / 96.0);
        }

        public static int ForMonitor(Rectangle monitorBounds)
        {
            try
            {
                var point = new POINT
                {
                    X = monitorBounds.Left + monitorBounds.Width / 2,
                    Y = monitorBounds.Top + monitorBounds.Height / 2
                };
                IntPtr monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    uint dpiX;
                    uint dpiY;
                    if (Win32.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0)
                    {
                        return (int)dpiX;
                    }
                }
            }
            catch
            {
                // 老系统缺少 shcore 导出时退回 96 DPI
            }

            return 96;
        }
    }
}
