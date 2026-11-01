using System;
using System.Threading;
using System.Windows.Forms;
using QuadDock.Core;

namespace QuadDock
{
    internal static class Program
    {
        private const string MutexName = "QuadDock.SingleInstance";
        private const string WakeEventName = "QuadDock.Wake";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExisting();
                    return;
                }

                using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName))
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += (s, e) => Log.Write(e.Exception);
                    AppDomain.CurrentDomain.UnhandledException +=
                        (s, e) => Log.Write(e.ExceptionObject as Exception);

                    var manager = new DockManager();

                    var waiter = new Thread(() => WaitForWake(manager, wake))
                    {
                        IsBackground = true,
                        Name = "QuadDock.WakeWaiter"
                    };
                    waiter.Start();

                    try
                    {
                        Application.Run(manager);
                    }
                    catch (Exception ex)
                    {
                        Log.Write(ex);
                    }
                    finally
                    {
                        wake.Set();
                    }
                }
            }
        }

        /// <summary>再次启动时把已有实例的侧栏切出来，而不是开第二个进程。</summary>
        private static void WaitForWake(DockManager manager, EventWaitHandle wake)
        {
            while (true)
            {
                try
                {
                    wake.WaitOne();
                }
                catch
                {
                    return;
                }

                Form main = manager.MainForm;
                if (main == null || main.IsDisposed)
                {
                    return;
                }

                try
                {
                    main.BeginInvoke(new Action(manager.ShowAll));
                }
                catch
                {
                    return;
                }
            }
        }

        private static void SignalExisting()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    using (EventWaitHandle wake = EventWaitHandle.OpenExisting(WakeEventName))
                    {
                        wake.Set();
                        return;
                    }
                }
                catch
                {
                    Thread.Sleep(150);
                }
            }
        }
    }
}
