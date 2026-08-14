using System;
using System.IO;
using System.Text;

namespace DeepSeekHarnessManager
{
    public static class FileLog
    {
        private static readonly object SyncRoot = new object();

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        public static void Error(Exception exception)
        {
            Write("ERROR", exception == null ? "Unknown exception" : exception.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                string singleLine = (message ?? String.Empty).Replace("\r", " ").Replace("\n", " ");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + singleLine + Environment.NewLine;
                lock (SyncRoot)
                {
                    AppPaths.EnsureDirectories();
                    File.AppendAllText(AppPaths.ManagerLog, line, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }
}
