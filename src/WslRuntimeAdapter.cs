using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace DeepSeekHarnessManager
{
    public sealed class WslRuntimeAdapter : IRuntimeAdapter
    {
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
                launchCommand = "npx --yes @deepseek-ai/dsh@" + (instance.PinnedVersion ?? String.Empty);
                version = instance.PinnedVersion ?? String.Empty;
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
            resolution.Arguments.Add("bash");
            resolution.Arguments.Add("-lic");
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
            inspection.Kind = inspection.HttpVerified
                ? (managerOwned ? InstanceStateKind.Running : InstanceStateKind.Starting)
                : InstanceStateKind.Stopped;
            inspection.Detail = managerOwned
                ? "WSL managed launch (HTTP markers verified; Runtime Bridge is authoritative when connected)"
                : "WSL HTTP markers only; Runtime Bridge authentication is required for adoption";
            return inspection;
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

        private static string BuildShellCommand(string launchCommand, string profile, int port, string patchPath)
        {
            StringBuilder command = new StringBuilder();
            command.Append("exec ");
            command.Append(launchCommand);
            command.Append(" --profile ");
            command.Append(BashQuote(profile));
            if (!String.IsNullOrWhiteSpace(patchPath))
            {
                command.Append(" --patch ");
                command.Append(BashQuote(patchPath));
            }
            command.Append(" --host 127.0.0.1 --port ");
            command.Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return command.ToString();
        }

        public static string ConvertToWslPath(string distro, string windowsPath)
        {
            if (String.IsNullOrWhiteSpace(windowsPath)) return "~";
            try
            {
                string runDirectory = Directory.Exists(windowsPath) ? windowsPath : Path.GetDirectoryName(windowsPath);
                if (String.IsNullOrWhiteSpace(runDirectory)) runDirectory = AppPaths.AppDirectory;
                CommandResult result = CommandRunner.RunCapture(FindWslExe(), new string[] { "-d", distro, "--", "wslpath", "-u", windowsPath.Replace('\\', '/') }, runDirectory, 5000);
                string converted = (result.StandardOutput ?? String.Empty).Trim();
                if (!String.IsNullOrWhiteSpace(converted)) return converted;
            }
            catch
            {
            }
            return windowsPath.Replace('\\', '/');
        }

        private static string ResolveGlobalVersion(string distro)
        {
            try
            {
                CommandResult result = CommandRunner.RunCapture(FindWslExe(),
                    new string[] { "-d", distro, "--", "bash", "-lic", "dsh --version" },
                    String.Empty, 10000);
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
            wslArgs.Add("bash");
            wslArgs.Add("-lic");
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
            try
            {
                TokenContext context = RuntimeResolver.CreateContext(instance, plugin, port, String.Empty);
                string url = AppPaths.Expand(plugin.Probe.UrlTemplate, context);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                request.Method = "GET";
                request.UserAgent = "DeepSeekHarnessManager/1.0";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    if ((int)response.StatusCode != 200) return false;
                    string content = reader.ReadToEnd();
                    foreach (string marker in plugin.Probe.Markers)
                        if (content.IndexOf(marker, StringComparison.Ordinal) < 0) return false;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string BashQuote(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "'\\''") + "'";
        }
    }
}
