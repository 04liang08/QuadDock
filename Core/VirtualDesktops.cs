using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuadDock.Core
{
    /// <summary>
    /// 虚拟桌面切换/新建/枚举没有公开 API，这里直接调用 Explorer 的 ImmersiveShell 接口。
    /// 接口定义参考 MScholtes/VirtualDesktop v1.21（MIT），对应 Windows 11 24H2/25H2（26100/26200）。
    /// 系统更新改了接口时 TryInitialize 会失败，功能整体禁用，不影响侧栏本身。
    /// </summary>
    internal sealed class VirtualDesktops
    {
        private static readonly Guid ClsidImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        private static readonly Guid ClsidVirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        private static readonly Guid ClsidVirtualDesktopManager = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");

        private readonly object _gate = new object();
        private IVirtualDesktopManagerInternal _internal;
        private IVirtualDesktopManager _manager;
        private bool _failed;

        public bool Available
        {
            get { return _internal != null && _manager != null; }
        }

        public bool TryInitialize()
        {
            lock (_gate)
            {
                if (Available)
                {
                    return true;
                }
                if (_failed)
                {
                    return false;
                }

                try
                {
                    var shell = (IServiceProvider10)Activator.CreateInstance(
                        Type.GetTypeFromCLSID(ClsidImmersiveShell));
                    _internal = (IVirtualDesktopManagerInternal)shell.QueryService(
                        ClsidVirtualDesktopManagerInternal,
                        typeof(IVirtualDesktopManagerInternal).GUID);
                    _manager = (IVirtualDesktopManager)Activator.CreateInstance(
                        Type.GetTypeFromCLSID(ClsidVirtualDesktopManager));
                    return Available;
                }
                catch (Exception ex)
                {
                    _internal = null;
                    _manager = null;
                    _failed = true;
                    Log.Write(ex);
                    return false;
                }
            }
        }

        public List<Guid> GetDesktopIds()
        {
            var ids = new List<Guid>();
            lock (_gate)
            {
                if (_internal == null)
                {
                    return ids;
                }

                IObjectArray array = null;
                try
                {
                    _internal.GetDesktops(out array);
                    int count;
                    array.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object item = null;
                        try
                        {
                            Guid iid = typeof(IVirtualDesktop).GUID;
                            array.GetAt(i, ref iid, out item);
                            var desktop = item as IVirtualDesktop;
                            if (desktop != null)
                            {
                                ids.Add(desktop.GetId());
                            }
                        }
                        finally
                        {
                            if (item != null)
                            {
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                }
                finally
                {
                    if (array != null)
                    {
                        Marshal.ReleaseComObject(array);
                    }
                }
            }
            return ids;
        }

        public Guid GetCurrentDesktopId()
        {
            lock (_gate)
            {
                if (_internal == null)
                {
                    return Guid.Empty;
                }

                try
                {
                    return _internal.GetCurrentDesktop().GetId();
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    return Guid.Empty;
                }
            }
        }

        /// <summary>保证至少有 count 个虚拟桌面，不够就新建。</summary>
        public bool EnsureCount(int count)
        {
            lock (_gate)
            {
                if (_internal == null)
                {
                    return false;
                }

                try
                {
                    int have = _internal.GetCount();
                    int guard = 0;
                    while (have < count && guard++ < 12)
                    {
                        _internal.CreateDesktop();
                        int next = _internal.GetCount();
                        if (next <= have)
                        {
                            break;
                        }

                        have = next;
                    }

                    return have >= count;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    return false;
                }
            }
        }

        /// <summary>按桌面 GUID 切换。GUID 比序号稳，桌面被重排也不会切错。</summary>
        public bool SwitchToDesktop(Guid id)
        {
            if (id == Guid.Empty)
            {
                return false;
            }

            lock (_gate)
            {
                if (_internal == null)
                {
                    return false;
                }

                try
                {
                    List<Guid> ids = GetDesktopIds();
                    int index = ids.IndexOf(id);
                    if (index < 0)
                    {
                        return false;
                    }

                    IVirtualDesktop target = GetDesktopAt(index);
                    if (target == null)
                    {
                        return false;
                    }

                    if (target.GetId() == _internal.GetCurrentDesktop().GetId())
                    {
                        return true;
                    }

                    _internal.SwitchDesktop(target);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    return false;
                }
            }
        }

        public bool TryGetWindowDesktopId(IntPtr hwnd, out Guid id)
        {
            id = Guid.Empty;
            lock (_gate)
            {
                if (_manager == null)
                {
                    return false;
                }

                try
                {
                    id = _manager.GetWindowDesktopId(hwnd);
                    return id != Guid.Empty;
                }
                catch
                {
                    // 提权窗口等拿不到桌面 ID，跳过即可
                    return false;
                }
            }
        }

        public bool IsWindowOnCurrentDesktop(IntPtr hwnd)
        {
            lock (_gate)
            {
                if (_manager == null)
                {
                    return true;
                }

                try
                {
                    return _manager.IsWindowOnCurrentVirtualDesktop(hwnd);
                }
                catch
                {
                    return true;
                }
            }
        }

        public bool MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
        {
            lock (_gate)
            {
                if (_manager == null)
                {
                    return false;
                }

                try
                {
                    _manager.MoveWindowToDesktop(hwnd, ref desktopId);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    return false;
                }
            }
        }

        private IVirtualDesktop GetDesktopAt(int index)
        {
            IObjectArray array = null;
            try
            {
                _internal.GetDesktops(out array);
                int count;
                array.GetCount(out count);
                if (index < 0 || index >= count)
                {
                    return null;
                }

                object item;
                Guid iid = typeof(IVirtualDesktop).GUID;
                array.GetAt(index, ref iid, out item);
                return item as IVirtualDesktop;
            }
            finally
            {
                if (array != null)
                {
                    Marshal.ReleaseComObject(array);
                }
            }
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        private interface IServiceProvider10
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
        private interface IVirtualDesktop
        {
            bool IsViewVisible(IntPtr view);
            Guid GetId();
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
        private interface IVirtualDesktopManagerInternal
        {
            int GetCount();
            void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
            bool CanViewMoveDesktops(IntPtr view);
            IVirtualDesktop GetCurrentDesktop();
            void GetDesktops(out IObjectArray desktops);
            [PreserveSig]
            int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
            void SwitchDesktop(IVirtualDesktop desktop);
            void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
            IVirtualDesktop CreateDesktop();
            void MoveDesktop(IVirtualDesktop desktop, int nIndex);
            void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
            IVirtualDesktop FindDesktop(ref Guid desktopid);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
        private interface IVirtualDesktopManager
        {
            bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
            Guid GetWindowDesktopId(IntPtr topLevelWindow);
            void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
        private interface IObjectArray
        {
            void GetCount(out int count);
            void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        }
    }
}
