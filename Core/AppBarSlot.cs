using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace QuadDock.Core
{
    /// <summary>
    /// 一个 AppBar 注册位。Windows 里每个 HWND 只能占一条边，
    /// 所以四条边各由一个独立的侧栏窗口注册。
    /// </summary>
    internal sealed class AppBarSlot
    {
        private readonly IntPtr _hwnd;
        private bool _registered;

        public AppBarSlot(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        /// <summary>
        /// 直接按算好的矩形占位。矩形由 DockManager 统一计算（风车式拼法），
        /// 这里不做 ABM_QUERYPOS：已经占过位之后再协商，自己的旧矩形会参与避让，边会被裁短。
        /// ABM_SETPOS 回填的 rc 会被外壳按横边条带裁掉竖边的角段（上/下边一旦注册，
        /// 竖边就只能落在两条横边之间），窗口位置必须用我们自己的矩形，否则四角会透出桌面。
        /// </summary>
        public void Place(Edge edge, Rectangle rect)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = _hwnd,
                uEdge = (uint)edge
            };

            if (!_registered)
            {
                _registered = Win32.SHAppBarMessage(Win32.ABM_NEW, ref data) != UIntPtr.Zero;
            }

            data.uEdge = (uint)edge;
            data.rc = new RECT(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref data);

            Win32.SetWindowPos(
                _hwnd,
                Win32.HWND_TOPMOST,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void Remove()
        {
            if (!_registered)
            {
                return;
            }

            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = _hwnd
            };
            Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref data);
            _registered = false;
        }
    }
}
