using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeekHarnessManager
{
    public static class AppPaths
    {
        private static string appDirectoryOverride;
        private static string dataDirectoryOverride;

        public static string AppDirectory
        {
            get
            {
                if (!String.IsNullOrEmpty(appDirectoryOverride)) return appDirectoryOverride;
                return Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            }
        }

        public static string DataDirectory
        {
            get
            {
                if (!String.IsNullOrEmpty(dataDirectoryOverride)) return dataDirectoryOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarnessManager");
            }
        }

        public static string PluginDirectory { get { return Path.Combine(AppDirectory, "plugins"); } }
        public static string AssetDirectory { get { return Path.Combine(AppDirectory, "assets"); } }
        public static string LocaleDirectory { get { return Path.Combine(AppDirectory, "locales"); } }
        public static string ConfigFile { get { return Path.Combine(DataDirectory, "config.json"); } }
        public static string StateDirectory { get { return Path.Combine(DataDirectory, "state"); } }
        public static string RuntimeDirectory { get { return Path.Combine(DataDirectory, "runtime"); } }
        public static string LogDirectory { get { return Path.Combine(DataDirectory, "logs"); } }
        public static string UpdateDirectory { get { return Path.Combine(DataDirectory, "updates"); } }
        public static string ManagerLog { get { return Path.Combine(LogDirectory, "manager.log"); } }

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(StateDirectory);
            Directory.CreateDirectory(RuntimeDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(UpdateDirectory);
        }

        public static string StateFile(string instanceId)
        {
            return Path.Combine(StateDirectory, SafeFileName(instanceId) + ".json");
        }

        public static string InstanceRuntimeDirectory(string instanceId)
        {
            string path = Path.Combine(RuntimeDirectory, SafeFileName(instanceId));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string SafeFileName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "default";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = value.ToCharArray();
            int i;
            for (i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }

        public static string Expand(string value, TokenContext context)
        {
            if (value == null) return String.Empty;
            string result = Environment.ExpandEnvironmentVariables(value);
            if (context == null) return result;
            result = result.Replace("{appDir}", NullToEmpty(context.AppDirectory));
            result = result.Replace("{pluginDir}", NullToEmpty(context.PluginDirectory));
            result = result.Replace("{commandDir}", NullToEmpty(context.CommandDirectory));
            result = result.Replace("{sourceRoot}", NullToEmpty(context.SourceRoot));
            result = result.Replace("{workspace}", NullToEmpty(context.Workspace));
            result = result.Replace("{profile}", NullToEmpty(context.Profile));
            result = result.Replace("{pinnedVersion}", NullToEmpty(context.PinnedVersion));
            result = result.Replace("{patchPath}", NullToEmpty(context.PatchPath));
            result = result.Replace("{port}", context.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }

        public static string FindExecutable(IEnumerable<string> candidates, TokenContext context)
        {
            if (candidates == null) return null;
            foreach (string candidateValue in candidates)
            {
                string candidate = Expand(candidateValue, context).Trim();
                if (candidate.Length == 0) continue;
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    continue;
                }
                if (candidate.IndexOf(Path.DirectorySeparatorChar) >= 0 || candidate.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                {
                    string relative = Path.GetFullPath(Path.Combine(AppDirectory, candidate));
                    if (File.Exists(relative)) return relative;
                    continue;
                }
                string found = FindOnPath(candidate);
                if (!String.IsNullOrEmpty(found)) return found;
            }
            return null;
        }

        public static string FindOnPath(string fileName)
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            string pathExtValue = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            string[] extensions;
            if (Path.HasExtension(fileName)) extensions = new string[] { String.Empty };
            else extensions = pathExtValue.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string directoryValue in pathValue.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = directoryValue.Trim().Trim('"');
                if (directory.Length == 0) continue;
                foreach (string extension in extensions)
                {
                    try
                    {
                        string path = Path.Combine(directory, fileName + extension.ToLowerInvariant());
                        if (File.Exists(path)) return Path.GetFullPath(path);
                        path = Path.Combine(directory, fileName + extension.ToUpperInvariant());
                        if (File.Exists(path)) return Path.GetFullPath(path);
                    }
                    catch
                    {
                    }
                }
            }
            return null;
        }

        public static void SetTestOverrides(string appDirectory, string dataDirectory)
        {
            appDirectoryOverride = appDirectory;
            dataDirectoryOverride = dataDirectory;
        }

        private static string NullToEmpty(string value)
        {
            return value ?? String.Empty;
        }
    }
}
