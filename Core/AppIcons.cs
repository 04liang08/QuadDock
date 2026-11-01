using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuadDock.Core
{
    /// <summary>
    /// 取窗口对应的应用图标。
    ///
    /// 三条路，按可靠程度排：
    /// 1. UWP：所有 UWP 窗口都挂在 ApplicationFrameHost 上，WM_GETICON 只会给一个空白窗口图标。
    ///    必须读窗口的 AppUserModelID，再去 shell:AppsFolder 里取那个应用自己的图标。
    /// 2. 普通窗口：直接问窗口自己（WM_GETICON）。
    /// 3. 兜底：进程 exe 的图标。
    ///
    /// 结果按 exe 路径缓存（UWP 按窗口缓存，否则所有 UWP 应用会共用一个图标）。
    /// </summary>
    internal static class AppIcons
    {
        private const string FrameHost = "ApplicationFrameHost.exe";

        private static readonly Guid PkeyAppUserModelIdFormat = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint PkeyAppUserModelIdPid = 5;

        private static readonly Dictionary<string, Bitmap> Cache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        public static Bitmap ForWindow(IntPtr hwnd)
        {
            string path = ProcessPath(hwnd);
            bool frameHost = !string.IsNullOrEmpty(path) &&
                             string.Equals(Path.GetFileName(path), FrameHost, StringComparison.OrdinalIgnoreCase);

            string key = frameHost
                ? "uwp:" + hwnd.ToInt64()
                : (string.IsNullOrEmpty(path) ? "hwnd:" + hwnd.ToInt64() : path);

            lock (Gate)
            {
                Bitmap cached;
                if (Cache.TryGetValue(key, out cached))
                {
                    return cached;
                }
            }

            Bitmap icon = frameHost ? LoadUwp(hwnd, path) : LoadWindow(hwnd, path);

            lock (Gate)
            {
                // 只是兜底：正常一天也就几十个应用
                if (Cache.Count > 256)
                {
                    Cache.Clear();
                }
                Cache[key] = icon;
            }

            return icon;
        }

        private static Bitmap LoadUwp(IntPtr hwnd, string path)
        {
            Bitmap icon = FromAppUserModelId(hwnd);
            if (icon != null)
            {
                return icon;
            }

            // 退一步总比空白强
            return LoadWindow(hwnd, path);
        }

        private static Bitmap FromAppUserModelId(IntPtr hwnd)
        {
            string appId = AppUserModelId(hwnd);
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attributes;
                int hr = Win32.SHParseDisplayName(
                    "shell:AppsFolder\\" + appId, IntPtr.Zero, out pidl, 0, out attributes);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    return null;
                }

                var info = new SHFILEINFO();
                IntPtr result = Win32.SHGetFileInfo(
                    pidl,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    Win32.SHGFI_PIDL | Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Copy(info.hIcon);
                }
                finally
                {
                    Win32.DestroyIcon(info.hIcon);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    Win32.CoTaskMemFree(pidl);
                }
            }
        }

        private static string AppUserModelId(IntPtr hwnd)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (Win32.SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null)
                {
                    return null;
                }

                var key = new PROPERTYKEY
                {
                    fmtid = PkeyAppUserModelIdFormat,
                    pid = PkeyAppUserModelIdPid
                };

                PROPVARIANT value;
                if (store.GetValue(ref key, out value) != 0)
                {
                    return null;
                }

                try
                {
                    if (value.vt != PROPVARIANT.VT_LPWSTR || value.pointer == IntPtr.Zero)
                    {
                        return null;
                    }

                    return Marshal.PtrToStringUni(value.pointer);
                }
                finally
                {
                    Win32.PropVariantClear(ref value);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (store != null)
                {
                    Marshal.ReleaseComObject(store);
                }
            }
        }

        private static Bitmap LoadWindow(IntPtr hwnd, string path)
        {
            try
            {
                IntPtr handle = Win32.SendMessage(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_BIG, IntPtr.Zero);
                if (handle == IntPtr.Zero)
                {
                    handle = Win32.SendMessage(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_SMALL2, IntPtr.Zero);
                }
                if (handle == IntPtr.Zero)
                {
                    handle = Win32.SendMessage(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_SMALL, IntPtr.Zero);
                }

                if (handle != IntPtr.Zero)
                {
                    Bitmap fromWindow = Copy(handle);
                    if (fromWindow != null)
                    {
                        return fromWindow;
                    }
                }
            }
            catch
            {
                // 窗口可能在枚举和取图标之间关掉了，退回 exe 图标
            }

            return FromExecutable(path);
        }

        private static Bitmap FromExecutable(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                var info = new SHFILEINFO();
                IntPtr result = Win32.SHGetFileInfo(
                    path,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                handle = info.hIcon;
                return Copy(handle);
            }
            catch
            {
                return null;
            }
            finally
            {
                // SHGetFileInfo 给的图标要自己销毁；WM_GETICON 的那个归窗口所有，不能碰
                if (handle != IntPtr.Zero)
                {
                    Win32.DestroyIcon(handle);
                }
            }
        }

        /// <summary>
        /// 复制成 32 位带 alpha 的位图。用 DrawIconEx 而不是 Icon.ToBitmap：
        /// 后者对带 alpha 通道的图标会丢透明信息，画出来一圈黑底。
        /// </summary>
        private static Bitmap Copy(IntPtr hicon)
        {
            try
            {
                using (var icon = Icon.FromHandle(hicon))
                {
                    Size size = icon.Size;
                    if (size.Width <= 0 || size.Height <= 0)
                    {
                        return null;
                    }

                    var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.Transparent);
                        IntPtr hdc = g.GetHdc();
                        try
                        {
                            Win32.DrawIconEx(
                                hdc, 0, 0, hicon, size.Width, size.Height, 0, IntPtr.Zero, Win32.DI_NORMAL);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                    }

                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ProcessPath(IntPtr hwnd)
        {
            uint pid;
            Win32.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0)
            {
                return null;
            }

            IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var builder = new StringBuilder(1024);
                int size = builder.Capacity;
                if (!Win32.QueryFullProcessImageName(process, 0, builder, ref size))
                {
                    return null;
                }

                return builder.ToString(0, size);
            }
            finally
            {
                Win32.CloseHandle(process);
            }
        }
    }
}
