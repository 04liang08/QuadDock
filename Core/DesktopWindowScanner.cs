using System;
using System.Collections.Generic;
using System.Text;

namespace QuadDock.Core
{
    internal sealed class DesktopWindow
    {
        public DesktopWindow(IntPtr handle, string title, Guid desktopId, bool minimized)
        {
            Handle = handle;
            Title = title;
            DesktopId = desktopId;
            IsMinimized = minimized;
        }

        public IntPtr Handle { get; private set; }
        public string Title { get; private set; }
        public Guid DesktopId { get; private set; }
        public bool IsMinimized { get; private set; }
    }

    internal static class DesktopWindowScanner
    {
        /// <summary>桌面、壁纸层、任务栏，还有系统和输入法的宿主窗口，都不是"用户窗口"。</summary>
        private static readonly string[] ShellClasses =
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow"
        };

        /// <summary>按 Z 序列出所有虚拟桌面上的可展示窗口，带上它所属桌面的 GUID。</summary>
        public static List<DesktopWindow> Scan(VirtualDesktops desktops, uint ownProcessId)
        {
            var result = new List<DesktopWindow>();
            List<Guid> ids = desktops.GetDesktopIds();
            if (ids.Count == 0)
            {
                return result;
            }

            // 当前桌面在 ids 里的下标，用来区分"被虚拟桌面遮蔽"和"真的该藏起来"。
            int currentIndex = ids.IndexOf(desktops.GetCurrentDesktopId());

            Win32.EnumWindowsProc callback = delegate (IntPtr hwnd, IntPtr param)
            {
                if (!Win32.IsWindowVisible(hwnd))
                {
                    return true;
                }

                long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
                if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }

                if (IsShellWindow(hwnd))
                {
                    return true;
                }

                int length = Win32.GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                uint pid;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == ownProcessId)
                {
                    return true;
                }

                bool minimized = Win32.IsIconic(hwnd);
                if (!minimized)
                {
                    RECT rect;
                    if (!Win32.GetWindowRect(hwnd, out rect) || rect.Width < 64 || rect.Height < 48)
                    {
                        return true;
                    }
                }

                Guid desktopId;
                if (!desktops.TryGetWindowDesktopId(hwnd, out desktopId))
                {
                    return true;
                }

                int desktopIndex = ids.IndexOf(desktopId);
                if (desktopIndex < 0)
                {
                    return true;
                }

                // 虚拟桌面就是靠 DWM 遮蔽实现的：别的桌面上的窗口一定是 cloaked，
                // 所以不能一刀切。只有"声称就在当前桌面上、却被遮蔽"的才是真幽灵
                //（挂起的 UWP、关掉了但进程没退的框架窗口）。
                if (desktopIndex == currentIndex && Win32.IsCloaked(hwnd))
                {
                    return true;
                }

                var buffer = new StringBuilder(Math.Min(length, 512) + 1);
                Win32.GetWindowText(hwnd, buffer, buffer.Capacity);
                string title = buffer.ToString().Trim();
                if (title.Length == 0)
                {
                    return true;
                }

                result.Add(new DesktopWindow(hwnd, title, desktopId, minimized));
                return true;
            };

            Win32.EnumWindows(callback, IntPtr.Zero);
            return result;
        }

        private static bool IsShellWindow(IntPtr hwnd)
        {
            var buffer = new StringBuilder(64);
            if (Win32.GetClassName(hwnd, buffer, buffer.Capacity) <= 0)
            {
                return false;
            }

            string name = buffer.ToString();
            foreach (string shellClass in ShellClasses)
            {
                if (string.Equals(name, shellClass, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
