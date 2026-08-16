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
            string wslWorkingDirectory = ConvertToWslPath(instance.Workspace);
            string wslPatchPath = String.IsNullOrWhiteSpace(patchPath) ? String.Empty : ConvertToWslPath(patchPath);
            string shell = BuildShellCommand(instance, plugin, port, wslPatchPath);

            RuntimeResolution resolution = new RuntimeResolution();
            resolution.Definition = new RuntimeDefinition
            {
                Id = "wsl",
                Label = "WSL " + distro,
                Kind = "wsl",
                WorkingDirectory = instance.Workspace
            };
            resolution.CommandPath = FindWslExe();
            resolution.WorkingDirectory = instance.Workspace;
            resolution.Arguments = new List<string>();
            resolution.Arguments.Add("-d");
            resolution.Arguments.Add(distro);
            resolution.Arguments.Add("--cd");
            resolution.Arguments.Add(wslWorkingDirectory);
            resolution.Arguments.Add("--");
            resolution.Arguments.Add("bash");
            resolution.Arguments.Add("-lic");
            resolution.Arguments.Add(shell);
            resolution.Version = ResolveVersion(distro);
            resolution.Description = "WSL " + distro + " (dsh via wsl.exe)";
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

        private static string BuildShellCommand(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            StringBuilder command = new StringBuilder();
            command.Append("exec dsh --profile ");
            command.Append(BashQuote(instance.Profile ?? "web"));
            command.Append(" --host 127.0.0.1 --port ");
            command.Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!String.IsNullOrWhiteSpace(patchPath))
            {
                command.Append(" --patch ");
                command.Append(BashQuote(patchPath));
            }
            return command.ToString();
        }

        private static string ConvertToWslPath(string windowsPath)
        {
            if (String.IsNullOrWhiteSpace(windowsPath)) return "~";
            try
            {
                CommandResult result = CommandRunner.RunCapture(FindWslExe(), new string[] { "wslpath", "-u", windowsPath }, windowsPath, 5000);
                string converted = (result.StandardOutput ?? String.Empty).Trim();
                if (!String.IsNullOrWhiteSpace(converted)) return converted;
            }
            catch
            {
            }
            return windowsPath.Replace('\\', '/');
        }

        private static string ResolveVersion(string distro)
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
