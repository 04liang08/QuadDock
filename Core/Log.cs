using System;
using System.IO;

namespace QuadDock.Core
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "QuadDock");
            }
        }

        public static void Write(Exception ex)
        {
            if (ex != null)
            {
                Write(ex.ToString());
            }
        }

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.AppendAllText(
                        Path.Combine(DirectoryPath, "error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message
                        + Environment.NewLine + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
