using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace DeepSeekHarnessManager
{
    public sealed class WslDistroState
    {
        public string Name { get; set; }
        public string State { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class WslRuntimeAdapter : IRuntimeAdapter
    {
        private static readonly Dictionary<string, string> ShellByDistro = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ShellSync = new object();
        private static readonly Dictionary<string, string> WslPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object PathCacheSync = new object();
        private const int MaxWslPathCacheEntries = 128;

        public string RuntimeType { get { return InstanceModel.RuntimeTypeWsl; } }

        public RuntimeResolution Resolve(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            string distro = GetDistro(instance);
            string kind = ResolveKind(instance);
            string workingDirectory = instance.Workspace;
            string launchCommand = "dsh";
            string version = ResolveGlobalVersion(distro);

            if (kind == "source")
            {
                if (String.IsNullOrWhiteSpace(instance.SourceRoot))
                    throw new InvalidOperationException("WSL source runtime requires SourceRoot.");
                workingDirectory = instance.SourceRoot;
                launchCommand = "pnpm dsh";
                version = ResolveSourceVersion(instance.SourceRoot);
            }
            else if (kind == "npx")
            {
                if (String.IsNullOrWhiteSpace(instance.PinnedVersion))
                    throw new InvalidOperationException("WSL npx runtime requires PinnedVersion.");
                launchCommand = "npx --yes @deepseek-ai/dsh@" + instance.PinnedVersion;
                version = instance.PinnedVersion;
            }
            else if (kind != "global")
            {
                throw new InvalidOperationException("Unsupported WSL runtime kind: " + kind);
            }

            string wslWorkingDirectory = ConvertToWslPath(distro, workingDirectory);
            string wslPatchPath = String.IsNullOrWhiteSpace(patchPath) ? String.Empty : ConvertToWslPath(distro, patchPath);
            string shell = BuildShellCommand(launchCommand, instance.Profile ?? "web", port, wslPatchPath);

            RuntimeResolution resolution = new RuntimeResolution();
            resolution.Definition = new RuntimeDefinition
            {
                Id = "wsl-" + kind,
                Label = "WSL " + distro + " " + kind,
                Kind = kind,
                WorkingDirectory = workingDirectory
            };
            resolution.CommandPath = FindWslExe();
            resolution.WorkingDirectory = workingDirectory;
            resolution.Arguments = new List<string>();
            resolution.Arguments.Add("-d");
            resolution.Arguments.Add(distro);
            resolution.Arguments.Add("--cd");
            resolution.Arguments.Add(wslWorkingDirectory);
            resolution.Arguments.Add("--");
            AddShellArguments(resolution.Arguments, distro);
            resolution.Arguments.Add(shell);
            resolution.Version = version;
            resolution.Description = "WSL " + distro + " (" + kind + " via wsl.exe)";
            resolution.EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return resolution;
        }

        public string ResolveInstalledVersion(InstanceConfig instance, PluginDefinition plugin)
        {
            try { return Resolve(instance, plugin, instance.PreferredPort, String.Empty).Version; }
            catch { return String.Empty; }
        }

        public IRuntimeProcess Start(RuntimeResolution runtime, string outputLog, string errorLog)
        {
            return CommandRunner.StartService(runtime, outputLog, errorLog);
        }

        public ProcessIdentity CaptureIdentity(IRuntimeProcess process)
        {
            if (process == null || process.ProcessId <= 0) return null;
            ProcessIdentity identity = ProcessInspector.Get(process.ProcessId, false);
            if (identity != null) identity.Name = "wsl.exe (" + identity.Name + ")";
            return identity;
        }

        public void Kill(IRuntimeProcess process)
        {
            ManagedProcess managed = process as ManagedProcess;
            if (managed != null && managed.RootProcess != null)
            {
                CommandRunner.KillProcessTree(managed.RootProcess);
                return;
            }
            try
            {
                if (process != null && process.ProcessId > 0)
                {
                    using (System.Diagnostics.Process native = System.Diagnostics.Process.GetProcessById(process.ProcessId))
                        CommandRunner.KillProcessTree(native);
                }
            }
            catch
            {
            }
        }

        public PortInspection InspectPort(InstanceConfig instance, PluginDefinition plugin, int port, bool managerOwned)
        {
            PortInspection inspection = new PortInspection();
            inspection.Port = port;
            inspection.ProcessId = 0;
            inspection.HttpVerified = VerifyHttp(instance, plugin, port);
            inspection.ProcessVerified = managerOwned && inspection.HttpVerified;
            inspection.BridgeVerified = false;
            if (inspection.HttpVerified)
            {
                inspection.Kind = InstanceStateKind.Running;
                inspection.Detail = managerOwned
                    ? "WSL managed launch (HTTP markers verified; Runtime Bridge is authoritative when connected)"
                    : "WSL attached DSH (HTTP markers verified; no Runtime Bridge token, lifecycle is limited)";
                return inspection;
            }
            int windowsOwner = PortMap.GetPreferredListenerProcessId(port);
            if (windowsOwner > 0)
            {
                inspection.ProcessId = windowsOwner;
                inspection.Process = ProcessInspector.Get(windowsOwner, true);
                inspection.Kind = InstanceStateKind.Conflict;
                inspection.Detail = "The Windows-side loopback port is occupied.";
                return inspection;
            }
            inspection.Kind = InstanceStateKind.Stopped;
            inspection.Detail = "No WSL DSH HTTP listener was detected.";
            return inspection;
        }

        public static List<string> DetectDistros()
        {
            List<string> distros = new List<string>();
            try
            {
                string wsl = FindWslExe();
                if (!File.Exists(wsl)) return distros;
                using (System.Diagnostics.Process process = new System.Diagnostics.Process())
                {
                    process.StartInfo.FileName = wsl;
                    process.StartInfo.Arguments = "--list --quiet";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.Unicode;
                    if (!process.Start()) return distros;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0) return distros;
                    foreach (string value in output.Replace("\0", String.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string distro = value.Trim();
                        if (distro.Length > 0 && !distros.Contains(distro)) distros.Add(distro);
                    }
                }
            }
            catch
            {
            }
            return distros;
        }

        public static List<WslDistroState> DetectDistroStates()
        {
            List<WslDistroState> states = new List<WslDistroState>();
            try
            {
                string wsl = FindWslExe();
                if (!File.Exists(wsl)) return states;
                using (System.Diagnostics.Process process = new System.Diagnostics.Process())
                {
                    process.StartInfo.FileName = wsl;
                    process.StartInfo.Arguments = "--list --verbose";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.Unicode;
                    if (!process.Start()) return states;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0) return states;
                    foreach (string value in output.Replace("\0", String.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = value.Trim();
                        if (line.Length == 0) continue;
                        bool isDefault = false;
                        if (line[0] == '*')
                        {
                            isDefault = true;
                            line = line.Substring(1).Trim();
                        }
                        string[] columns = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (columns.Length < 3) continue;
                        bool header = (String.Equals(columns[0], "NAME", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[0], "名称", StringComparison.OrdinalIgnoreCase)) &&
                            (String.Equals(columns[columns.Length - 2], "STATE", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[columns.Length - 2], "状态", StringComparison.OrdinalIgnoreCase)) &&
                            (String.Equals(columns[columns.Length - 1], "VERSION", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[columns.Length - 1], "版本", StringComparison.OrdinalIgnoreCase));
                        if (header) continue;
                        WslDistroState state = new WslDistroState();
                        state.Name = String.Join(" ", columns, 0, columns.Length - 2);
                        state.State = columns[columns.Length - 2];
                        state.IsDefault = isDefault;
                        states.Add(state);
                    }
                }
            }
            catch
            {
            }
            return states;
        }

        /// <summary>
        /// Docker Desktop and similar products register helper distros that are
        /// not general-purpose Linux environments. DSH cannot be launched there.
        /// </summary>
        public static bool IsUserWslDistro(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return false;
            string name = distro.Trim().ToLowerInvariant();
            if (name == "docker-desktop" || name == "docker-desktop-data") return false;
            if (name.StartsWith("docker-desktop-", StringComparison.Ordinal)) return false;
            if (name == "rancher-desktop" || name == "rancher-desktop-data") return false;
            if (name.StartsWith("rancher-desktop-", StringComparison.Ordinal)) return false;
            if (name.StartsWith("podman-machine-", StringComparison.Ordinal)) return false;
            return true;
        }

        public static int ScoreUserWslDistro(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return 0;
            string name = distro.Trim().ToLowerInvariant();
            if (name.StartsWith("ubuntu", StringComparison.Ordinal)) return 100;
            if (name == "debian" || name.StartsWith("debian-", StringComparison.Ordinal)) return 90;
            if (name.StartsWith("kali", StringComparison.Ordinal)) return 85;
            if (name.IndexOf("suse", StringComparison.Ordinal) >= 0) return 80;
            if (name.IndexOf("fedora", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("rocky", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("alma", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("centos", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("rhel", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("oracle", StringComparison.Ordinal) >= 0) return 75;
            if (name.StartsWith("arch", StringComparison.Ordinal) ||
                name.IndexOf("manjaro", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("endeavouros", StringComparison.Ordinal) >= 0) return 70;
            if (name.IndexOf("alpine", StringComparison.Ordinal) >= 0) return 60;
            return 0;
        }

        public static string SelectPreferredDistro(string configured, List<string> detected, List<WslDistroState> states)
        {
            if (detected == null || detected.Count == 0) return null;

            if (!String.IsNullOrWhiteSpace(configured))
            {
                string configuredName = configured.Trim();
                foreach (string item in detected)
                {
                    if (String.Equals(item, configuredName, StringComparison.OrdinalIgnoreCase)) return item;
                }
            }

            List<string> candidates = new List<string>();
            foreach (string item in detected)
            {
                if (IsUserWslDistro(item) && !ContainsDistro(candidates, item)) candidates.Add(item);
            }
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            if (states != null)
            {
                foreach (WslDistroState state in states)
                {
                    if (state == null || !state.IsDefault || String.IsNullOrWhiteSpace(state.Name)) continue;
                    string match = FindDistro(candidates, state.Name);
                    if (match != null) return match;
                }

                List<string> running = new List<string>();
                foreach (WslDistroState state in states)
                {
                    if (state == null || String.IsNullOrWhiteSpace(state.Name)) continue;
                    if (!String.Equals(state.State, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                    string match = FindDistro(candidates, state.Name);
                    if (match != null && !ContainsDistro(running, match)) running.Add(match);
                }
                if (running.Count == 1) return running[0];
            }

            string best = null;
            int bestScore = -1;
            bool tie = false;
            foreach (string candidate in candidates)
            {
                int score = ScoreUserWslDistro(candidate);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                    tie = false;
                }
                else if (score == bestScore)
                {
                    tie = true;
                }
            }
            if (best != null && !tie) return best;
            return null;
        }

        public static string SelectPreferredDistro(string configured)
        {
            return SelectPreferredDistro(configured, DetectDistros(), DetectDistroStates());
        }

        public static List<string> GetUserWslDistros(List<string> detected)
        {
            List<string> candidates = new List<string>();
            if (detected == null) return candidates;
            foreach (string item in detected)
            {
                if (IsUserWslDistro(item) && !ContainsDistro(candidates, item)) candidates.Add(item);
            }
            return candidates;
        }

        private static bool ContainsDistro(List<string> distros, string distro)
        {
            foreach (string item in distros)
            {
                if (String.Equals(item, distro, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FindDistro(List<string> distros, string distro)
        {
            foreach (string item in distros)
            {
                if (String.Equals(item, distro, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        public string GetDistro(InstanceConfig instance)
        {
            string distro = instance == null ? String.Empty : (instance.WslDistro ?? String.Empty);
            if (String.IsNullOrWhiteSpace(distro)) throw new InvalidOperationException("No WSL distro is configured for this instance.");
            return distro.Trim();
        }

        private static string FindWslExe()
        {
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string path = Path.Combine(system32, "wsl.exe");
            if (File.Exists(path)) return path;
            return "wsl.exe";
        }

        internal static string BuildShellCommand(string launchCommand, string profile, int port, string patchPath)
        {
            StringBuilder command = new StringBuilder();
            command.Append("exec ");
            command.Append(launchCommand);
            // DSH 0.1.1 parses launcher-owned flags only before the profile
            // alias; everything after --profile web belongs to the web app.
            if (!String.IsNullOrWhiteSpace(patchPath))
            {
                command.Append(" --patch ");
                command.Append(BashQuote(patchPath));
            }
            command.Append(" --profile ");
            command.Append(BashQuote(profile));
            command.Append(" --no-open --host 127.0.0.1 --port ");
            command.Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return command.ToString();
        }

        public static string ResolveLinuxHome(string distro)
        {
            try
            {
                List<string> homeArgs = new List<string>();
                homeArgs.Add("-d"); homeArgs.Add(distro); homeArgs.Add("--");
                AddShellArguments(homeArgs, distro);
                homeArgs.Add("printf %s \"$HOME\"");
                CommandResult result = CommandRunner.RunCapture(FindWslExe(), homeArgs, AppPaths.AppDirectory, 5000);
                if (result.ExitCode == 0 && !String.IsNullOrWhiteSpace(result.StandardOutput))
                    return result.StandardOutput.Trim();
            }
            catch
            {
            }
            return "~";
        }

        public static string ConvertToWindowsPath(string distro, string linuxPath)
        {
            if (String.IsNullOrWhiteSpace(linuxPath)) return String.Empty;
            string cacheKey = "w:" + distro + "\n" + linuxPath;
            string cached = GetCachedWslPath(cacheKey);
            if (cached != null) return cached;
            string converted;
            try
            {
                CommandResult result = CommandRunner.RunCapture(FindWslExe(),
                    new string[] { "-d", distro, "--", "wslpath", "-w", linuxPath },
                    AppPaths.AppDirectory, 5000);
                if (result.ExitCode == 0 && !String.IsNullOrWhiteSpace(result.StandardOutput))
                    converted = result.StandardOutput.Trim();
                else
                    converted = "\\\\wsl.localhost\\" + distro + linuxPath.Replace('/', '\\');
            }
            catch
            {
                converted = "\\\\wsl.localhost\\" + distro + linuxPath.Replace('/', '\\');
            }
            PutCachedWslPath(cacheKey, converted);
            return converted;
        }

        public static string ResolveShell(string distro)
        {
            lock (ShellSync)
            {
                string cached;
                if (ShellByDistro.TryGetValue(distro, out cached) && !String.IsNullOrWhiteSpace(cached)) return cached;
            }
            string shell = "sh";
            try
            {
                CommandResult result = CommandRunner.RunCapture(FindWslExe(),
                    new string[] { "-d", distro, "--", "sh", "-lc", "command -v bash || echo sh" },
                    AppPaths.AppDirectory, 5000);
                if (result.ExitCode == 0 && !String.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    string value = result.StandardOutput.Trim();
                    int newline = value.IndexOfAny(new char[] { '\r', '\n' });
                    if (newline >= 0) value = value.Substring(0, newline).Trim();
                    if (value.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0) shell = "bash";
                }
            }
            catch
            {
            }
            lock (ShellSync) ShellByDistro[distro] = shell;
            return shell;
        }

        private static void AddShellArguments(List<string> arguments, string distro)
        {
            string shell = ResolveShell(distro);
            arguments.Add(shell);
            arguments.Add(shell == "bash" ? "-lic" : "-lc");
        }

        public static string ConvertToWslPath(string distro, string windowsPath)
        {
            if (String.IsNullOrWhiteSpace(windowsPath)) return "~";
            string cacheKey = "u:" + distro + "\n" + windowsPath;
            string cached = GetCachedWslPath(cacheKey);
            if (cached != null) return cached;
            string converted = null;
            try
            {
                string runDirectory = Directory.Exists(windowsPath) ? windowsPath : Path.GetDirectoryName(windowsPath);
                if (String.IsNullOrWhiteSpace(runDirectory)) runDirectory = AppPaths.AppDirectory;
                CommandResult result = CommandRunner.RunCapture(FindWslExe(), new string[] { "-d", distro, "--", "wslpath", "-u", windowsPath.Replace('\\', '/') }, runDirectory, 5000);
                string value = (result.StandardOutput ?? String.Empty).Trim();
                if (result.ExitCode == 0 && !String.IsNullOrWhiteSpace(value)) converted = value;
            }
            catch
            {
            }
            if (String.IsNullOrWhiteSpace(converted)) converted = ConvertWindowsToWslFallback(distro, windowsPath);
            PutCachedWslPath(cacheKey, converted);
            return converted;
        }

        private static string GetCachedWslPath(string key)
        {
            lock (PathCacheSync)
            {
                string value;
                return WslPathCache.TryGetValue(key, out value) ? value : null;
            }
        }

        private static void PutCachedWslPath(string key, string value)
        {
            lock (PathCacheSync)
            {
                if (WslPathCache.Count >= MaxWslPathCacheEntries) WslPathCache.Clear();
                WslPathCache[key] = value;
            }
        }

        private static string ConvertWindowsToWslFallback(string distro, string windowsPath)
        {
            try
            {
                string normalized = (windowsPath ?? String.Empty).Replace('\\', '/');
                string mountRoot = "/mnt/" + Char.ToLowerInvariant(normalized[0]);
                if (normalized.Length > 1 && normalized[1] == ':') normalized = normalized.Substring(2).TrimStart('/');
                return mountRoot + "/" + normalized;
            }
            catch
            {
                return (windowsPath ?? String.Empty).Replace('\\', '/');
            }
        }

        private static string ResolveGlobalVersion(string distro)
        {
            try
            {
                List<string> versionArgs = new List<string>();
                versionArgs.Add("-d"); versionArgs.Add(distro); versionArgs.Add("--");
                AddShellArguments(versionArgs, distro);
                versionArgs.Add("dsh --version");
                CommandResult result = CommandRunner.RunCapture(FindWslExe(), versionArgs, AppPaths.AppDirectory, 10000);
                if (result.ExitCode == 0)
                {
                    string version = (result.StandardOutput ?? String.Empty).Trim();
                    int newline = version.IndexOfAny(new char[] { '\r', '\n' });
                    if (newline >= 0) version = version.Substring(0, newline).Trim();
                    return version;
                }
            }
            catch
            {
            }
            return String.Empty;
        }

        public List<WslRunningInstance> DetectRunning(string distro)
        {
            List<WslRunningInstance> result = new List<WslRunningInstance>();
            if (String.IsNullOrWhiteSpace(distro)) return result;
            string scriptPath = Path.Combine(AppPaths.RuntimeDirectory, "wsl-detect-dsh.cjs");
            File.WriteAllText(scriptPath, WslDetectScript, new UTF8Encoding(false));
            string wslScriptPath = ConvertToWslPath(distro, scriptPath);
            CommandResult command = RunCommand(distro, "node", new string[] { wslScriptPath }, Path.GetDirectoryName(scriptPath), 15000);
            if (command.ExitCode != 0) return result;
            string[] lines = (command.StandardOutput ?? String.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                try
                {
                    Dictionary<string, object> item = JsonStore.Deserialize<Dictionary<string, object>>(line);
                    if (item == null) continue;
                    int pid = BridgeProtocol.GetInt(item, "pid");
                    int port = BridgeProtocol.GetInt(item, "port");
                    if (pid <= 0 || port <= 0) continue;
                    WslRunningInstance running = new WslRunningInstance();
                    running.Distro = distro.Trim();
                    running.Pid = pid;
                    running.Port = port;
                    running.CommandLine = BridgeProtocol.GetString(item, "command");
                    result.Add(running);
                }
                catch
                {
                }
            }
            return result;
        }

        public bool TerminateLinuxProcess(string distro, int pid, out string error)
        {
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(distro) || pid <= 0)
            {
                error = "Invalid WSL process identity.";
                return false;
            }
            CommandResult terminate = RunCommand(distro, "kill", new string[] { "-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, AppPaths.AppDirectory, 5000);
            if (terminate.ExitCode == 0)
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    System.Threading.Thread.Sleep(500);
                    CommandResult check = RunCommand(distro, "ps", new string[] { "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "pid=" }, AppPaths.AppDirectory, 5000);
                    if (check.ExitCode != 0 || String.IsNullOrWhiteSpace(check.StandardOutput)) return true;
                }
            }
            CommandResult force = RunCommand(distro, "kill", new string[] { "-KILL", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, AppPaths.AppDirectory, 5000);
            if (force.ExitCode == 0)
            {
                error = String.Empty;
                return true;
            }
            error = force.StandardError ?? "The WSL process could not be terminated.";
            return false;
        }

        public CommandResult RunCommand(InstanceConfig instance, string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds)
        {
            string distro = GetDistro(instance);
            return RunCommand(distro, command, arguments, workingDirectory, timeoutMilliseconds);
        }

        public CommandResult RunCommand(string distro, string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds)
        {
            StringBuilder shell = new StringBuilder();
            shell.Append(BashQuote(command));
            if (arguments != null)
            {
                foreach (string argument in arguments) shell.Append(' ').Append(BashQuote(argument));
            }
            List<string> wslArgs = new List<string>();
            wslArgs.Add("-d");
            wslArgs.Add(distro);
            wslArgs.Add("--cd");
            wslArgs.Add(ConvertToWslPath(distro, workingDirectory));
            wslArgs.Add("--");
            AddShellArguments(wslArgs, distro);
            wslArgs.Add(shell.ToString());
            return CommandRunner.RunCapture(FindWslExe(), wslArgs, workingDirectory, timeoutMilliseconds);
        }

        private static string ResolveKind(InstanceConfig instance)
        {
            string kind = String.IsNullOrWhiteSpace(instance.Runtime) ? "auto" : instance.Runtime.Trim().ToLowerInvariant();
            if (kind == "auto") kind = "global";
            return kind;
        }

        private static string ResolveSourceVersion(string sourceRoot)
        {
            try
            {
                string packagePath = Path.Combine(sourceRoot, "package.json");
                if (!File.Exists(packagePath)) return String.Empty;
                Dictionary<string, object> package = JsonStore.Deserialize<Dictionary<string, object>>(File.ReadAllText(packagePath, Encoding.UTF8));
                object version;
                if (package != null && package.TryGetValue("version", out version)) return Convert.ToString(version);
            }
            catch
            {
            }
            return String.Empty;
        }

        private static bool VerifyHttp(InstanceConfig instance, PluginDefinition plugin, int port)
        {
            return RuntimeHttpProbe.Verify(instance, plugin, port, 1500);
        }

        private static string BashQuote(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "'\\''") + "'";
        }

        private const string WslDetectScript = @"const fs = require('fs');
function parsePort(hex) { return parseInt(hex, 16); }
function scanTable(file, listeners) {
  let text = '';
  try { text = fs.readFileSync(file, 'utf8'); } catch (_) { return; }
  const lines = text.split(/\r?\n/);
  for (let i = 1; i < lines.length; i += 1) {
    const columns = lines[i].trim().split(/\s+/);
    if (columns.length < 10) continue;
    const local = columns[1] || '';
    const state = columns[3] || '';
    const inode = columns[9] || '';
    if (state !== '0A') continue;
    const portHex = local.split(':').pop() || '0';
    const port = parsePort(portHex);
    if (port > 0 && port <= 65535) listeners[inode] = port;
  }
}
function socketInodes(pid) {
  const found = new Set();
  let fdDir;
  try { fdDir = fs.readdirSync('/proc/' + pid + '/fd'); } catch (_) { return found; }
  for (const fd of fdDir) {
    try {
      const target = fs.readlinkSync('/proc/' + pid + '/fd/' + fd);
      const match = /^socket:\[(\d+)\]$/.exec(target);
      if (match) found.add(match[1]);
    } catch (_) { }
  }
  return found;
}
const listeners = {};
scanTable('/proc/net/tcp', listeners);
scanTable('/proc/net/tcp6', listeners);
const pids = fs.readdirSync('/proc').filter((value) => /^\d+$/.test(value));
for (const pid of pids) {
  let command = '';
  try { command = fs.readFileSync('/proc/' + pid + '/cmdline', 'utf8').replace(/\0/g, ' ').trim(); } catch (_) { continue; }
  if (!/dsh/.test(command)) continue;
  const inodes = socketInodes(pid);
  for (const inode of inodes) {
    if (Object.prototype.hasOwnProperty.call(listeners, inode)) {
      process.stdout.write(JSON.stringify({ pid: Number(pid), port: listeners[inode], command }) + '\n');
    }
  }
}
";
    }
}
