using System;
using System.Collections.Generic;

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
    }

    public static class RuntimeAdapters
    {
        private static readonly Dictionary<string, IRuntimeAdapter> Adapters = CreateAdapters();

        private static Dictionary<string, IRuntimeAdapter> CreateAdapters()
        {
            Dictionary<string, IRuntimeAdapter> adapters = new Dictionary<string, IRuntimeAdapter>(StringComparer.OrdinalIgnoreCase);
            WindowsRuntimeAdapter windows = new WindowsRuntimeAdapter();
            adapters.Add(windows.RuntimeType, windows);
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