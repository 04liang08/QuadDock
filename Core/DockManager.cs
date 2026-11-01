using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using QuadDock.UI;

namespace QuadDock.Core
{
    /// <summary>四条侧栏的总控：AppBar 排布、虚拟桌面切换与标签、托盘与热键。</summary>
    internal sealed class DockManager : ApplicationContext
    {
        private static readonly Edge[] Order = { Edge.Left, Edge.Right, Edge.Top, Edge.Bottom };

        private readonly DockSettings _settings;
        private readonly Dictionary<Edge, StripForm> _strips = new Dictionary<Edge, StripForm>();
        private readonly Dictionary<Edge, AppBarSlot> _bars = new Dictionary<Edge, AppBarSlot>();
        private readonly Dictionary<Edge, Rectangle> _placed = new Dictionary<Edge, Rectangle>();

        /// <summary>五个槽位（0 中央、1 左、2 上、3 右、4 下）各自压着的桌面 GUID。</summary>
        private readonly Guid[] _slotDesktop = new Guid[DockSettings.SlotCount];

        /// <summary>每个桌面上"上次用过的那个窗口"，用来在侧栏里做标记。</summary>
        private readonly Dictionary<Guid, IntPtr> _recent = new Dictionary<Guid, IntPtr>();

        private readonly VirtualDesktops _desktops = new VirtualDesktops();
        private readonly HotkeyWindow _hotkey;
        private readonly System.Windows.Forms.Timer _commitTimer;
        private readonly System.Windows.Forms.Timer _scanTimer;
        private readonly System.Windows.Forms.Timer _followTimer;
        private readonly System.Windows.Forms.Timer _switchTimer;

        private ContextMenuStrip _menu;
        private NotifyIcon _tray;
        private Rectangle _monitor;
        private Rectangle _available;
        private string _enabledMask = "";
        private int _monitorDpi = 96;
        private IntPtr _switchTarget;
        private int _switchAttempts;
        private int _lastForcedLayout;
        private bool _dragging;
        private bool _negotiated;
        private bool _allHidden;
        private bool _applying;
        private bool _quitting;

        public DockManager()
        {
            _settings = SettingsStore.Load();
            Theme.Apply((ThemeMode)_settings.ThemeMode);
            Theme.ApplyStyle((StripStyle)_settings.Style);

            foreach (Edge edge in Order)
            {
                _strips[edge] = new StripForm(edge, this);
            }

            _hotkey = new HotkeyWindow(ToggleAll, OnDisplayChanged, OnSystemThemeChanged);
            MainForm = _hotkey;
            _hotkey.TryRegister(_settings.HotkeyModifiers, _settings.HotkeyKey);

            BuildTray();

            _commitTimer = new System.Windows.Forms.Timer { Interval = 130 };
            _commitTimer.Tick += (s, e) => CommitPending();

            _desktops.TryInitialize();
            ResolveSlots();

            _scanTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            _scanTimer.Tick += (s, e) => RefreshDesktopWindows();

            _followTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _followTimer.Tick += (s, e) => FollowCurrentDesktop(false);

            _switchTimer = new System.Windows.Forms.Timer { Interval = 160 };
            _switchTimer.Tick += (s, e) => CompleteSwitch();

            ApplyLayout();

            RefreshDesktopWindows();
            _scanTimer.Start();
            _followTimer.Start();
        }

        // ---------- AppBar 布局 ----------

        /// <summary>
        /// 两种模式：
        /// 结构变化（首次布局 / 开关某条边 / 换屏 / 任务栏变化）时重新协商，
        /// 协商前先把四条边全部摘掉，否则我们自己的旧矩形会参与 ABM_QUERYPOS 的避让计算，
        /// 结果是边被裁短、四角露出缺口（只有下次再调一下才对得上）。
        /// 纯粗细变化时走 Place：矩形我们自己算好了，直接 SETPOS，不再协商。
        /// </summary>
        internal void ApplyLayout()
        {
            ApplyLayout(false);
        }

