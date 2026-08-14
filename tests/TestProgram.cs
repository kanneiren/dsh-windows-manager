using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DeepSeekHarnessManager.Tests
{
    public static class TestProgram
    {
        private static int passed;
        private static int failed;

        public static int Main(string[] args)
        {
            string projectRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string tempData = Path.Combine(Path.GetTempPath(), "dsh-manager-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempData);
            Console.WriteLine("Test data: " + tempData);
            AppPaths.SetTestOverrides(projectRoot, tempData);
            try
            {
                Run("plugin catalog", TestPluginCatalog);
                Run("configuration round trip", TestConfiguration);
                Run("localization", TestLocalization);
                Run("semantic versions", TestSemanticVersions);
                Run("stable inspection cadence", TestInspectionCadence);
                Run("port map IPv4", TestPortMap);
                Run("port map IPv6", TestPortMapIpv6);
                Run("HTTP and process fingerprints", TestInspection);
                Run("external unhealthy DSH preservation", TestExternalUnhealthyHarnessPreserved);
                Run("runtime resolution", TestRuntimeResolution);
                Run("companion patch", TestCompanionPatch);
                Run("npm update check", TestUpdateCheck);
                Run("update rollback transaction", TestUpdateRollbackTransaction);
                Run("existing DSH adoption", TestExistingHarnessAdoption);
                if (Array.IndexOf(args, "--integration") >= 0)
                {
                    Run("real DSH graceful shutdown", TestRealHarnessGracefulShutdown);
                    Run("runtime compatibility smoke test", TestRuntimeCompatibilitySmokeTest);
                }
            }
            finally
            {
                if (!String.Equals(Environment.GetEnvironmentVariable("DSH_MANAGER_KEEP_TEST_DATA"), "1", StringComparison.Ordinal))
                {
                    try { Directory.Delete(tempData, true); } catch { }
                }
            }
            Console.WriteLine("Passed: " + passed + ", Failed: " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void TestPluginCatalog()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            Assert(plugin.Runtimes.Count == 3, "expected global/source/npx runtimes");
            Assert(plugin.Companion != null && plugin.Companion.Enabled, "companion must be enabled");
        }

        private static void TestConfiguration()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            Assert(config.Instances.Count == 1, "default config should contain one instance");
            Assert(config.Instances[0].Runtime == "auto", "default runtime should be auto");
            store.Save(config);
            ManagerConfig read = store.LoadOrCreate();
            Assert(read.DefaultInstanceId == config.DefaultInstanceId, "config round trip failed");

            InstanceConfig duplicatePort = CreateInstance(catalog.Get("deepseek-harness-web"), config.Instances[0].PreferredPort);
            duplicatePort.Id = "duplicate-port";
            config.Instances.Add(duplicatePort);
            bool rejected = false;
            try { store.Save(config); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "duplicate preferred ports must be rejected");
            config.Instances.Remove(duplicatePort);
        }

        private static void TestSemanticVersions()
        {
            Assert(SemanticVersion.Compare("0.1.0-rc.6", "0.1.0-rc.5") > 0, "prerelease numeric compare");
            Assert(SemanticVersion.Compare("0.1.0", "0.1.0-rc.6") > 0, "release should be newer than prerelease");
            Assert(SemanticVersion.Compare("v22.23.2", "22.19.0") > 0, "v prefix compare");
            Assert(SemanticVersion.Compare("1.0.0", "1.0.0") == 0, "equal compare");
            DateTime attempt = DateTime.UtcNow.AddHours(-10);
            DateTime next = UpdateManager.NextAutomaticCheckUtc(attempt);
            Assert(Math.Abs((next - attempt.AddHours(24)).TotalSeconds) < 2, "running manager update schedule should preserve the 24-hour interval");
        }

        private static void TestLocalization()
        {
            Localization.Initialize("en-US");
            Assert(Localization.CurrentLanguage == "en-US", "English locale was not selected");
            Assert(Localization.Text("Menu.OpenWeb") == "Open Web UI", "English locale value is missing");
            Localization.Initialize("zh-CN");
            Assert(Localization.CurrentLanguage == "zh-CN", "Chinese locale was not selected");
            Assert(Localization.Text("Menu.OpenWeb") == "\u6253\u5f00 Web UI", "Chinese locale value is missing");
            Assert(Localization.Text("Missing.Test.Key") == "Missing.Test.Key", "missing locale keys should return their key");
            Localization.Initialize("auto");
        }

        private static void TestInspectionCadence()
        {
            Assert(InstanceController.InspectionIntervalMilliseconds(InstanceStateKind.Starting) == 1000, "starting instances must retain one-second probes");
            Assert(InstanceController.InspectionIntervalMilliseconds(InstanceStateKind.Running) == 5000, "stable running instances should use five-second probes");
            Assert(InstanceController.InspectionIntervalMilliseconds(InstanceStateKind.Stopped) == 5000, "stopped instances should use five-second probes");
        }

        private static void TestPortMap()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                IList<int> owners = PortMap.GetListenerProcessIds(port);
                Assert(owners.Contains(Process.GetCurrentProcess().Id), "listener owner PID was not found");
            }
            finally { listener.Stop(); }
        }

        private static void TestPortMapIpv6()
        {
            if (!Socket.OSSupportsIPv6) return;
            TcpListener listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                IList<int> owners = PortMap.GetListenerProcessIds(port);
                Assert(owners.Contains(Process.GetCurrentProcess().Id), "IPv6 listener owner PID was not found");
            }
            finally { listener.Stop(); }
        }

        private static void TestInspection()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[2048];
                        stream.Read(buffer, 0, buffer.Length);
                        string body = "<html><title>DeepSeek Harness</title><script>window.__DSH_BOOT__={}</script></html>";
                        string response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch { }
            });
            server.IsBackground = true;
            server.Start();
            try
            {
                InstanceConfig instance = CreateInstance(plugin, port);
                InstanceController controller = new InstanceController(instance, plugin, store);
                PortInspection inspection = controller.InspectPort(port);
                Assert(inspection.Kind == InstanceStateKind.Conflict, "HTTP markers alone must not identify Harness without a process fingerprint");
                Assert(inspection.HttpVerified, "HTTP marker verification failed");
                Assert(!inspection.ProcessVerified, "test process should not match DSH command line");
            }
            finally
            {
                listener.Stop();
                server.Join(2000);
            }

            TcpListener unknown = new TcpListener(IPAddress.Loopback, 0);
            unknown.Start();
            try
            {
                int unknownPort = ((IPEndPoint)unknown.LocalEndpoint).Port;
                InstanceController controller = new InstanceController(CreateInstance(plugin, unknownPort), plugin, store);
                PortInspection inspection = controller.InspectPort(unknownPort);
                Assert(inspection.Kind == InstanceStateKind.Conflict, "unknown listener should be a conflict");
            }
            finally { unknown.Stop(); }
        }

        private static void TestRuntimeResolution()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            UseTestPnpm(plugin);
            InstanceConfig instance = CreateInstance(plugin, 3080);
            instance.Runtime = "global";
            RuntimeResolution global = RuntimeResolver.Resolve(instance, plugin, 3080, String.Empty);
            Assert(File.Exists(global.CommandPath), "global dsh command not found");
            Assert(global.Version == "0.1.0-rc.6", "unexpected global version: " + global.Version);

            string fakeSource = Path.Combine(AppPaths.DataDirectory, "fake-source");
            Directory.CreateDirectory(Path.Combine(fakeSource, ".git"));
            Directory.CreateDirectory(Path.Combine(fakeSource, "apps", "cli"));
            File.WriteAllText(Path.Combine(fakeSource, "package.json"), "{\"version\":\"9.8.7\"}");
            File.WriteAllText(Path.Combine(fakeSource, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");
            instance.Runtime = "source";
            instance.SourceRoot = fakeSource;
            RuntimeResolution source = RuntimeResolver.Resolve(instance, plugin, 3081, String.Empty);
            Assert(source.Version == "9.8.7", "source version was not read");
            Assert(source.Arguments[0] == "dsh", "source runtime must invoke pnpm dsh");

            instance.Runtime = "npx";
            instance.PinnedVersion = "0.1.0-rc.6";
            RuntimeResolution npx = RuntimeResolver.Resolve(instance, plugin, 3082, String.Empty);
            Assert(npx.Arguments.Contains("@deepseek-ai/dsh@0.1.0-rc.6"), "npx runtime must pin the selected version");
        }

        private static void TestExternalUnhealthyHarnessPreserved()
        {
            string node = AppPaths.FindOnPath("node.exe");
            Assert(!String.IsNullOrWhiteSpace(node), "node.exe is required for the external process test");
            int port = ReserveFreePort();
            string scriptDirectory = Path.Combine(AppPaths.DataDirectory, "external", "@deepseek-ai", "dsh", "lib");
            Directory.CreateDirectory(scriptDirectory);
            string scriptPath = Path.Combine(scriptDirectory, "bin.js");
            File.WriteAllText(scriptPath,
                "const net=require('net');const port=Number(process.argv[3]);net.createServer(s=>s.end('HTTP/1.1 503 Service Unavailable\\r\\nContent-Length: 0\\r\\n\\r\\n')).listen(port,'127.0.0.1');",
                Encoding.UTF8);
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = node;
            startInfo.Arguments = "\"" + scriptPath + "\" web " + port;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            using (Process process = Process.Start(startInfo))
            {
                try
                {
                    DateTime listeningDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < listeningDeadline && PortMap.GetPreferredListenerProcessId(port) == 0) Thread.Sleep(100);
                    Assert(PortMap.GetPreferredListenerProcessId(port) == process.Id, "fake external DSH did not listen on the test port");

                    PluginCatalog catalog = PluginCatalog.Load();
                    PluginDefinition plugin = catalog.Get("deepseek-harness-web");
                    ConfigurationStore store = new ConfigurationStore(catalog);
                    InstanceController controller = new InstanceController(CreateInstance(plugin, port), plugin, store);
                    controller.OpenOrStart(null);
                    Assert(controller.State == InstanceStateKind.Starting, "unhealthy external DSH should enter the starting state");
                    System.Reflection.FieldInfo deadlineField = typeof(InstanceController).GetField("startDeadlineUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    deadlineField.SetValue(controller, DateTime.UtcNow.AddSeconds(-1));
                    controller.Tick();
                    process.Refresh();
                    Assert(!process.HasExited, "the manager must not terminate an external DSH after a readiness timeout");
                }
                finally
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.WaitForExit(3000); } catch { }
                }
            }
        }

        private static void TestCompanionPatch()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            CompanionLaunch launch = CompanionPatch.Create(CreateInstance(plugin, 3080), plugin);
            Assert(File.Exists(launch.PatchPath), "patch was not created");
            Assert(launch.Token.Length == 64, "token must be 256-bit hex");
            string yaml = File.ReadAllText(launch.PatchPath);
            Assert(yaml.IndexOf("windows-lifecycle.mjs", StringComparison.Ordinal) >= 0, "patch has no companion module");
        }

        private static void TestUpdateCheck()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            InstanceConfig instance = CreateInstance(plugin, 3080);
            instance.Runtime = "global";
            InstanceController controller = new InstanceController(instance, plugin, store);
            UpdateManager updates = new UpdateManager(store, config);
            UpdateInfo info = updates.Check(controller, true);
            Assert(!String.IsNullOrWhiteSpace(info.LatestVersion), "latest npm version missing");
            Assert(SemanticVersion.Compare(info.LatestVersion, info.InstalledVersion) >= 0, "latest version is unexpectedly older");

            string registryUrl = plugin.Update.RegistryUrl;
            plugin.Update.RegistryUrl = "http://127.0.0.1:1/unreachable";
            try
            {
                UpdateInfo cached = updates.Check(controller, false);
                Assert(cached.LatestVersion == info.LatestVersion, "fresh 24-hour cache was not reused");

                InstanceConfig failedInstance = CreateInstance(plugin, 3080);
                failedInstance.Id = "failed-update-check";
                failedInstance.Runtime = "global";
                InstanceController failedController = new InstanceController(failedInstance, plugin, store);
                bool firstFailed = false;
                try { updates.Check(failedController, true); } catch { firstFailed = true; }
                Assert(firstFailed, "forced unreachable update check should fail");
                UpdateInfo deferred = updates.Check(failedController, false);
                Assert(deferred.Detail.IndexOf("deferred", StringComparison.OrdinalIgnoreCase) >= 0, "failed attempt should defer automatic retries for 24 hours");
            }
            finally { plugin.Update.RegistryUrl = registryUrl; }
        }

        private static void TestExistingHarnessAdoption()
        {
            if (PortMap.GetListenerProcessIds(3080).Count == 0) return;
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            InstanceController controller = new InstanceController(CreateInstance(plugin, 3080), plugin, store);
            PortInspection inspection = controller.InspectPort(3080);
            Assert(inspection.Kind == InstanceStateKind.Running, "port 3080 should be adopted as a running Harness");
            Assert(inspection.ProcessVerified, "the existing Harness process command line was not recognized");
        }

        private static void TestUpdateRollbackTransaction()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            UseTestPnpm(plugin);
            ConfigurationStore store = new ConfigurationStore(catalog);
            InstanceConfig globalInstance = CreateInstance(plugin, 3080);
            globalInstance.Id = "rollback-global";
            globalInstance.Runtime = "auto";
            ManagerConfig config = new ManagerConfig();
            config.SchemaVersion = 1;
            config.Language = "auto";
            config.DefaultInstanceId = globalInstance.Id;
            config.Instances = new List<InstanceConfig> { globalInstance };
            store.Save(config);
            InstanceController globalController = new InstanceController(globalInstance, plugin, store);
            RuntimeResolution globalRuntime = RuntimeResolver.Resolve(globalInstance, plugin, 3080, String.Empty);
            List<string> installedSpecs = new List<string>();
            int smokeCalls = 0;
            string smokeRuntimeId = String.Empty;
            UpdateManager globalUpdates = new UpdateManager(
                store,
                config,
                delegate(string command, IList<string> commandArgs, string workingDirectory, int timeout)
                {
                    if (commandArgs.Count >= 3 && commandArgs[0] == "install") installedSpecs.Add(commandArgs[2]);
                    return new CommandResult { ExitCode = 0, StandardOutput = "ok", StandardError = String.Empty };
                },
                delegate(InstanceConfig instance, PluginDefinition definition, ConfigurationStore configurationStore, string expectedVersion, string runtimeId)
                {
                    smokeCalls++;
                    smokeRuntimeId = runtimeId;
                    return smokeCalls == 1
                        ? new CommandResult { ExitCode = 1, StandardError = "simulated incompatible update" }
                        : new CommandResult { ExitCode = 0, StandardOutput = "rollback verified" };
                });
            string target = "0.1.0-rc.7";
            UpdateOutcome globalOutcome = globalUpdates.ExecuteTransaction(globalRuntime, globalController, target);
            Assert(!globalOutcome.Succeeded, "an incompatible update must not succeed");
            Assert(globalOutcome.RollbackAttempted && globalOutcome.RollbackSucceeded, "global update should roll back and verify the previous version");
            Assert(installedSpecs.Count == 2, "global update and rollback commands were not both executed");
            Assert(installedSpecs[0] == plugin.Update.PackageName + "@" + target, "global update did not use the exact target version");
            Assert(installedSpecs[1] == plugin.Update.PackageName + "@" + globalRuntime.Version, "global rollback did not use the exact previous version");
            Assert(smokeRuntimeId == globalRuntime.Definition.Id, "auto runtime smoke test did not stay on the updated runtime adapter");
            Assert(Directory.GetFiles(AppPaths.UpdateDirectory, "*.journal.json").Length == 0, "successful rollback should remove the update journal");

            InstanceConfig npxInstance = CreateInstance(plugin, 3081);
            npxInstance.Id = "rollback-npx";
            npxInstance.Runtime = "npx";
            npxInstance.PinnedVersion = plugin.Update.BundledVersion;
            config.DefaultInstanceId = npxInstance.Id;
            config.Instances = new List<InstanceConfig> { npxInstance };
            store.Save(config);
            InstanceController npxController = new InstanceController(npxInstance, plugin, store);
            RuntimeResolution npxRuntime = RuntimeResolver.Resolve(npxInstance, plugin, 3081, String.Empty);
            int npxSmokeCalls = 0;
            UpdateManager npxUpdates = new UpdateManager(
                store,
                config,
                delegate(string command, IList<string> commandArgs, string workingDirectory, int timeout)
                {
                    return new CommandResult { ExitCode = 0 };
                },
                delegate(InstanceConfig instance, PluginDefinition definition, ConfigurationStore configurationStore, string expectedVersion, string runtimeId)
                {
                    npxSmokeCalls++;
                    return npxSmokeCalls == 1
                        ? new CommandResult { ExitCode = 1, StandardError = "simulated npx failure" }
                        : new CommandResult { ExitCode = 0 };
                });
            string previousPin = npxInstance.PinnedVersion;
            UpdateOutcome npxOutcome = npxUpdates.ExecuteTransaction(npxRuntime, npxController, target);
            Assert(npxOutcome.RollbackSucceeded, "npx pin should be restored after a failed smoke test");
            Assert(npxInstance.PinnedVersion == previousPin, "npx rollback did not restore the previous pin");

            string fakeSource = Path.Combine(AppPaths.DataDirectory, "rollback-source");
            Directory.CreateDirectory(Path.Combine(fakeSource, ".git"));
            Directory.CreateDirectory(Path.Combine(fakeSource, "apps", "cli"));
            File.WriteAllText(Path.Combine(fakeSource, "package.json"), "{\"version\":\"9.8.7\"}");
            File.WriteAllText(Path.Combine(fakeSource, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");
            InstanceConfig sourceInstance = CreateInstance(plugin, 3082);
            sourceInstance.Id = "rollback-source";
            sourceInstance.Runtime = "source";
            sourceInstance.SourceRoot = fakeSource;
            config.DefaultInstanceId = sourceInstance.Id;
            config.Instances = new List<InstanceConfig> { sourceInstance };
            store.Save(config);
            InstanceController sourceController = new InstanceController(sourceInstance, plugin, store);
            RuntimeResolution sourceRuntime = RuntimeResolver.Resolve(sourceInstance, plugin, 3082, String.Empty);
            bool resetToPreviousSha = false;
            int sourceSmokeCalls = 0;
            int sourceShaReads = 0;
            string previousSha = "1234567890abcdef1234567890abcdef12345678";
            string updatedSha = "abcdef1234567890abcdef1234567890abcdef12";
            UpdateManager sourceUpdates = new UpdateManager(
                store,
                config,
                delegate(string command, IList<string> commandArgs, string workingDirectory, int timeout)
                {
                    if (commandArgs.Contains("rev-parse"))
                    {
                        sourceShaReads++;
                        return new CommandResult { ExitCode = 0, StandardOutput = (sourceShaReads == 1 ? previousSha : updatedSha) + Environment.NewLine };
                    }
                    if (commandArgs.Contains("reset") && commandArgs.Contains(previousSha)) resetToPreviousSha = true;
                    return new CommandResult { ExitCode = 0, StandardOutput = String.Empty, StandardError = String.Empty };
                },
                delegate(InstanceConfig instance, PluginDefinition definition, ConfigurationStore configurationStore, string expectedVersion, string runtimeId)
                {
                    sourceSmokeCalls++;
                    return sourceSmokeCalls == 1
                        ? new CommandResult { ExitCode = 1, StandardError = "simulated source incompatibility" }
                        : new CommandResult { ExitCode = 0 };
                });
            UpdateOutcome sourceOutcome = sourceUpdates.ExecuteTransaction(sourceRuntime, sourceController, "abcdef12");
            Assert(sourceOutcome.RollbackSucceeded, "clean source checkout should roll back after failed compatibility testing");
            Assert(resetToPreviousSha, "source rollback did not reset to the recorded previous commit");

            UpdateManager failedRollback = new UpdateManager(
                store,
                config,
                delegate(string command, IList<string> commandArgs, string workingDirectory, int timeout) { return new CommandResult { ExitCode = 0 }; },
                delegate(InstanceConfig instance, PluginDefinition definition, ConfigurationStore configurationStore, string expectedVersion, string runtimeId) { return new CommandResult { ExitCode = 1, StandardError = "simulated persistent failure" }; });
            UpdateOutcome failedOutcome = failedRollback.ExecuteTransaction(npxRuntime, npxController, target);
            Assert(failedOutcome.RollbackAttempted && !failedOutcome.RollbackSucceeded, "failed rollback verification must be reported");
            string[] journals = Directory.GetFiles(AppPaths.UpdateDirectory, "*.journal.json");
            Assert(journals.Length == 1, "failed rollback should preserve one recovery journal");
            foreach (string journal in journals) File.Delete(journal);
        }

        private static void TestRuntimeCompatibilitySmokeTest()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            InstanceConfig instance = CreateInstance(plugin, ReserveFreePort());
            instance.Id = "compatibility-smoke";
            instance.Runtime = "global";
            CommandResult result = RuntimeSmokeTest.Run(instance, plugin, store, plugin.Update.BundledVersion, "global");
            Assert(result.ExitCode == 0, "runtime compatibility smoke test failed: " + result.StandardError);
        }

        private static void TestRealHarnessGracefulShutdown()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            int port = ReserveFreePort();
            InstanceConfig instance = CreateInstance(plugin, port);
            instance.Runtime = "global";
            instance.Workspace = AppPaths.DataDirectory;
            instance.DshHome = String.Empty;
            CompanionLaunch bridge = CompanionPatch.Create(instance, plugin);
            RuntimeResolution runtime = RuntimeResolver.Resolve(instance, plugin, port, bridge.PatchPath);
            string output = Path.Combine(AppPaths.LogDirectory, "integration.out.log");
            string error = Path.Combine(AppPaths.LogDirectory, "integration.err.log");
            ManagedProcess process = CommandRunner.StartService(runtime, output, error);
            PortInspection running = null;
            InstanceController inspector = new InstanceController(instance, plugin, store);
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    running = inspector.InspectPort(port);
                    if (running.Kind == InstanceStateKind.Running) break;
                    if (process.RootProcess.HasExited) throw new Exception("DSH exited during startup: " + ReadIfExists(error));
                    Thread.Sleep(500);
                }
                Assert(running != null && running.Kind == InstanceStateKind.Running, "real DSH did not become ready: " + ReadIfExists(error));
                string shutdownError;
                Assert(GracefulShutdownClient.Request(bridge.PipeName, bridge.Token, 3000, out shutdownError), "shutdown bridge failed: " + shutdownError);
                using (Process listener = Process.GetProcessById(running.ProcessId)) Assert(listener.WaitForExit(8000), "DSH did not exit after graceful request");
                Assert(PortMap.GetListenerProcessIds(port).Count == 0, "port remained occupied after graceful shutdown");
            }
            finally
            {
                int owner = PortMap.GetPreferredListenerProcessId(port);
                if (owner > 0)
                {
                    try { Process.GetProcessById(owner).Kill(); } catch { }
                }
                try { if (!process.RootProcess.HasExited) process.RootProcess.Kill(); } catch { }
            }
        }

        private static InstanceConfig CreateInstance(PluginDefinition plugin, int port)
        {
            InstanceConfig instance = new InstanceConfig();
            instance.Id = "test";
            instance.Name = "Test Harness";
            instance.PluginId = plugin.Id;
            instance.Profile = "web";
            instance.Runtime = "auto";
            instance.SourceRoot = String.Empty;
            instance.Workspace = AppPaths.DataDirectory;
            instance.PreferredPort = port;
            instance.PinnedVersion = plugin.Update.BundledVersion;
            return instance;
        }

        private static void UseTestPnpm(PluginDefinition plugin)
        {
            string command = Path.Combine(AppPaths.DataDirectory, "fake-tools", "pnpm.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(command));
            File.WriteAllText(command, "@exit /b 0\r\n", Encoding.ASCII);
            foreach (RuntimeDefinition runtime in plugin.Runtimes)
            {
                if (!String.Equals(runtime.Kind, "source", StringComparison.OrdinalIgnoreCase)) continue;
                runtime.CommandCandidates = new List<string> { command };
                return;
            }
            throw new InvalidOperationException("The source runtime definition was not found.");
        }

        private static int ReserveFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string ReadIfExists(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : String.Empty; } catch { return String.Empty; }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + exception.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
