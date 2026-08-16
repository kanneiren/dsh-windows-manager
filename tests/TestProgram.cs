using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
                Run("manager service facade", TestManagerServiceFacade);
                Run("detected WSL instance registration", TestDetectedWslInstanceRegistration);
                Run("manager interaction routing", TestManagerInteractionRouting);
                Run("manager control protocol", TestManagerControlProtocol);
                Run("DSH settings path", TestDshSettingsPath);
                Run("localization", TestLocalization);
                Run("semantic versions", TestSemanticVersions);
                Run("stable inspection cadence", TestInspectionCadence);
                Run("port map IPv4", TestPortMap);
                Run("port map IPv6", TestPortMapIpv6);
                Run("HTTP and process fingerprints", TestInspection);
                Run("external unhealthy DSH preservation", TestExternalUnhealthyHarnessPreserved);
                Run("runtime resolution", TestRuntimeResolution);
                Run("runtime adapter registry", TestRuntimeAdapters);
                Run("runtime bridge patch", TestRuntimeBridgePatch);
                Run("wsl runtime bridge patch", TestWslRuntimeBridgePatch);
                Run("frontend launcher", TestFrontendLauncher);
                Run("bridge protocol", TestBridgeProtocol);
                Run("tcp bridge connection", TestTcpBridgeConnection);
                Run("log retention", TestLogRetention);
                Run("npm update check", TestUpdateCheck);
                Run("update rollback transaction", TestUpdateRollbackTransaction);
                Run("existing DSH adoption", TestExistingHarnessAdoption);
                if (Array.IndexOf(args, "--integration") >= 0)
                {
                    Run("real DSH graceful shutdown", TestRealHarnessGracefulShutdown);
                    Run("runtime compatibility smoke test", TestRuntimeCompatibilitySmokeTest);
                    Run("wsl runtime adapter resolution", TestWslRuntimeAdapterResolution);
                Run("wsl runtime resolution kinds", TestWslRuntimeResolutionKinds);
                    Run("wsl runtime command and launch", TestWslRuntimeCommandAndLaunch);
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
            Assert(plugin.RuntimeBridge != null && plugin.RuntimeBridge.Enabled, "runtime bridge must be enabled");
            Assert(plugin.RuntimeBridge.BridgeProtocolVersion == 1, "runtime bridge protocol version must be 1");
            Assert(plugin.MarketplaceUrl == "https://github.com/topics/dsh-plugin", "plugin marketplace URL is missing");
        }

        private static void TestConfiguration()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            Assert(config.Instances.Count == 1, "default config should contain one instance");
            Assert(config.Instances[0].Runtime == "auto", "default runtime should be auto");
            Assert(config.Instances[0].RuntimeType == InstanceModel.RuntimeTypeWindows, "default runtime type should be windows");
            Assert(config.Instances[0].Frontend == InstanceModel.FrontendWeb, "default frontend should be web");
            Assert(config.TrayEnabled.HasValue && config.TrayEnabled.Value, "default tray mode should be enabled");
            Assert(!config.StartWithWindows.Value, "Start with Windows should default to false");
            Assert(!config.DesktopShortcut.Value, "desktop shortcut should default to false in a source-created config");
            Assert(!config.WslEnabled.Value, "WSL support should default to false");
            Assert(config.WslDefaultDistro.Length == 0, "WSL default distro should start empty");
            config.TrayEnabled = false;
            store.Save(config);
            Assert(!store.LoadOrCreate().TrayEnabled.Value, "disabled tray mode was not preserved");
            config.TrayEnabled = true;
            store.Save(config);
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

            InstanceConfig reserved = CreateInstance(catalog.Get("deepseek-harness-web"), 4099);
            reserved.Id = "wsl-reserved";
            reserved.RuntimeType = InstanceModel.RuntimeTypeWsl;
            reserved.Frontend = InstanceModel.FrontendOhDsh;
            config.Instances.Add(reserved);
            bool disabledWslRejected = false;
            try { store.Save(config); } catch (InvalidDataException) { disabledWslRejected = true; }
            Assert(disabledWslRejected, "wsl runtime must be rejected while WSL support is disabled");
            config.WslEnabled = true;
            config.WslDefaultDistro = "TestDistro";
            store.Save(config);
            ManagerConfig reservedRead = store.LoadOrCreate();
            Assert(reservedRead.Instances[reservedRead.Instances.Count - 1].RuntimeType == InstanceModel.RuntimeTypeWsl, "wsl runtime type must be accepted when enabled");
            Assert(reservedRead.Instances[reservedRead.Instances.Count - 1].Frontend == InstanceModel.FrontendOhDsh, "oh-dsh frontend must be accepted as a reserved value");
            config.Instances.Remove(reserved);
            config.WslEnabled = false;
            config.WslDefaultDistro = String.Empty;
            store.Save(config);
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

        private static void TestManagerServiceFacade()
        {
            Localization.Initialize("en-US");
            PluginCatalog catalog = PluginCatalog.Load();
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            using (ManagerService service = new ManagerService(config, catalog, store, SilentManagerInteraction.Instance))
            {
                ManagerSnapshot snapshot = service.GetSnapshot();
                Assert(snapshot.Instances.Count == 1, "manager service should expose one default instance");
                Assert(snapshot.DefaultInstanceId == snapshot.Instances[0].Id, "manager service default instance mismatch");
                Assert(snapshot.Instances[0].State == InstanceStateKind.Stopped, "new manager service instance should start stopped");
                Assert(snapshot.Instances[0].RuntimeType == InstanceModel.RuntimeTypeWindows, "manager snapshot should expose runtime type");
                Assert(snapshot.Instances[0].Ownership == InstanceModel.OwnershipAttached, "unowned stopped instance should default to attached");
                Assert(snapshot.Instances[0].Frontend == InstanceModel.FrontendWeb, "manager snapshot should expose frontend");
                Assert(snapshot.TrayEnabled, "manager snapshot should expose tray mode");
                Assert(!snapshot.StartWithWindows, "manager snapshot should expose autostart mode");
                Assert(!snapshot.DesktopShortcut, "manager snapshot should expose shortcut mode");
                Assert(!snapshot.WslEnabled, "manager snapshot should expose WSL enablement");
                Assert(snapshot.WslDefaultDistro.Length == 0, "manager snapshot should expose the WSL default distro");
                string details = service.GetInstanceDetails(snapshot.DefaultInstanceId);
                Assert(details.IndexOf(snapshot.Instances[0].Name, StringComparison.Ordinal) >= 0, "manager service details should contain the instance name");
                string diagnostics = service.GetDiagnosticsText();
                Assert(diagnostics.IndexOf("Manager version:", StringComparison.Ordinal) >= 0, "diagnostics should contain the manager version");
                Assert(diagnostics.IndexOf(snapshot.Instances[0].Name, StringComparison.Ordinal) >= 0, "diagnostics should contain the instance name");
            }
        }

        private static void TestDetectedWslInstanceRegistration()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            config.WslEnabled = true;
            config.WslDefaultDistro = "TestDistro";
            store.Save(config);
            using (ManagerService service = new ManagerService(config, catalog, store, SilentManagerInteraction.Instance))
            {
                WslRunningInstance detected = new WslRunningInstance();
                detected.Distro = "TestDistro";
                detected.Pid = 12345;
                detected.Port = 4098;
                detected.CommandLine = "node dsh web --port 4098";
                service.RegisterDetectedWslInstance(detected);
                ManagerSnapshot snapshot = service.GetSnapshot();
                Assert(snapshot.Instances.Count == 2, "detected WSL instance should appear in the snapshot");
                InstanceSnapshot dynamic = snapshot.Instances[1];
                Assert(dynamic.RuntimeType == InstanceModel.RuntimeTypeWsl, "detected instance should be wsl runtime type");
                Assert(dynamic.IsDetected, "detected instance should be marked dynamic");
                service.SaveDetectedInstance(dynamic.Id);
                ManagerSnapshot savedSnapshot = service.GetSnapshot();
                Assert(!savedSnapshot.Instances[1].IsDetected, "saved WSL instance should no longer be dynamic");
                ManagerConfig savedConfig = store.LoadOrCreate();
                Assert(savedConfig.Instances.Count == 2, "saved WSL instance should be persisted to config");
                savedConfig.Instances.RemoveAt(savedConfig.Instances.Count - 1);
                savedConfig.WslEnabled = false;
                savedConfig.WslDefaultDistro = String.Empty;
                store.Save(savedConfig);
            }
        }

        private static void TestManagerInteractionRouting()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            InstanceController controller = new InstanceController(CreateInstance(plugin, 3080), plugin, store);
            controller.UpdateInfo = new UpdateInfo { UpdateAvailable = false };
            RecordingInteraction interaction = new RecordingInteraction();
            UpdateManager updates = new UpdateManager(store, config, interaction);
            bool accepted = updates.ExecuteConfirmedUpdate(controller);
            Assert(!accepted, "an unavailable update must not be accepted");
            Assert(interaction.InformationCount == 1, "update unavailable should route through the interaction boundary");
        }

        private static void TestManagerControlProtocol()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            ConfigurationStore store = new ConfigurationStore(catalog);
            ManagerConfig config = store.LoadOrCreate();
            using (ManagerService service = new ManagerService(config, catalog, store, SilentManagerInteraction.Instance))
            using (ManagerControlServer server = new ManagerControlServer(service, "dsh-windows-manager-test-" + Guid.NewGuid().ToString("N")))
            {
                server.Start();
                Dictionary<string, object> version = SendControl(server, "{\"protocolVersion\":1,\"command\":\"getVersion\"}");
                Assert(Convert.ToBoolean(version["ok"]), "getVersion should succeed");
                Assert(BridgeProtocol.GetInt(version, "protocolVersion") == ManagerControlProtocol.CurrentProtocolVersion, "control protocol version mismatch");
                Assert(!String.IsNullOrWhiteSpace(BridgeProtocol.GetString(version, "managerVersion")), "manager version is missing");

                Dictionary<string, object> list = SendControl(server, "{\"protocolVersion\":1,\"command\":\"listInstances\"}");
                Assert(Convert.ToBoolean(list["ok"]), "listInstances should succeed");
                System.Collections.IEnumerable instances = BridgeProtocol.GetValue(list, "instances") as System.Collections.IEnumerable;
                int instanceCount = 0;
                if (instances != null) foreach (object item in instances) instanceCount++;
                Assert(instanceCount == 1, "listInstances should return the configured instance");

                Dictionary<string, object> status = SendControl(server, "{\"protocolVersion\":1,\"command\":\"getStatus\"}");
                Assert(Convert.ToBoolean(status["ok"]), "getStatus should succeed");
                Assert(BridgeProtocol.GetString(status, "runtime") == InstanceModel.RuntimeTypeWindows, "getStatus runtime type mismatch");
                Assert(BridgeProtocol.GetString(status, "frontend") == InstanceModel.FrontendWeb, "getStatus frontend mismatch");
                Assert(BridgeProtocol.GetString(status, "ownership") == InstanceModel.OwnershipAttached, "getStatus ownership mismatch");
                Assert(Convert.ToBoolean(status["trayEnabled"]), "getStatus trayEnabled mismatch");
                Assert(!Convert.ToBoolean(status["wslEnabled"]), "getStatus wslEnabled mismatch");
                Assert(BridgeProtocol.GetString(status, "wslDefaultDistro") == String.Empty, "getStatus wslDefaultDistro mismatch");

                Dictionary<string, object> unknown = SendControl(server, "{\"protocolVersion\":1,\"command\":\"runCommand\"}");
                Assert(!Convert.ToBoolean(unknown["ok"]), "unknown commands must be rejected");
                Dictionary<string, object> unknownError = BridgeProtocol.GetValue(unknown, "error") as Dictionary<string, object>;
                Assert(unknownError != null && BridgeProtocol.GetString(unknownError, "code") == "unknown-command", "unknown command error code missing");

                Dictionary<string, object> unsupported = SendControl(server, "{\"protocolVersion\":99,\"command\":\"getVersion\"}");
                Assert(!Convert.ToBoolean(unsupported["ok"]), "unsupported protocol versions must be rejected");
                Dictionary<string, object> unsupportedError = BridgeProtocol.GetValue(unsupported, "error") as Dictionary<string, object>;
                Assert(unsupportedError != null && BridgeProtocol.GetString(unsupportedError, "code") == "protocol-version-unsupported", "unsupported protocol error code missing");

                Dictionary<string, object> exitResponse = SendControl(server, "{\"protocolVersion\":1,\"command\":\"exit\"}");
                Assert(Convert.ToBoolean(exitResponse["ok"]), "exit should be accepted by the control protocol");
            }
        }

        private static Dictionary<string, object> SendControl(ManagerControlServer server, string json)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", server.Name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(3000);
                pipe.ReadMode = PipeTransmissionMode.Byte;
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                {
                    writer.NewLine = "\n";
                    writer.WriteLine(json);
                    writer.Flush();
                }
                Task<string> readTask = Task.Factory.StartNew(delegate
                {
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true)) return reader.ReadLine();
                });
                if (!readTask.Wait(5000)) throw new TimeoutException("control test request timed out");
                return JsonStore.Deserialize<Dictionary<string, object>>(readTask.Result);
            }
        }

        private static void TestDshSettingsPath()
        {
            InstanceConfig instance = new InstanceConfig();
            instance.Workspace = AppPaths.DataDirectory;
            instance.DshHome = "isolated-home";
            Assert(AppPaths.DshSettingsFile(instance) == Path.Combine(AppPaths.DataDirectory, "isolated-home", "settings.yaml"), "relative DSH home should resolve from the workspace");

            string previous = Environment.GetEnvironmentVariable("DSH_HOME");
            try
            {
                string environmentHome = Path.Combine(AppPaths.DataDirectory, "environment-home");
                Environment.SetEnvironmentVariable("DSH_HOME", environmentHome);
                instance.DshHome = String.Empty;
                Assert(AppPaths.DshSettingsFile(instance) == Path.Combine(environmentHome, "settings.yaml"), "DSH_HOME should select the settings file");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_HOME", previous);
            }
        }

        private static void TestLocalization()
        {
            Localization.Initialize("en-US");
            Assert(Localization.CurrentLanguage == "en-US", "English locale was not selected");
            Assert(Localization.Text("Menu.OpenWeb") == "Open Web UI", "English locale value is missing");
            Assert(Localization.Text("Menu.OpenManagerConfig") == "Open manager configuration file", "manager configuration label is missing");
            Localization.Initialize("zh-CN");
            Assert(Localization.CurrentLanguage == "zh-CN", "Chinese locale was not selected");
            Assert(Localization.Text("Menu.OpenWeb") == "\u6253\u5f00 Web UI", "Chinese locale value is missing");
            Assert(Localization.Text("Menu.OpenDshSettings") == "\u6253\u5f00 DSH \u914d\u7f6e\u6587\u4ef6", "DSH settings label is missing");
            Assert(Localization.Text("Menu.OpenOhDsh") == "打开 oh-dsh", "oh-dsh menu label is missing");
            Assert(Localization.Text("Menu.CopyDiagnostics") == "复制诊断信息", "copy diagnostics menu label is missing");
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

        private static void TestRuntimeAdapters()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            InstanceConfig instance = CreateInstance(plugin, 3080);
            IRuntimeAdapter adapter = RuntimeAdapters.Get(instance);
            Assert(adapter is WindowsRuntimeAdapter, "windows runtime should resolve to WindowsRuntimeAdapter");
            Assert(adapter.RuntimeType == InstanceModel.RuntimeTypeWindows, "runtime adapter type mismatch");
            RuntimeResolution viaAdapter = adapter.Resolve(instance, plugin, 3080, String.Empty);
            RuntimeResolution direct = RuntimeResolver.Resolve(instance, plugin, 3080, String.Empty);
            Assert(viaAdapter.CommandPath == direct.CommandPath, "windows adapter should preserve runtime resolution");
            instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
            instance.WslDistro = "TestDistro";
            IRuntimeAdapter wslAdapter = RuntimeAdapters.Get(instance);
            Assert(wslAdapter is WslRuntimeAdapter, "wsl runtime should resolve to WslRuntimeAdapter");
            Assert(wslAdapter.RuntimeType == InstanceModel.RuntimeTypeWsl, "wsl adapter runtime type mismatch");
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
                    controller.OpenOrStart();
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

        private static void TestRuntimeBridgePatch()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            RuntimeBridgeLaunch launch = RuntimeBridgePatch.Create(CreateInstance(plugin, 3080), plugin);
            Assert(File.Exists(launch.PatchPath), "patch was not created");
            Assert(launch.Token.Length == 64, "token must be 256-bit hex");
            string yaml = File.ReadAllText(launch.PatchPath);
            Assert(yaml.IndexOf("windows-lifecycle.mjs", StringComparison.Ordinal) >= 0, "patch has no runtime bridge module");
            Assert(yaml.IndexOf("profile:", StringComparison.Ordinal) >= 0, "patch does not record the launched profile");
        }

        private static void TestWslRuntimeBridgePatch()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            InstanceConfig instance = CreateInstance(plugin, 3080);
            instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
            instance.WslDistro = "TestDistro";
            RuntimeBridgeLaunch launch = RuntimeBridgePatch.Create(instance, plugin);
            Assert(launch.Transport == "tcp", "wsl runtime bridge should use tcp transport");
            Assert(launch.Port > 0 && launch.Port <= 65535, "wsl runtime bridge should reserve a loopback port");
            Assert(launch.Host == "127.0.0.1", "wsl runtime bridge host should be loopback");
            string yaml = File.ReadAllText(launch.PatchPath);
            Assert(yaml.IndexOf("transport:", StringComparison.Ordinal) >= 0, "wsl patch should declare tcp transport");
            Assert(yaml.IndexOf("pipeName:", StringComparison.Ordinal) < 0, "wsl patch must not declare a named pipe");
        }

        private static void TestFrontendLauncher()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            InstanceConfig instance = CreateInstance(plugin, 3080);
            string url;
            string error;
            Assert(FrontendLauncher.TryResolve(instance, plugin, 3080, out url, out error), "web frontend should resolve");
            Assert(url == "http://127.0.0.1:3080/", "web frontend URL mismatch");
            instance.Frontend = InstanceModel.FrontendOhDsh;
            Assert(!FrontendLauncher.TryResolve(instance, plugin, 3080, out url, out error), "oh-dsh should not be silently opened");
            Assert(error.IndexOf(InstanceModel.FrontendOhDsh, StringComparison.Ordinal) >= 0, "oh-dsh not-configured error should name the frontend");
            instance.Frontend = InstanceModel.FrontendCustom;
            Assert(!FrontendLauncher.TryResolve(instance, plugin, 3080, out url, out error), "custom frontend should not be silently opened");
        }

        private static void TestBridgeProtocol()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["pong"] = true;
            BridgeMessage command = BridgeProtocol.Command("ping", "a".PadRight(64, 'a'), payload);
            Assert(command.ProtocolVersion == BridgeProtocol.CurrentProtocolVersion, "bridge command protocol version mismatch");
            Assert(command.MessageType == "command", "bridge command message type mismatch");
            Assert(!String.IsNullOrWhiteSpace(command.RequestId), "bridge command request id missing");

            string json = BridgeProtocol.Serialize(command);
            Assert(json.IndexOf("\"type\":\"ping\"", StringComparison.Ordinal) >= 0, "bridge wire format must use lowercase protocol keys");
            BridgeMessage roundTrip = BridgeProtocol.Deserialize(json);
            Assert(roundTrip.RequestId == command.RequestId, "bridge command round trip failed");
            Assert(BridgeProtocol.GetString(roundTrip.Payload, "pong") == "True", "bridge payload round trip failed");

            BridgeMessage rejection = BridgeProtocol.Deserialize("{\"protocolVersion\":1,\"ok\":false,\"error\":{\"code\":\"unauthorized\"}}");
            Assert(BridgeProtocol.ErrorCode(rejection) == "unauthorized", "bridge error code was not parsed");
        }

        private static void TestTcpBridgeConnection()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Factory.StartNew(delegate
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096))
                {
                    writer.AutoFlush = true;
                    writer.NewLine = "\n";
                    BridgeMessage request = BridgeProtocol.Deserialize(reader.ReadLine());
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["pong"] = true;
                    BridgeMessage response = BridgeProtocol.Command(request.Type, request.Token, payload);
                    response.MessageType = "response";
                    response.RequestId = request.RequestId;
                    response.Ok = true;
                    writer.WriteLine(BridgeProtocol.Serialize(response));
                }
            });
            try
            {
                string error;
                using (IpcBridgeConnection connection = IpcBridgeConnection.ConnectTcp("127.0.0.1", port, "a".PadRight(64, 'a'), 2000, out error))
                {
                    Assert(connection != null, "TCP bridge connect failed: " + error);
                    BridgeMessage response = connection.Request("ping", null, 1500);
                    Assert(response != null && response.Ok, "TCP bridge ping failed");
                }
            }
            finally
            {
                listener.Stop();
                server.Wait(3000);
            }
        }

        private static void TestLogRetention()
        {
            string logDirectory = AppPaths.LogDirectory;
            Directory.CreateDirectory(logDirectory);

            DateTime nowUtc = DateTime.UtcNow;
            DateTime expiredUtc = nowUtc.AddDays(-(FileLog.InstanceLogRetentionDays + 1));
            int i;
            for (i = 0; i < 22; i++)
            {
                string stamp = nowUtc.AddMinutes(-i).ToString("yyyyMMdd-HHmmss");
                string outLog = Path.Combine(logDirectory, "web-" + stamp + ".out.log");
                string errLog = Path.Combine(logDirectory, "web-" + stamp + ".err.log");
                File.WriteAllText(outLog, "dsh web: http://127.0.0.1:3080" + Environment.NewLine);
                File.WriteAllText(errLog, "$ node --profile web" + Environment.NewLine);
                File.SetLastWriteTimeUtc(outLog, nowUtc.AddMinutes(-i));
                File.SetLastWriteTimeUtc(errLog, nowUtc.AddMinutes(-i));
            }
            string expiredOut = Path.Combine(logDirectory, "web-" + expiredUtc.ToString("yyyyMMdd-HHmmss") + ".out.log");
            string expiredErr = Path.Combine(logDirectory, "web-" + expiredUtc.ToString("yyyyMMdd-HHmmss") + ".err.log");
            File.WriteAllText(expiredOut, "old" + Environment.NewLine);
            File.WriteAllText(expiredErr, "old" + Environment.NewLine);
            File.SetLastWriteTimeUtc(expiredOut, expiredUtc);
            File.SetLastWriteTimeUtc(expiredErr, expiredUtc);

            string multiOut = Path.Combine(logDirectory, "source-dev-" + expiredUtc.ToString("yyyyMMdd-HHmmss") + ".out.log");
            string multiErr = Path.Combine(logDirectory, "source-dev-" + expiredUtc.ToString("yyyyMMdd-HHmmss") + ".err.log");
            File.WriteAllText(multiOut, "old" + Environment.NewLine);
            File.WriteAllText(multiErr, "old" + Environment.NewLine);
            File.SetLastWriteTimeUtc(multiOut, expiredUtc);
            File.SetLastWriteTimeUtc(multiErr, expiredUtc);

            byte[] filler = new byte[4096];
            using (FileStream stream = File.Create(AppPaths.ManagerLog))
            {
                for (long written = 0; written < FileLog.ManagerLogRolloverBytes + 4096; written += filler.Length)
                    stream.Write(filler, 0, filler.Length);
            }

            FileLog.EnforceRetention();

            Assert(File.Exists(AppPaths.ManagerLog + ".1"), "manager.log was not rolled over");
            Assert(!File.Exists(expiredOut) && !File.Exists(expiredErr), "expired instance logs were not removed");
            Assert(!File.Exists(multiOut) && !File.Exists(multiErr), "expired non-web instance logs were not removed");
            string[] remaining = Directory.GetFiles(logDirectory, "web-*.log");
            Assert(remaining.Length <= FileLog.MaxInstanceLogPairs * 2, "instance log count exceeded the retention bound");
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

        private static void TestWslRuntimeResolutionKinds()
        {
            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            WslRuntimeAdapter adapter = new WslRuntimeAdapter();

            InstanceConfig npx = CreateInstance(plugin, 3080);
            npx.RuntimeType = InstanceModel.RuntimeTypeWsl;
            npx.WslDistro = "TestDistro";
            npx.Runtime = "npx";
            npx.PinnedVersion = "1.2.3";
            RuntimeResolution npxResolution = adapter.Resolve(npx, plugin, 3080, String.Empty);
            Assert(npxResolution.Version == "1.2.3", "wsl npx version should come from PinnedVersion");
            Assert(npxResolution.Arguments[npxResolution.Arguments.Count - 1].IndexOf("@deepseek-ai/dsh@1.2.3", StringComparison.Ordinal) >= 0, "wsl npx launch command mismatch");

            string fakeSource = Path.Combine(AppPaths.DataDirectory, "wsl-fake-source");
            Directory.CreateDirectory(fakeSource);
            File.WriteAllText(Path.Combine(fakeSource, "package.json"), "{\"version\":\"9.8.7\"}", Encoding.UTF8);
            InstanceConfig source = CreateInstance(plugin, 3081);
            source.RuntimeType = InstanceModel.RuntimeTypeWsl;
            source.WslDistro = "TestDistro";
            source.Runtime = "source";
            source.SourceRoot = fakeSource;
            RuntimeResolution sourceResolution = adapter.Resolve(source, plugin, 3081, String.Empty);
            Assert(sourceResolution.Version == "9.8.7", "wsl source version should be read from package.json");
            Assert(sourceResolution.Arguments[sourceResolution.Arguments.Count - 1].IndexOf("pnpm dsh", StringComparison.Ordinal) >= 0, "wsl source launch command mismatch");

            InstanceConfig invalid = CreateInstance(plugin, 3082);
            invalid.RuntimeType = InstanceModel.RuntimeTypeWsl;
            invalid.WslDistro = "TestDistro";
            invalid.Runtime = "unknown-kind";
            bool rejected = false;
            try { adapter.Resolve(invalid, plugin, 3082, String.Empty); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "unknown wsl runtime kinds must be rejected");
        }

        private static void TestWslRuntimeAdapterResolution()
        {
            string[] distros = GetWslDistros();
            if (distros.Length == 0) return;

            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            InstanceConfig instance = CreateInstance(plugin, ReserveFreePort());
            instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
            instance.WslDistro = distros[0];
            instance.Workspace = AppPaths.DataDirectory;
            RuntimeResolution resolution = null;
            string selectedDistro = null;
            foreach (string distro in distros)
            {
                instance.WslDistro = distro;
                WslRuntimeAdapter adapter = new WslRuntimeAdapter();
                RuntimeResolution candidate = adapter.Resolve(instance, plugin, instance.PreferredPort, String.Empty);
                if (!String.IsNullOrWhiteSpace(candidate.Version))
                {
                    resolution = candidate;
                    selectedDistro = distro;
                    break;
                }
            }
            if (resolution == null) return;
            Assert(resolution.CommandPath.IndexOf("wsl.exe", StringComparison.OrdinalIgnoreCase) >= 0, "wsl adapter should launch through wsl.exe");
            Assert(resolution.Arguments.Contains(selectedDistro), "wsl adapter should pass the configured distro");
            Assert(!String.IsNullOrWhiteSpace(resolution.Version), "wsl adapter should resolve the installed DSH version");
        }

        private static void TestWslRuntimeCommandAndLaunch()
        {
            string[] distros = GetWslDistros();
            if (distros.Length == 0) return;
            string distro = null;
            foreach (string candidate in distros)
            {
                try
                {
                    InstanceConfig probe = CreateInstance(PluginCatalog.Load().Get("deepseek-harness-web"), 3080);
                    probe.RuntimeType = InstanceModel.RuntimeTypeWsl;
                    probe.WslDistro = candidate;
                    WslRuntimeAdapter probeAdapter = new WslRuntimeAdapter();
                    if (!String.IsNullOrWhiteSpace(probeAdapter.Resolve(probe, PluginCatalog.Load().Get("deepseek-harness-web"), 3080, String.Empty).Version)) { distro = candidate; break; }
                }
                catch { }
            }
            if (distro == null) return;

            PluginCatalog catalog = PluginCatalog.Load();
            PluginDefinition plugin = catalog.Get("deepseek-harness-web");
            ConfigurationStore store = new ConfigurationStore(catalog);
            InstanceConfig instance = CreateInstance(plugin, ReserveFreePort());
            instance.Id = "wsl-integration";
            instance.Runtime = "global";
            instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
            instance.WslDistro = distro;
            instance.Workspace = AppPaths.DataDirectory;
            WslRuntimeAdapter adapter = new WslRuntimeAdapter();
            CommandResult node = adapter.RunCommand(instance, "node", new string[] { "--version" }, instance.Workspace, 10000);
            Assert(node.ExitCode == 0 && !String.IsNullOrWhiteSpace(node.StandardOutput), "wsl adapter should run commands inside the configured distro");

            RuntimeBridgeLaunch bridge = RuntimeBridgePatch.Create(instance, plugin);
            RuntimeResolution runtime = adapter.Resolve(instance, plugin, instance.PreferredPort, bridge.PatchPath);
            string outputLog = Path.Combine(AppPaths.LogDirectory, instance.Id + ".out.log");
            string errorLog = Path.Combine(AppPaths.LogDirectory, instance.Id + ".err.log");
            IRuntimeProcess process = adapter.Start(runtime, outputLog, errorLog);
            try
            {
                Thread.Sleep(4000);
                Assert(!process.HasExited, "wsl-launched DSH should remain running under wsl.exe");
                List<WslRunningInstance> detected = adapter.DetectRunning(distro);
                bool found = false;
                foreach (WslRunningInstance item in detected)
                    if (item.Port == instance.PreferredPort) { found = true; break; }
                Assert(found, "wsl detector should find the launched DSH port");
            }
            finally
            {
                adapter.Kill(process);
                process.WaitForExit(5000);
                process.Dispose();
                try { if (File.Exists(bridge.PatchPath)) File.Delete(bridge.PatchPath); } catch { }
            }
        }

        private static string[] GetWslDistros()
        {
            try
            {
                string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
                if (!File.Exists(wsl)) return new string[0];
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = wsl;
                    process.StartInfo.Arguments = "--list --quiet";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.Unicode;
                    if (!process.Start()) return new string[0];
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0) return new string[0];
                    return output.Replace("\0", String.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch
            {
                return new string[0];
            }
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
            RuntimeBridgeLaunch bridge = RuntimeBridgePatch.Create(instance, plugin);
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
                Assert(GracefulShutdownClient.Request(bridge, 3000, out shutdownError), "shutdown bridge failed: " + shutdownError);
                using (Process listener = Process.GetProcessById(running.ProcessId)) Assert(listener.WaitForExit(8000), "DSH did not exit after graceful request");
                Assert(PortMap.GetListenerProcessIds(port).Count == 0, "port remained occupied after graceful shutdown");

                InstanceController managerController = new InstanceController(instance, plugin, store, SilentManagerInteraction.Instance);
                managerController.Start(port, false);
                Assert(managerController.Ownership == InstanceOwnership.Managed, "manager-launched instance should be owned");
                Assert(managerController.ProcessId > 0, "manager-launched instance should record its PID");
                Assert(managerController.StartedAtUtc.HasValue, "manager-launched instance should record its start time");
                DateTime managedDeadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < managedDeadline && managerController.State != InstanceStateKind.Running)
                {
                    managerController.Tick();
                    if (managerController.State == InstanceStateKind.Error) break;
                    if (managerController.State == InstanceStateKind.Running) break;
                    Thread.Sleep(500);
                }
                Assert(managerController.State == InstanceStateKind.Running, "manager-launched DSH did not become ready");
                Assert(managerController.Ownership == InstanceOwnership.Managed, "ownership should remain managed while DSH runs");
                string managedStopError;
                Assert(managerController.TryGracefulStop(out managedStopError), "manager-owned graceful stop failed: " + managedStopError);
                Assert(managerController.State == InstanceStateKind.Stopped, "manager-owned instance did not return to stopped");
                Assert(managerController.Ownership == InstanceOwnership.Attached, "stopped instance should no longer claim managed ownership");
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
            instance.RuntimeType = InstanceModel.RuntimeTypeWindows;
            instance.WslDistro = String.Empty;
            instance.Frontend = InstanceModel.FrontendWeb;
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
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            string commandDirectory = Path.GetDirectoryName(command);
            if (path.IndexOf(commandDirectory, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("PATH", commandDirectory + ";" + path);
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

        private sealed class RecordingInteraction : IManagerInteraction
        {
            public int InformationCount;
            public int WarningCount;
            public int ErrorCount;
            public int ConfirmCount;

            public void Show(ManagerMessageKind kind, string message)
            {
                if (kind == ManagerMessageKind.Error) ErrorCount++;
                else if (kind == ManagerMessageKind.Warning) WarningCount++;
                else InformationCount++;
            }

            public bool Confirm(ManagerConfirmKind kind, string message, string title)
            {
                ConfirmCount++;
                return false;
            }

            public ConflictChoice ResolvePortConflict(PortInspection inspection, int alternatePort)
            {
                ConflictChoice choice = new ConflictChoice();
                choice.Action = ConflictAction.Cancel;
                return choice;
            }

            public bool ConfirmForceEnd(ProcessIdentity process)
            {
                return false;
            }

            public UpdateOutcome WaitForUpdate(string title, System.Threading.Tasks.Task<UpdateOutcome> updateTask)
            {
                return updateTask == null ? new UpdateOutcome { Error = "missing task" } : updateTask.Result;
            }
        }
    }
}