        internal void ApplyLayout(bool forceNegotiate)
        {
            if (_quitting || _applying)
            {
                return;
            }

            _applying = true;
            try
            {
                Rectangle monitor = MonitorBounds();
                int dpi = Dpi.ForMonitor(monitor);
                string mask = EnabledMask();

                bool structural = forceNegotiate ||
                                  !_negotiated ||
                                  monitor != _monitor ||
                                  dpi != _monitorDpi ||
                                  mask != _enabledMask;

                _monitor = monitor;
                _monitorDpi = dpi;
                _enabledMask = mask;

                if (structural)
                {
                    Negotiate(Thickness(dpi));
                }
                else
                {
                    Place(Thickness(dpi));
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
            finally
            {
                _applying = false;
            }
        }

        private string EnabledMask()
        {
            var builder = new StringBuilder(4);
            foreach (Edge edge in Order)
            {
                builder.Append(_settings.IsEnabled(edge) && !_allHidden ? '1' : '0');
            }
            return builder.ToString();
        }

        private Dictionary<Edge, int> Thickness(int dpi)
        {
            var thickness = new Dictionary<Edge, int>();
            foreach (Edge edge in Order)
            {
                bool on = _settings.IsEnabled(edge) && !_allHidden;
                thickness[edge] = on ? Dpi.Scale(_settings.GetThickness(edge), dpi) : 0;
            }

            ClampThickness(thickness, _monitor);
            return thickness;
        }

        /// <summary>
        /// 结构变化：先把四条边全部摘掉（摘掉后系统工作区回到只剩任务栏），
        /// 读一次真正的可用区域，再按风车式重新占位。这样每次得到的结果都一样。
        /// </summary>
        private void Negotiate(Dictionary<Edge, int> thickness)
        {
            foreach (Edge edge in Order)
            {
                AppBarSlot existing;
                if (_bars.TryGetValue(edge, out existing))
                {
                    existing.Remove();
                }
            }

            _placed.Clear();
            _available = AvailableRect(_monitor);
            PlaceCore(thickness);
            _negotiated = true;
        }

        private void Place(Dictionary<Edge, int> thickness)
        {
            PlaceCore(thickness);
        }

        private void PlaceCore(Dictionary<Edge, int> thickness)
        {
            foreach (Edge edge in Order)
            {
                StripForm strip = _strips[edge];
                if (thickness[edge] <= 0)
                {
                    if (strip.Visible)
                    {
                        strip.Hide();
                    }
                }
            }

            Dictionary<Edge, Rectangle> rects = ComputeRects(thickness);

            foreach (Edge edge in Order)
            {
                Rectangle target;
                if (!rects.TryGetValue(edge, out target))
                {
                    continue;
                }

                // 矩形没变就别碰：每次 SETPOS 都会让系统重新计算工作区，
                // 最大化窗口会被依次推几次，看起来就是松手时的抖动。
                Rectangle current;
                if (_placed.TryGetValue(edge, out current) && current == target)
                {
                    continue;
                }

                Bar(edge).Place(edge, target);
                _strips[edge].SyncBounds(target);

                // 窗口是 SetWindowPos(SWP_SHOWWINDOW) 显示出来的，WinForms 的 Visible 仍是 false，
                // 后面所有 strip.Visible 判断都会失灵（扫描结果到不了侧栏）。这里补一次 Show 同步托管状态。
                _strips[edge].Show();
                _placed[edge] = target;
            }
        }

        /// <summary>
        /// 风车式拼法：每个角只由一条边延伸过去独占。
        /// 上边→占左上角，右边→占右上角，下边→占右下角，左边→占左下角。
        /// </summary>
        private Dictionary<Edge, Rectangle> ComputeRects(Dictionary<Edge, int> thickness)
        {
            var rects = new Dictionary<Edge, Rectangle>();
            Rectangle area = _available;

            int left = thickness[Edge.Left];
            int top = thickness[Edge.Top];
            int right = thickness[Edge.Right];
            int bottom = thickness[Edge.Bottom];

            int contentLeft = area.Left + left;
            int contentTop = area.Top + top;
            int contentRight = area.Right - right;
            int contentBottom = area.Bottom - bottom;

            if (contentRight - contentLeft < 80 || contentBottom - contentTop < 60)
            {
                return rects;
            }

            if (top > 0)
            {
                rects[Edge.Top] = new Rectangle(area.Left, area.Top, contentRight - area.Left, top);
            }

            if (right > 0)
            {
                rects[Edge.Right] = new Rectangle(contentRight, area.Top, right, contentBottom - area.Top);
            }

            if (bottom > 0)
            {
                rects[Edge.Bottom] = new Rectangle(
                    contentLeft, contentBottom, area.Right - contentLeft, bottom);
            }

            if (left > 0)
            {
                rects[Edge.Left] = new Rectangle(area.Left, contentTop, left, area.Bottom - contentTop);
            }

            return rects;
        }

        private static Rectangle AvailableRect(Rectangle monitor)
        {
            try
            {
                var point = new POINT
                {
                    X = monitor.Left + monitor.Width / 2,
                    Y = monitor.Top + monitor.Height / 2
                };
                IntPtr handle = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
                if (handle != IntPtr.Zero)
                {
                    var info = new MONITORINFO();
                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                    if (Win32.GetMonitorInfo(handle, ref info))
                    {
                        Rectangle work = Rectangle.FromLTRB(
                            info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
                        return ExcludeTaskbar(work, monitor);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }

            return ExcludeTaskbar(monitor, monitor);
        }

        /// <summary>
        /// 换屏、全屏程序、外壳刚重启这些时候，读到的 rcWork 会等于整个屏幕——任务栏没被算进去。
        /// 这时底部侧栏会直接压到任务栏上。既然一个边都没被排除，就按任务栏自己的矩形再裁一次。
        /// 自动隐藏的任务栏不裁：那种情况下全屏本来就是对的。
        /// </summary>
        private static Rectangle ExcludeTaskbar(Rectangle work, Rectangle monitor)
        {
            if (work != monitor)
            {
                return work;
            }

            try
            {
                var state = new APPBARDATA();
                state.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref state);
                if ((state.lParam.ToInt64() & Win32.ABS_AUTOHIDE) != 0)
                {
                    return work;
                }

                var data = new APPBARDATA();
                data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                if (Win32.SHAppBarMessage(Win32.ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
                {
                    return work;
                }

                Rectangle bar = Rectangle.FromLTRB(
                    data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
                if (bar.Width <= 0 || bar.Height <= 0 || !monitor.IntersectsWith(bar))
                {
                    return work;
                }

                switch (data.uEdge)
                {
                    case Win32.ABE_LEFT:
                        return Rectangle.FromLTRB(
                            Math.Max(work.Left, bar.Right), work.Top, work.Right, work.Bottom);
                    case Win32.ABE_TOP:
                        return Rectangle.FromLTRB(
                            work.Left, Math.Max(work.Top, bar.Bottom), work.Right, work.Bottom);
                    case Win32.ABE_RIGHT:
                        return Rectangle.FromLTRB(
                            work.Left, work.Top, Math.Min(work.Right, bar.Left), work.Bottom);
                    default:
                        return Rectangle.FromLTRB(
                            work.Left, work.Top, work.Right, Math.Min(work.Bottom, bar.Top));
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                return work;
            }
        }

        private AppBarSlot Bar(Edge edge)
        {
            AppBarSlot slot;
            if (!_bars.TryGetValue(edge, out slot))
            {
                slot = new AppBarSlot(_strips[edge].Handle);
                _bars[edge] = slot;
            }
            return slot;
        }

        /// <summary>四面都占位时留一块最小主区，避免把工作区挤没。</summary>
        private static void ClampThickness(Dictionary<Edge, int> thickness, Rectangle monitor)
        {
            int minMainWidth = Math.Max(280, monitor.Width / 5);
            int minMainHeight = Math.Max(200, monitor.Height / 5);

            int horizontalBudget = Math.Max(80, monitor.Width - minMainWidth);
            int sum = thickness[Edge.Left] + thickness[Edge.Right];
            if (sum > horizontalBudget && sum > 0)
            {
                double factor = (double)horizontalBudget / sum;
                thickness[Edge.Left] = Math.Max(40, (int)Math.Round(thickness[Edge.Left] * factor));
                thickness[Edge.Right] = Math.Max(40, (int)Math.Round(thickness[Edge.Right] * factor));
            }

            int verticalBudget = Math.Max(60, monitor.Height - minMainHeight);
            sum = thickness[Edge.Top] + thickness[Edge.Bottom];
            if (sum > verticalBudget && sum > 0)
            {
                double factor = (double)verticalBudget / sum;
                thickness[Edge.Top] = Math.Max(30, (int)Math.Round(thickness[Edge.Top] * factor));
                thickness[Edge.Bottom] = Math.Max(30, (int)Math.Round(thickness[Edge.Bottom] * factor));
            }
        }

        internal void BeginDrag()
        {
            _dragging = true;
        }

        /// <summary>拖动过程只做纯几何：算出四条边的新矩形直接挪窗口，不跟系统协商 AppBar。</summary>
        internal void PreviewThickness(Edge edge, int pixels)
        {
            if (!_dragging)
            {
                return;
            }

            int dpi = _monitorDpi > 0 ? _monitorDpi : 96;
            int min = Dpi.Scale(edge.IsVertical() ? DockSettings.MinVertical : DockSettings.MinHorizontal, dpi);
            int max = Dpi.Scale(edge.IsVertical() ? DockSettings.MaxVertical : DockSettings.MaxHorizontal, dpi);

            var thickness = new Dictionary<Edge, int>();
            foreach (Edge e in Order)
            {
                thickness[e] = _settings.IsEnabled(e) && !_allHidden
                    ? Dpi.Scale(_settings.GetThickness(e), dpi)
                    : 0;
            }
            thickness[edge] = Math.Max(min, Math.Min(max, pixels));

            ClampThickness(thickness, _monitor);

            Dictionary<Edge, Rectangle> rects = ComputeRects(thickness);
            foreach (Edge e in Order)
            {
                Rectangle target;
                if (rects.TryGetValue(e, out target))
                {
                    _strips[e].SyncBounds(target);
                }
            }
        }

        internal void EndDrag(Edge edge, int pixels)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;

            int dpi = _monitorDpi > 0 ? _monitorDpi : 96;
            _settings.SetThickness(edge, (int)Math.Round(pixels * 96.0 / dpi));
            SettingsStore.Save(_settings);

            // 松手后稍微延迟再提交 AppBar：连续拖几次会被合并成一次工作区变化，
            // 而且延迟期间侧栏已经就位，最大化窗口只会平顺地缩一次。
            _commitTimer.Stop();
            _commitTimer.Start();
        }

        private void CommitPending()
        {
            _commitTimer.Stop();
            ApplyLayout();
        }

        internal void ResetThickness(Edge edge)
        {
            _settings.SetThickness(
                edge,
                edge.IsVertical() ? DockSettings.DefaultVertical : DockSettings.DefaultHorizontal);
            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        // ---------- 虚拟桌面 ----------

        /// <summary>
        /// 五个槽位铺开：先按设置里的排列填入，再把"你现在所在的桌面"换到中央，
        /// 这样启动时不会把人弹到别的桌面去。
        /// </summary>
        private void ResolveSlots()
        {
            if (!_desktops.Available)
            {
                return;
            }

            _desktops.EnsureCount(DockSettings.SlotCount);

            List<Guid> ids = _desktops.GetDesktopIds();
            if (ids.Count < DockSettings.SlotCount)
            {
                return;
            }

            for (int slot = 0; slot < DockSettings.SlotCount; slot++)
            {
                int position = _settings.GetSlot(slot);
                if (position < 1 || position > DockSettings.SlotCount)
                {
                    _settings.ResetSlots();
                    position = slot + 1;
                }

                _slotDesktop[slot] = ids[position - 1];
            }

            Guid current = _desktops.GetCurrentDesktopId();
            int currentIndex = ids.IndexOf(current);
            if (currentIndex < 0 || currentIndex >= DockSettings.SlotCount)
            {
                // 停在这 5 个之外（用户自己加过桌面），先回中央槽，保证模型自洽。
                _desktops.SwitchToDesktop(_slotDesktop[0]);
                return;
            }

            int holder = Array.IndexOf(_slotDesktop, current);
            if (holder > 0)
            {
                SwapSlots(0, holder);
            }

            PersistSlots();
        }

        private void SwapSlots(int first, int second)
        {
            Guid temp = _slotDesktop[first];
            _slotDesktop[first] = _slotDesktop[second];
            _slotDesktop[second] = temp;
        }

        /// <summary>把槽位映射换算成"第几个桌面"存下去，下次启动还能还原。</summary>
        private void PersistSlots()
        {
            if (!_desktops.Available)
            {
                return;
            }

            List<Guid> ids = _desktops.GetDesktopIds();
            if (ids.Count < DockSettings.SlotCount)
            {
                return;
            }

            for (int slot = 0; slot < DockSettings.SlotCount; slot++)
            {
                int position = ids.IndexOf(_slotDesktop[slot]) + 1;
                if (position <= 0 || position > DockSettings.SlotCount)
                {
                    return;
                }

                _settings.SetSlot(slot, position);
            }

            SettingsStore.Save(_settings);
        }

        /// <summary>把每个槽位上的窗口标题分发到对应的侧栏。</summary>
        private void RefreshDesktopWindows()
        {
            if (_quitting || _allHidden)
            {
                return;
            }

            List<DesktopWindow> windows = _desktops.Available
                ? DesktopWindowScanner.Scan(_desktops, (uint)Environment.ProcessId)
                : new List<DesktopWindow>();
            List<Guid> ids = _desktops.Available ? _desktops.GetDesktopIds() : new List<Guid>();

            // 顺手记下"现在前台的是哪个窗口、属于哪个桌面"。
            // 前台窗口一定在中央那个桌面上，只有靠这条记录它才能在别的侧栏里留下标记。
            IntPtr foreground = Win32.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                foreach (DesktopWindow window in windows)
                {
                    if (window.Handle == foreground)
                    {
                        _recent[window.DesktopId] = foreground;
                        break;
                    }
                }
            }

            foreach (Edge edge in Order)
            {
                Guid desktop = _slotDesktop[DockSettings.SlotOf(edge)];

                var list = new List<DesktopWindow>();
                foreach (DesktopWindow window in windows)
                {
                    if (window.DesktopId == desktop)
                    {
                        list.Add(window);
                    }
                }

                int position = ids.IndexOf(desktop) + 1;
                _strips[edge].SetDesktopNumber(position);

                IntPtr recent;
                if (!_recent.TryGetValue(desktop, out recent))
                {
                    recent = IntPtr.Zero;
                }
                _strips[edge].SetRecent(recent);

                _strips[edge].SetWindows(list);
            }
        }

        /// <summary>侧栏窗口跟着当前桌面走，否则切换后就点不到了。</summary>
        private void FollowCurrentDesktop(bool force)
        {
            if (_quitting || !_desktops.Available)
            {
                return;
            }

            // 交换引起的切换还没走完，这会儿读到的当前桌面还是旧的，先别改模型。
            if (_switchTimer.Enabled)
            {
                return;
            }

            ReconcileCenter();

            Guid current = _desktops.GetCurrentDesktopId();
            if (current == Guid.Empty)
            {
                return;
            }

            if (!force && _desktops.IsWindowOnCurrentDesktop(_strips[Edge.Top].Handle))
            {
                return;
            }

            foreach (StripForm strip in _strips.Values)
            {
                if (force || !_desktops.IsWindowOnCurrentDesktop(strip.Handle))
                {
                    _desktops.MoveWindowToDesktop(strip.Handle, current);
                }
            }
        }

        /// <summary>
        /// 用户可能在任务视图里自己切了桌面。切到的那个如果正压在某条边下面，
        /// 就把它和中央换一下——和点侧栏是同一个语义，模型始终自洽。
        /// </summary>
        private void ReconcileCenter()
        {
            Guid current = _desktops.GetCurrentDesktopId();
            if (current == Guid.Empty || current == _slotDesktop[0])
            {
                return;
            }

            int holder = Array.IndexOf(_slotDesktop, current);
            if (holder > 0)
            {
                SwapSlots(0, holder);
            }
            else if (holder < 0)
            {
                _slotDesktop[0] = current;
            }
            else
            {
                return;
            }

            PersistSlots();
            RefreshDesktopWindows();
        }

        /// <summary>点击侧栏：把它压着的桌面和中央交换。</summary>
        internal void ClickStrip(Edge edge)
        {
            if (_quitting)
            {
                return;
            }

            BeginSwap(DockSettings.SlotOf(edge), IntPtr.Zero);
        }

        /// <summary>点击窗口标签：先把它所在的桌面换到中央，再激活那个窗口。</summary>
        internal void ActivateWindow(IntPtr hwnd, Guid desktopId)
        {
            if (_quitting)
            {
                return;
            }

            int slot = Array.IndexOf(_slotDesktop, desktopId);
            if (slot > 0)
            {
                BeginSwap(slot, hwnd);
                return;
            }

            ActivateForeground(hwnd);
        }

        /// <summary>交换中央和指定槽位，然后把系统切到新的中央桌面。</summary>
        private void BeginSwap(int slot, IntPtr activate)
        {
            if (slot <= 0 || slot >= DockSettings.SlotCount)
            {
                return;
            }

            SwapSlots(0, slot);
            PersistSlots();
            RefreshDesktopWindows();

            _switchTarget = activate;
            _switchAttempts = 0;

            _desktops.SwitchToDesktop(_slotDesktop[0]);

            _switchTimer.Stop();
            _switchTimer.Start();
        }

        /// <summary>
        /// 桌面切换是异步的（Win11 上还带动画），返回时前台和当前桌面都还没落定。
        /// 等到真落定了再搬侧栏、激活窗口，否则会搬到旧桌面上。
        /// </summary>
        private void CompleteSwitch()
        {
            _switchTimer.Stop();
            if (_quitting)
            {
                return;
            }

            bool settled = _desktops.GetCurrentDesktopId() == _slotDesktop[0] &&
                           (_switchTarget == IntPtr.Zero ||
                            _desktops.IsWindowOnCurrentDesktop(_switchTarget));

            // 最多重试约 1 秒。两个查询出错时都会退化成"已落定"，这里不会打转。
            if (!settled && _switchAttempts++ < 6)
            {
                _switchTimer.Start();
                return;
            }

            FollowCurrentDesktop(true);
            RefreshDesktopWindows();

            IntPtr target = _switchTarget;
            _switchTarget = IntPtr.Zero;
            if (target != IntPtr.Zero)
            {
                ActivateForeground(target);
            }
        }

        private static void ActivateForeground(IntPtr hwnd)
        {
            if (!Win32.IsWindow(hwnd))
            {
                return;
            }

            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            }

            if (Win32.SetForegroundWindow(hwnd))
            {
                return;
            }

            IntPtr foreground = Win32.GetForegroundWindow();
            uint foregroundThread = foreground != IntPtr.Zero
                ? Win32.GetWindowThreadProcessId(foreground, out _)
                : 0;
            uint currentThread = Win32.GetCurrentThreadId();
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                Win32.AttachThreadInput(currentThread, foregroundThread, true);
                Win32.SetForegroundWindow(hwnd);
                Win32.AttachThreadInput(currentThread, foregroundThread, false);
            }
            Win32.BringWindowToTop(hwnd);
        }

        // ---------- 菜单与托盘 ----------

        private void BuildTray()
        {
            _menu = new ContextMenuStrip { ShowImageMargin = false };
            _menu.Opening += (s, e) => RebuildMenu();

            _tray = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "QuadDock · 四边侧栏",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _tray.DoubleClick += (s, e) => ToggleAll();
        }

        internal void ShowMenu(Control anchor, Point location)
        {
            _menu.Show(anchor, location);
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();

            foreach (Edge edge in Order)
            {
                Edge captured = edge;
                var item = new ToolStripMenuItem(
                    (_settings.IsEnabled(edge) ? "✓  " : "     ") + edge.Label());
                item.Click += (s, e) => ToggleEdge(captured);
                _menu.Items.Add(item);
            }

            _menu.Items.Add(SlotMenu());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(PresetMenu("左右宽度", true, new[] { 80, 110, 160, 220, 300 }));
            _menu.Items.Add(PresetMenu("上下高度", false, new[] { 64, 90, 130, 190 }));

            _menu.Items.Add(new ToolStripSeparator());
            var toggle = new ToolStripMenuItem(_allHidden ? "显示全部侧栏    Ctrl+Alt+Space" : "隐藏全部侧栏    Ctrl+Alt+Space");
            toggle.Click += (s, e) => ToggleAll();
            _menu.Items.Add(toggle);

            _menu.Items.Add(ThemeMenu());
            _menu.Items.Add(StyleMenu());
            _menu.Items.Add(MonitorMenu());
            _menu.Items.Add(StartupItem());

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(
                "点侧栏交换桌面 · 点标签换到中间并激活 · 拖动内边缘调宽窄 · 双击复位") { Enabled = false });

            _menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("退出 QuadDock");
            exit.Click += (s, e) => Quit();
            _menu.Items.Add(exit);
        }

        private ToolStripMenuItem PresetMenu(string title, bool vertical, int[] presets)
        {
            var menu = new ToolStripMenuItem(title);

            // 左右（上下）可以从两侧分别拖成不同宽度，这时没有哪一档能被勾上。
            int current = vertical
                ? (_settings.LeftThickness == _settings.RightThickness ? _settings.LeftThickness : 0)
                : (_settings.TopThickness == _settings.BottomThickness ? _settings.TopThickness : 0);

            foreach (int preset in presets)
            {
                int captured = preset;
                var item = new ToolStripMenuItem(
                    (preset == current ? "✓  " : "     ") + preset + " px");
                item.Click += (s, e) => ApplyPreset(vertical, captured);
                menu.DropDownItems.Add(item);
            }

            return menu;
        }

        private static readonly string[] SlotNames = { "中央", "左侧栏", "上侧栏", "右侧栏", "下侧栏" };

        /// <summary>哪个桌面此刻压在哪——只读展示，分配靠点侧栏交换完成。</summary>
        private ToolStripMenuItem SlotMenu()
        {
            var root = new ToolStripMenuItem("桌面对应");

            if (!_desktops.Available)
            {
                root.DropDownItems.Add(new ToolStripMenuItem("虚拟桌面接口不可用") { Enabled = false });
                return root;
            }

            List<Guid> ids = _desktops.GetDesktopIds();
            for (int slot = 0; slot < DockSettings.SlotCount; slot++)
            {
                int position = ids.IndexOf(_slotDesktop[slot]) + 1;
                string text = SlotNames[slot] + "  ←  桌面 "
                              + (position > 0 ? position.ToString() : "—");
                root.DropDownItems.Add(new ToolStripMenuItem(text) { Enabled = false });
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            var reset = new ToolStripMenuItem("重置为默认分配");
            reset.Click += (s, e) => ResetSlots();
            root.DropDownItems.Add(reset);

            return root;
        }

        private void ResetSlots()
        {
            _settings.ResetSlots();
            SettingsStore.Save(_settings);
            ResolveSlots();
            RefreshDesktopWindows();
        }

        private ToolStripMenuItem ThemeMenu()
        {
            var menu = new ToolStripMenuItem("主题");
            AddThemeItem(menu, "跟随系统", ThemeMode.System);
            AddThemeItem(menu, "浅色", ThemeMode.Light);
            AddThemeItem(menu, "深色", ThemeMode.Dark);
            return menu;
        }

        private void AddThemeItem(ToolStripMenuItem parent, string text, ThemeMode mode)
        {
            var item = new ToolStripMenuItem(
                (_settings.ThemeMode == (int)mode ? "✓  " : "     ") + text);
            item.Click += (s, e) => SetTheme(mode);
            parent.DropDownItems.Add(item);
        }

        private ToolStripMenuItem StyleMenu()
        {
            var menu = new ToolStripMenuItem("外观风格");
            AddStyleItem(menu, "卡片（投影 + 圆角）", StripStyle.Card);
            AddStyleItem(menu, "列表（细分隔线）", StripStyle.List);
            return menu;
        }

        private void AddStyleItem(ToolStripMenuItem parent, string text, StripStyle style)
        {
            var item = new ToolStripMenuItem(
                (_settings.Style == (int)style ? "✓  " : "     ") + text);
            item.Click += (s, e) => SetStyle(style);
            parent.DropDownItems.Add(item);
        }

        private void SetStyle(StripStyle style)
        {
            _settings.Style = (int)style;
            SettingsStore.Save(_settings);
            Theme.ApplyStyle(style);

            foreach (StripForm strip in _strips.Values)
            {
                strip.BackColor = Theme.Surface;
                strip.Invalidate();
            }
        }

        private ToolStripMenuItem MonitorMenu()
        {
            var menu = new ToolStripMenuItem("停靠显示器");
            foreach (Screen screen in Screen.AllScreens)
            {
                Screen captured = screen;
                bool selected = string.Equals(
                    _settings.Monitor, screen.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(_settings.Monitor) && screen.Primary);
                string label = screen.DeviceName.Replace(@"\\.\", "") + "  "
                               + screen.Bounds.Width + "×" + screen.Bounds.Height
                               + (screen.Primary ? "（主屏）" : "");
                var item = new ToolStripMenuItem((selected ? "✓  " : "     ") + label);
                item.Click += (s, e) => SetMonitor(captured);
                menu.DropDownItems.Add(item);
            }
            return menu;
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "QuadDock";

        /// <summary>只写 HKCU 的 Run 项，用户点了才写，默认不动系统设置。</summary>
        private ToolStripMenuItem StartupItem()
        {
            bool enabled = StartupEnabled();
            var item = new ToolStripMenuItem((enabled ? "✓  " : "     ") + "开机自动启动");
            item.Click += (s, e) => SetStartup(!StartupEnabled());
            return item;
        }

        private static bool StartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key != null && key.GetValue(RunValue) != null;
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                return false;
            }
        }

        private static void SetStartup(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (enabled)
                    {
                        key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValue, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        // ---------- 操作 ----------

        private void ToggleEdge(Edge edge)
        {
            _settings.SetEnabled(edge, !_settings.IsEnabled(edge));
            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        private void ApplyPreset(bool vertical, int value)
        {
            if (vertical)
            {
                _settings.SetThickness(Edge.Left, value);
                _settings.SetThickness(Edge.Right, value);
            }
            else
            {
                _settings.SetThickness(Edge.Top, value);
                _settings.SetThickness(Edge.Bottom, value);
            }

            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        private void SetTheme(ThemeMode mode)
        {
            _settings.ThemeMode = (int)mode;
            SettingsStore.Save(_settings);
            Theme.Apply(mode);
            ApplyThemeToStrips();
        }

        /// <summary>系统在浅色/深色之间切换时，只有"跟随系统"才重读配色。</summary>
        private void OnSystemThemeChanged()
        {
            if (_settings.ThemeMode != (int)ThemeMode.System)
            {
                return;
            }

            Theme.Apply(ThemeMode.System);
            ApplyThemeToStrips();
        }

        private void ApplyThemeToStrips()
        {
            foreach (StripForm strip in _strips.Values)
            {
                strip.BackColor = Theme.Surface;
                strip.Invalidate();
            }
        }

        private void SetMonitor(Screen screen)
        {
            if (screen == null)
            {
                return;
            }

            _settings.Monitor = screen.DeviceName;
            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        internal void ToggleAll()
        {
            if (_quitting)
            {
                return;
            }

            _allHidden = !_allHidden;
            ApplyLayout();

            if (!_allHidden)
            {
                RefreshDesktopWindows();
            }
        }

        /// <summary>第二次启动程序时唤出侧栏，而不是把它切没了。</summary>
        internal void ShowAll()
        {
            if (_quitting)
            {
                return;
            }

            if (!_allHidden)
            {
                return;
            }

            _allHidden = false;
            ApplyLayout();
            RefreshDesktopWindows();
        }

        private void OnDisplayChanged()
        {
            if (_quitting)
            {
                return;
            }

            // ABM_SETPOS 会让外壳广播 SPI_SETWORKAREA（WM_SETTINGCHANGE）。
            // 关键：只要当前工作区仍等于"我们算出来的主区"，这次广播就是自己引起的，必须忽略，
            // 否则会形成 排布→广播→再排布 的自激循环（表现为最大化窗口反复被推动、整机变卡）。
            if (_dragging)
            {
                return;
            }

            if (_negotiated && !_allHidden)
            {
                Rectangle current = AvailableRect(_monitor);
                if (current == ExpectedContent())
                {
                    return;
                }
            }

            // 万一外壳给的区域怎么协商都对不上预期（比如位置被别人占了），别一直重排：
            // 每次重排都会再广播一次 WM_SETTINGCHANGE，不拦就是一个空转循环。
            if (unchecked(Environment.TickCount - _lastForcedLayout) < 600)
            {
                return;
            }
            _lastForcedLayout = Environment.TickCount;

            ApplyLayout(true);
        }

        /// <summary>按当前厚度推算主区矩形，用来判断工作区变化是不是我们自己造成的。</summary>
        private Rectangle ExpectedContent()
        {
            int dpi = _monitorDpi > 0 ? _monitorDpi : 96;

            var thickness = new Dictionary<Edge, int>();
            foreach (Edge edge in Order)
            {
                bool on = _settings.IsEnabled(edge) && !_allHidden;
                thickness[edge] = on ? Dpi.Scale(_settings.GetThickness(edge), dpi) : 0;
            }
            ClampThickness(thickness, _monitor);

            return Rectangle.FromLTRB(
                _available.Left + thickness[Edge.Left],
                _available.Top + thickness[Edge.Top],
                _available.Right - thickness[Edge.Right],
                _available.Bottom - thickness[Edge.Bottom]);
        }

        private Rectangle MonitorBounds()
        {
            if (!string.IsNullOrEmpty(_settings.Monitor))
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    if (string.Equals(screen.DeviceName, _settings.Monitor, StringComparison.OrdinalIgnoreCase))
                    {
                        return screen.Bounds;
                    }
                }
            }

            return Screen.PrimaryScreen.Bounds;
        }

        private void Quit()
        {
            if (_quitting)
            {
                return;
            }

            _quitting = true;
            _commitTimer.Stop();
            _scanTimer.Stop();
            _followTimer.Stop();
            _switchTimer.Stop();

            foreach (Edge edge in Order)
            {
                AppBarSlot bar;
                if (_bars.TryGetValue(edge, out bar))
                {
                    bar.Remove();
                }
            }

            foreach (StripForm strip in _strips.Values)
            {
                strip.Dispose();
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            _hotkey.Dispose();
            ExitThread();
        }

        private static Icon CreateTrayIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (var back = new SolidBrush(Color.FromArgb(24, 30, 40)))
                    using (GraphicsPath path = Painter.RoundedPath(new Rectangle(1, 1, 30, 30), 7))
                    {
                        g.FillPath(back, path);
                    }

                    using (var accent = new SolidBrush(Color.FromArgb(76, 141, 255)))
                    {
                        g.FillRectangle(accent, 6, 6, 20, 3);
                        g.FillRectangle(accent, 6, 23, 20, 3);
                        g.FillRectangle(accent, 6, 6, 3, 20);
                        g.FillRectangle(accent, 23, 6, 3, 20);
                    }

                    using (var core = new SolidBrush(Color.FromArgb(230, 238, 248)))
                    {
                        g.FillRectangle(core, 12, 12, 8, 8);
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIconInternal(handle);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon")]
        private static extern bool DestroyIconInternal(IntPtr handle);
    }
}
