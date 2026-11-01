using System;
using System.Runtime.InteropServices;

namespace QuadDock.Core
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    internal static class Win32
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_GETICON = 0x007F;

        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL2 = 2;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_SMALLICON = 0x000000001;
        public const uint SHGFI_PIDL = 0x000000008;

        public const int DI_NORMAL = 0x0003;

        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;

        public const int DWMWA_CLOAKED = 14;

        public const int SW_RESTORE = 9;

        public const uint ABM_NEW = 0x0000;
        public const uint ABM_REMOVE = 0x0001;
        public const uint ABM_SETPOS = 0x0003;

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;
        public const int MOD_NOREPEAT = 0x4000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

        /// <summary>
        /// DWMWA_CLOAKED 非零表示窗口被 DWM "遮蔽"：UWP 应用在后台桌面时，
        /// 它的 ApplicationFrameWindow 宿主仍然 IsWindowVisible，只有这里能识别出来。
        /// </summary>
        public static bool IsCloaked(IntPtr hWnd)
        {
            try
            {
                int cloaked;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0)
                {
                    return cloaked != 0;
                }
            }
            catch
            {
                // 没有 dwmapi 或系统过老时直接当作未遮蔽
            }

            return false;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

        // ---------- 应用图标 ----------

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(
            IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int frame, IntPtr brush, int flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(
            IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(
            string path, uint attributes, ref SHFILEINFO info, uint infoSize, uint flags);

        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(
            IntPtr pidl, uint attributes, ref SHFILEINFO info, uint infoSize, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(
            string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(
            IntPtr hWnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT value);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr ptr);
    }

    /// <summary>只用到取 AppUserModelID 那一个属性，其余方法只为对齐 vtable 顺序。</summary>
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>x64 上是 24 字节，x86 上是 16 字节，这个布局两边都对上。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer;
        public IntPtr pointer2;

        public const ushort VT_LPWSTR = 31;
    }
}
