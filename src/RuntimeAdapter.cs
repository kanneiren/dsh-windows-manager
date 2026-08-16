using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessManager
{
    /// <summary>
    /// Runtime process abstraction. Windows adapters expose a native Process;
    /// future WSL adapters can expose a wsl.exe-launched process or another
    /// verified handle without changing InstanceController.
    /// </summary>
    public interface IRuntimeProcess : IDisposable
    {
        int ProcessId { get; }
        bool HasExited { get; }
        event EventHandler Exited;
        bool WaitForExit(int timeoutMilliseconds);
    }

    public interface IRuntimeAdapter
    {
        string RuntimeType { get; }
        RuntimeResolution Resolve(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath);
        string ResolveInstalledVersion(InstanceConfig instance, PluginDefinition plugin);
        IRuntimeProcess Start(RuntimeResolution runtime, string outputLog, string errorLog);
        ProcessIdentity CaptureIdentity(IRuntimeProcess process);
        void Kill(IRuntimeProcess process);
        CommandResult RunCommand(InstanceConfig instance, string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds);
    }

    public sealed class WindowsRuntimeAdapter : IRuntimeAdapter
    {
        public string RuntimeType { get { return InstanceModel.RuntimeTypeWindows; } }

        public RuntimeResolution Resolve(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            return RuntimeResolver.Resolve(instance, plugin, port, patchPath);
        }

        public string ResolveInstalledVersion(InstanceConfig instance, PluginDefinition plugin)
        {
            return RuntimeResolver.ResolveInstalledVersion(instance, plugin);
        }

        public IRuntimeProcess Start(RuntimeResolution runtime, string outputLog, string errorLog)
        {
            return CommandRunner.StartService(runtime, outputLog, errorLog);
        }

        public ProcessIdentity CaptureIdentity(IRuntimeProcess process)
        {
            if (process == null || process.ProcessId <= 0) return null;
            return ProcessInspector.Get(process.ProcessId, false);
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

        public CommandResult RunCommand(InstanceConfig instance, string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds)
        {
            return CommandRunner.RunCapture(command, arguments, workingDirectory, timeoutMilliseconds);
        }

        public List<WindowsRunningInstance> DetectRunning(PluginDefinition plugin)
        {
            List<WindowsRunningInstance> result = new List<WindowsRunningInstance>();
            if (plugin == null || plugin.Probe == null) return result;
            List<Regex> patterns = new List<Regex>();
            if (plugin.ProcessPatterns != null)
            {
                foreach (string pattern in plugin.ProcessPatterns)
                {
                    try { patterns.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                    catch { }
                }
            }
            foreach (PortOwner owner in PortMap.GetAllListenerOwners())
            {
                if (owner.Port <= 0 || owner.ProcessId <= 0) continue;
                ProcessIdentity identity = ProcessInspector.Get(owner.ProcessId, false);
                bool processMatch = false;
                foreach (Regex pattern in patterns)
                {
                    if (pattern.IsMatch(identity.CommandLine ?? String.Empty))
                    {
                        processMatch = true;
                        break;
                    }
                }
                if (!processMatch) continue;
                if (!VerifyHttp(plugin, owner.Port)) continue;
                WindowsRunningInstance running = new WindowsRunningInstance();
                running.Pid = owner.ProcessId;
                running.Port = owner.Port;
                running.CommandLine = identity.CommandLine;
                running.ImagePath = identity.ImagePath;
                running.StartTimeUtc = identity.StartTimeUtc;
                result.Add(running);
            }
            return result;
        }

        private static bool VerifyHttp(PluginDefinition plugin, int port)
        {
            try
            {
                InstanceConfig probe = new InstanceConfig();
                probe.Profile = "web";
                TokenContext context = RuntimeResolver.CreateContext(probe, plugin, port, String.Empty);
                string url = AppPaths.Expand(plugin.Probe.UrlTemplate, context);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = 1200;
                request.ReadWriteTimeout = 1200;
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
    }

    public static class RuntimeAdapters
    {
        private static readonly Dictionary<string, IRuntimeAdapter> Adapters = CreateAdapters();

        private static Dictionary<string, IRuntimeAdapter> CreateAdapters()
        {
            Dictionary<string, IRuntimeAdapter> adapters = new Dictionary<string, IRuntimeAdapter>(StringComparer.OrdinalIgnoreCase);
            WindowsRuntimeAdapter windows = new WindowsRuntimeAdapter();
            adapters.Add(windows.RuntimeType, windows);
            WslRuntimeAdapter wsl = new WslRuntimeAdapter();
            adapters.Add(wsl.RuntimeType, wsl);
            return adapters;
        }

        public static IRuntimeAdapter Get(InstanceConfig instance)
        {
            string runtimeType = instance == null || String.IsNullOrWhiteSpace(instance.RuntimeType)
                ? InstanceModel.RuntimeTypeWindows
                : instance.RuntimeType;
            IRuntimeAdapter adapter;
            if (Adapters.TryGetValue(runtimeType, out adapter)) return adapter;
            throw new InvalidOperationException("Runtime type '" + runtimeType + "' is reserved but its adapter is not implemented yet.");
        }

        public static RuntimeResolution Resolve(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            return Get(instance).Resolve(instance, plugin, port, patchPath);
        }

        public static string ResolveInstalledVersion(InstanceConfig instance, PluginDefinition plugin)
        {
            try
            {
                return Get(instance).ResolveInstalledVersion(instance, plugin);
            }
            catch
            {
                return String.Empty;
            }
        }
    }
}