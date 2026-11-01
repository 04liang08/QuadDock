using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuadDock.Core
{
    /// <summary>不可见的消息窗口：接收全局热键与系统的显示、主题变化。</summary>
    internal sealed class HotkeyWindow : Form
    {
        private const int HotkeyId = 0x5144;

        private readonly Action _toggle;
        private readonly Action _relayout;
        private readonly Action _themeChanged;
        private bool _registered;

        public HotkeyWindow(Action toggle, Action relayout, Action themeChanged)
        {
            _toggle = toggle;
            _relayout = relayout;
            _themeChanged = themeChanged;

            Text = "QuadDock";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-4000, -4000, 8, 8);
            Opacity = 0;

            // 强制建句柄，否则注册不上热键
            GC.KeepAlive(Handle);
        }

        public bool TryRegister(int modifiers, int key)
        {
            if (_registered)
            {
                Win32.UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }

            _registered = Win32.RegisterHotKey(
                Handle, HotkeyId, modifiers | Win32.MOD_NOREPEAT, key);
            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                if (_toggle != null)
                {
                    _toggle();
                }
                return;
            }

            if (m.Msg == Win32.WM_DISPLAYCHANGE || m.Msg == Win32.WM_SETTINGCHANGE)
            {
                base.WndProc(ref m);

                // lParam 指向变化区域的名字，切换深浅色时是 "ImmersiveColorSet"。
                // 只在这个名字命中时才重读主题，否则普通的工作区广播也会白白重绘四条侧栏。
                if (m.Msg == Win32.WM_SETTINGCHANGE && m.LParam != IntPtr.Zero && _themeChanged != null)
                {
                    string area = Marshal.PtrToStringUni(m.LParam);
                    if (string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal))
                    {
                        _themeChanged();
                    }
                }

                if (_relayout != null)
                {
                    _relayout();
                }
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_registered)
            {
                Win32.UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }
            base.OnFormClosed(e);
        }
    }
}
