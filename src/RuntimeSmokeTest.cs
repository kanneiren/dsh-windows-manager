using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DeepSeekHarnessManager
{
    public static class RuntimeSmokeTest
    {
        public static CommandResult Run(InstanceConfig source, PluginDefinition plugin, ConfigurationStore store, string expectedVersion, string runtimeId)
        {
            string suffix = Guid.NewGuid().ToString("N");
            InstanceConfig instance = Clone(source);
            instance.Id = "smoke-" + suffix;
            instance.Runtime = runtimeId;
            instance.DshHome = Path.Combine(AppPaths.RuntimeDirectory, "smoke-home-" + suffix);
            int port = ReserveFreePort();
            string outputLog = Path.Combine(AppPaths.LogDirectory, instance.Id + ".out.log");
            string errorLog = Path.Combine(AppPaths.LogDirectory, instance.Id + ".err.log");
            string runtimeDirectory = Path.Combine(AppPaths.RuntimeDirectory, instance.Id);
            RuntimeBridgeLaunch bridge = null;
            IRuntimeProcess managed = null;
            IRuntimeAdapter adapter = RuntimeAdapters.Get(instance);
            PortInspection lastInspection = null;
            try
            {
                bridge = RuntimeBridgePatch.Create(instance, plugin);
                RuntimeResolution runtime = adapter.Resolve(instance, plugin, port, bridge == null ? String.Empty : bridge.PatchPath);
                if (!String.IsNullOrWhiteSpace(expectedVersion) && SemanticVersion.Compare(runtime.Version, expectedVersion) != 0)
                    return Failure("Resolved version " + runtime.Version + " does not match expected version " + expectedVersion + ".");

                managed = adapter.Start(runtime, outputLog, errorLog);
                InstanceController inspector = new InstanceController(instance, plugin, store);
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    lastInspection = inspector.InspectPort(port);
                    if (lastInspection.Kind == InstanceStateKind.Running) break;
                    if (managed.HasExited)
                        return Failure("DSH exited before becoming ready. " + ReadTail(errorLog));
                    Thread.Sleep(500);
                }
                if (lastInspection == null || lastInspection.Kind != InstanceStateKind.Running)
                    return Failure("DSH did not become ready within 90 seconds. " + ReadTail(errorLog));

                string shutdownError;
                if (!GracefulShutdownClient.Request(bridge == null ? null : bridge.PipeName, bridge == null ? null : bridge.Token, 3000, out shutdownError))
                    return Failure("Compatibility startup passed, but graceful shutdown failed: " + shutdownError);
                DateTime stopDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < stopDeadline && PortMap.GetListenerProcessIds(port).Count > 0) Thread.Sleep(200);
                if (PortMap.GetListenerProcessIds(port).Count > 0)
                    return Failure("Compatibility startup passed, but the smoke-test port did not close.");

                return new CommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "Compatibility smoke test passed on port " + port + " using version " + runtime.Version + ".",
                    StandardError = String.Empty
                };
            }
            catch (Exception exception)
            {
                return Failure(exception.Message + " " + ReadTail(errorLog));
            }
            finally
            {
                Cleanup(lastInspection, managed, adapter, port);
                try { if (Directory.Exists(runtimeDirectory)) Directory.Delete(runtimeDirectory, true); } catch { }
                try { if (Directory.Exists(instance.DshHome)) Directory.Delete(instance.DshHome, true); } catch { }
            }
        }

        private static InstanceConfig Clone(InstanceConfig source)
        {
            return new InstanceConfig
            {
                Id = source.Id,
                Name = source.Name,
                PluginId = source.PluginId,
                Profile = source.Profile,
                Runtime = source.Runtime,
                RuntimeType = source.RuntimeType,
                WslDistro = source.WslDistro,
                Frontend = source.Frontend,
                SourceRoot = source.SourceRoot,
                Workspace = source.Workspace,
                DshHome = source.DshHome,
                PreferredPort = source.PreferredPort,
                PinnedVersion = source.PinnedVersion
            };
        }

        private static int ReserveFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static CommandResult Failure(string error)
        {
            return new CommandResult { ExitCode = 1, StandardOutput = String.Empty, StandardError = (error ?? String.Empty).Trim() };
        }

        private static string ReadTail(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return String.Empty;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - 10);
                return String.Join(" | ", new System.Collections.Generic.List<string>(lines).GetRange(start, lines.Length - start).ToArray());
            }
            catch { return String.Empty; }
        }

        private static void Cleanup(PortInspection inspection, IRuntimeProcess managed, IRuntimeAdapter adapter, int port)
        {
            try
            {
                int owner = PortMap.GetPreferredListenerProcessId(port);
                if (owner > 0 && inspection != null && owner == inspection.ProcessId && inspection.ProcessVerified)
                {
                    ProcessIdentity current = ProcessInspector.Get(owner, false);
                    if (ProcessInspector.IsSame(inspection.Process, current))
                    {
                        using (Process listener = Process.GetProcessById(owner)) CommandRunner.KillProcessTree(listener);
                    }
                }
            }
            catch { }
            if (managed != null)
            {
                try { if (!managed.HasExited) adapter.Kill(managed); } catch { }
                try { managed.Dispose(); } catch { }
            }
        }
    }
}
