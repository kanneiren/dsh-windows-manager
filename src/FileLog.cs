using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeepSeekHarnessManager
{
    public static class FileLog
    {
        private static readonly object SyncRoot = new object();

        public const int InstanceLogRetentionDays = 14;
        public const int MaxInstanceLogPairs = 20;
        public const long ManagerLogRolloverBytes = 1024 * 1024;

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        public static void Error(Exception exception)
        {
            Write("ERROR", exception == null ? "Unknown exception" : exception.ToString());
        }

        /// <summary>
        /// Bounded log retention policy, run once at manager startup:
        /// rolls manager.log over at 1 MB (keeping one archive) and keeps
        /// at most 20 pairs of instance out/err logs newer than 14 days.
        /// </summary>
        public static void EnforceRetention()
        {
            try
            {
                RolloverManagerLog();
                CleanInstanceLogs();
            }
            catch
            {
            }
        }

        private static void RolloverManagerLog()
        {
            string path = AppPaths.ManagerLog;
            if (!File.Exists(path)) return;
            long length;
            try { length = new FileInfo(path).Length; }
            catch { return; }
            if (length < ManagerLogRolloverBytes) return;
            string archive = path + ".1";
            TryDelete(archive);
            try { File.Move(path, archive); } catch { }
        }

        private static void CleanInstanceLogs()
        {
            string[] files;
            try
            {
                HashSet<string> collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string match in Directory.GetFiles(AppPaths.LogDirectory, "*.out.log", SearchOption.AllDirectories)) collected.Add(match);
                foreach (string match in Directory.GetFiles(AppPaths.LogDirectory, "*.err.log", SearchOption.AllDirectories)) collected.Add(match);
                files = new string[collected.Count];
                collected.CopyTo(files);
            }
            catch { return; }
            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-InstanceLogRetentionDays);
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DateTime> newestByBase = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                int dot = name.IndexOf('.');
                if (dot <= 0) continue;
                string pairBase = name.Substring(0, dot);
                DateTime writeUtc;
                try { writeUtc = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                List<string> members;
                if (!groups.TryGetValue(pairBase, out members))
                {
                    members = new List<string>();
                    groups[pairBase] = members;
                }
                members.Add(file);
                DateTime current;
                if (!newestByBase.TryGetValue(pairBase, out current) || writeUtc > current) newestByBase[pairBase] = writeUtc;
            }
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, List<string>> group in groups)
            {
                DateTime newest;
                if (!newestByBase.TryGetValue(group.Key, out newest)) continue;
                if (newest < cutoffUtc) expired.Add(group.Key);
            }
            foreach (string pairBase in expired)
            {
                List<string> members;
                if (groups.TryGetValue(pairBase, out members))
                {
                    foreach (string file in members) TryDelete(file);
                    groups.Remove(pairBase);
                }
            }
            if (groups.Count <= MaxInstanceLogPairs) return;
            List<KeyValuePair<string, DateTime>> pairs = new List<KeyValuePair<string, DateTime>>(newestByBase);
            pairs.Sort(delegate(KeyValuePair<string, DateTime> a, KeyValuePair<string, DateTime> b) { return a.Value.CompareTo(b.Value); });
            int remove = pairs.Count - MaxInstanceLogPairs;
            for (int i = 0; i < remove; i++)
            {
                List<string> members;
                if (groups.TryGetValue(pairs[i].Key, out members))
                {
                    foreach (string file in members) TryDelete(file);
                    groups.Remove(pairs[i].Key);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
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
