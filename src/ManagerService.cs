using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class ManagerSnapshot
    {
        public string Language { get; set; }
        public bool TrayEnabled { get; set; }
        public bool StartWithWindows { get; set; }
        public bool DesktopShortcut { get; set; }
        public bool WslEnabled { get; set; }
        public string WslDefaultDistro { get; set; }
        public string DefaultInstanceId { get; set; }
        public List<InstanceSnapshot> Instances { get; set; }
    }

    public sealed class InstanceSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PluginId { get; set; }
        public string Profile { get; set; }
        public string Runtime { get; set; }
        public string RuntimeType { get; set; }
        public string WslDistro { get; set; }
        public string Ownership { get; set; }
        public string Frontend { get; set; }
        public int ProcessId { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public string Workspace { get; set; }
        public string WorkingDirectory { get; set; }
        public string DshHome { get; set; }
        public int PreferredPort { get; set; }
        public int ActivePort { get; set; }
        public InstanceStateKind State { get; set; }
        public string StatusText { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool UpdateCheckInProgress { get; set; }
        public string LastStartResult { get; set; }
        public string LastExitReason { get; set; }
        public bool IsDetected { get; set; }
        public bool IpcBridgeConnected { get; set; }
        public string BridgeState { get; set; }
        public string RuntimeBridgeVersion { get; set; }
        public int RuntimeBridgeProtocolVersion { get; set; }
        public string MarketplaceUrl { get; set; }
    }

    public sealed class UpdateCheckResult
    {
        public string InstanceId { get; set; }
        public UpdateInfo Info { get; set; }
        public string Error { get; set; }
        public bool InProgress { get; set; }
    }

    public interface IManagerService
    {
        event EventHandler Changed;
        event EventHandler ExitRequested;
        int ChangeVersion { get; }

        ManagerSnapshot GetSnapshot();
        void TickInstances();
        void Tick();
        void DrainNotifications();
        void RequestExit();
        void HandleInitialAction(string action);
        void StartAutomaticUpdateChecks();
        void Open(string instanceId);
        string OpenOrStartWsl();
        void Start(string instanceId);
        void Stop(string instanceId, bool confirm);
        void Restart(string instanceId);
        Task<UpdateCheckResult> CheckForUpdatesAsync(string instanceId, bool force);
        bool InstallUpdate(string instanceId);
        void SetLanguage(string language);
        string GetInstanceDetails(string instanceId);
        string GetDiagnosticsText();
        List<WslRunningInstance> DetectWslDsh();
        List<WindowsRunningInstance> DetectWindowsDsh();
        void RegisterDetectedWslInstance(WslRunningInstance item);
        void RegisterDetectedWindowsInstance(WindowsRunningInstance item);
        void RemoveDetectedInstance(string instanceId);
        void SaveDetectedInstance(string instanceId);
        void OpenConfiguration();
        void OpenManagerLogs();
        void OpenDshLogs(string instanceId);
        void OpenLogs();
        void OpenInstanceWorkspace(string instanceId);
        void OpenDshSettings(string instanceId);
        void OpenUrl(string url);
    }

    public sealed class ManagerService : IManagerService, IDisposable
    {
        private readonly ManagerConfig config;
        private readonly PluginCatalog catalog;
        private readonly ConfigurationStore configurationStore;
        private readonly UpdateManager updateManager;
        private readonly IManagerInteraction interaction;
        private readonly List<InstanceController> controllers;
        private readonly Dictionary<string, InstanceController> controllersById;
        private readonly Dictionary<string, DateTime> nextUpdateChecksUtc;
        private readonly HashSet<string> updateChecksInFlight;
        private readonly HashSet<string> detectedInstanceIds;
        private readonly object updateSync = new object();
        private int changeVersion;
        private bool disposed;

        public ManagerService(ManagerConfig managerConfig, PluginCatalog pluginCatalog, ConfigurationStore store, IManagerInteraction managerInteraction)
        {
            config = managerConfig;
            catalog = pluginCatalog;
            configurationStore = store;
            interaction = managerInteraction ?? SilentManagerInteraction.Instance;
            controllers = new List<InstanceController>();
            controllersById = new Dictionary<string, InstanceController>(StringComparer.OrdinalIgnoreCase);
            nextUpdateChecksUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            updateChecksInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            detectedInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            updateManager = new UpdateManager(store, config, interaction);

            foreach (InstanceConfig instance in config.Instances)
            {
                InstanceController controller = new InstanceController(instance, catalog.Get(instance.PluginId), store, interaction);
                controller.Changed += ControllerChanged;
                controllers.Add(controller);
                controllersById.Add(instance.Id, controller);
            }
        }

        public event EventHandler Changed;
        public event EventHandler ExitRequested;
        public int ChangeVersion { get { return changeVersion; } }

        public ManagerSnapshot GetSnapshot()
        {
            ManagerSnapshot snapshot = new ManagerSnapshot();
            snapshot.Language = config.Language ?? String.Empty;
            snapshot.TrayEnabled = !config.TrayEnabled.HasValue || config.TrayEnabled.Value;
            snapshot.StartWithWindows = config.StartWithWindows.HasValue && config.StartWithWindows.Value;
            snapshot.DesktopShortcut = config.DesktopShortcut.HasValue && config.DesktopShortcut.Value;
            snapshot.WslEnabled = config.WslEnabled.HasValue && config.WslEnabled.Value;
            snapshot.WslDefaultDistro = config.WslDefaultDistro ?? String.Empty;
            snapshot.DefaultInstanceId = config.DefaultInstanceId ?? String.Empty;
            snapshot.Instances = new List<InstanceSnapshot>();
            foreach (InstanceController controller in controllers)
            {
                InstanceSnapshot item = new InstanceSnapshot();
                item.Id = controller.Config.Id ?? String.Empty;
                item.Name = controller.Config.Name ?? String.Empty;
                item.PluginId = controller.Config.PluginId ?? String.Empty;
                item.Profile = controller.Config.Profile ?? String.Empty;
                item.Runtime = controller.Config.Runtime ?? String.Empty;
                item.RuntimeType = String.IsNullOrWhiteSpace(controller.Config.RuntimeType) ? InstanceModel.RuntimeTypeWindows : controller.Config.RuntimeType;
                item.WslDistro = controller.Config.WslDistro ?? String.Empty;
                item.Ownership = InstanceModel.ToText(controller.Ownership);
                item.Frontend = String.IsNullOrWhiteSpace(controller.Config.Frontend) ? InstanceModel.FrontendWeb : controller.Config.Frontend;
                item.ProcessId = controller.ProcessId;
                item.StartedAtUtc = controller.StartedAtUtc;
                item.Workspace = controller.Config.Workspace ?? String.Empty;
                item.WorkingDirectory = controller.ActiveRuntime == null
                    ? (controller.Config.Workspace ?? String.Empty)
                    : (controller.ActiveRuntime.WorkingDirectory ?? controller.Config.Workspace ?? String.Empty);
                item.DshHome = controller.Config.DshHome ?? String.Empty;
                item.PreferredPort = controller.Config.PreferredPort;
                item.ActivePort = controller.ActivePort;
                item.State = controller.State;
                item.StatusText = controller.StatusText ?? String.Empty;
                item.InstalledVersion = controller.InstalledVersion ?? String.Empty;
                item.LatestVersion = controller.UpdateInfo == null ? String.Empty : (controller.UpdateInfo.LatestVersion ?? String.Empty);
                item.UpdateAvailable = controller.UpdateInfo != null && controller.UpdateInfo.UpdateAvailable;
                item.UpdateCheckInProgress = IsUpdateCheckInFlight(controller.Config.Id);
                item.LastStartResult = controller.LastStartResult ?? String.Empty;
                item.LastExitReason = controller.LastExitReason ?? String.Empty;
                item.IsDetected = detectedInstanceIds.Contains(controller.Config.Id);
                item.IpcBridgeConnected = controller.IpcBridgeConnected;
                item.BridgeState = controller.BridgeRuntime == null ? String.Empty : (controller.BridgeRuntime.State ?? String.Empty);
                item.RuntimeBridgeVersion = controller.BridgeRuntime == null ? String.Empty : (controller.BridgeRuntime.RuntimeBridgeVersion ?? String.Empty);
                item.RuntimeBridgeProtocolVersion = controller.Plugin != null && controller.Plugin.RuntimeBridge != null
                    ? controller.Plugin.RuntimeBridge.BridgeProtocolVersion
                    : 0;
                item.MarketplaceUrl = controller.Plugin == null ? String.Empty : (controller.Plugin.MarketplaceUrl ?? String.Empty);
                snapshot.Instances.Add(item);
            }
            return snapshot;
        }

        public void TickInstances()
        {
            foreach (InstanceController controller in controllers) controller.Tick();
        }

        public void Tick()
        {
            TickInstances();

            DateTime now = DateTime.UtcNow;
            foreach (InstanceController controller in controllers)
            {
                DateTime next;
                bool due = false;
                lock (updateSync)
                {
                    if (nextUpdateChecksUtc.TryGetValue(controller.Config.Id, out next) && now >= next) due = true;
                }
                if (due) CheckForUpdatesAsync(controller.Config.Id, false);
            }
        }

        public void DrainNotifications()
        {
            foreach (InstanceController controller in controllers) controller.DrainBridgeMessages();
        }

        public void RequestExit()
        {
            RaiseExitRequested();
        }

        public void HandleInitialAction(string action)
        {
            action = String.IsNullOrWhiteSpace(action) ? "tray" : action;
            if (String.Equals(action, "start", StringComparison.OrdinalIgnoreCase)) Start(null);
            else if (String.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                Stop(null, false);
                RaiseExitRequested();
            }
            else if (String.Equals(action, "restart", StringComparison.OrdinalIgnoreCase)) Restart(null);
            else if (String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)) RaiseExitRequested();
            else if (String.Equals(action, "tray", StringComparison.OrdinalIgnoreCase))
            {
                // Shortcut / autostart / direct EXE launch: show the tray only.
                // Do not start DSH or open a browser until the user chooses an action.
            }
            else Open(null);
        }

        public void StartAutomaticUpdateChecks()
        {
            foreach (InstanceController controller in controllers) CheckForUpdatesAsync(controller.Config.Id, false);
        }

        public void Open(string instanceId)
        {
            GetController(instanceId).OpenOrStart();
        }

        public string OpenOrStartWsl()
        {
            InstanceController wslController = null;
            foreach (InstanceController controller in controllers)
            {
                if (String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
                {
                    wslController = controller;
                    break;
                }
            }
            if (wslController == null)
            {
                string distro = String.IsNullOrWhiteSpace(config.WslDefaultDistro) ? null : config.WslDefaultDistro;
                if (String.IsNullOrWhiteSpace(distro))
                {
                    List<string> distros = WslRuntimeAdapter.DetectDistros();
                    distro = WslRuntimeAdapter.SelectPreferredDistro(null, distros, WslRuntimeAdapter.DetectDistroStates());
                    if (String.IsNullOrWhiteSpace(distro))
                    {
                        if (distros.Count == 0) throw new InvalidOperationException("No WSL distros were detected.");
                        List<string> candidates = WslRuntimeAdapter.GetUserWslDistros(distros);
                        if (candidates.Count == 0)
                            throw new InvalidOperationException("WSL only reports container-runtime distros (" + String.Join(", ", distros.ToArray()) + "). Install a general-purpose WSL distribution such as Ubuntu, then try again.");
                        throw new InvalidOperationException("Multiple WSL distros were detected (" + String.Join(", ", candidates.ToArray()) + "). Run dsh-windows-manager wsl enable --distro <name> first.");
                    }
                }
                int port = ChooseWslPreferredPort();
                PluginDefinition plugin = null;
                foreach (PluginDefinition candidate in catalog.All) { plugin = candidate; break; }
                if (plugin == null) throw new InvalidOperationException("No plugin definitions are available.");
                InstanceConfig instance = new InstanceConfig();
                instance.Id = "wsl-web";
                instance.Name = "WSL " + distro;
                instance.PluginId = plugin.Id;
                instance.Profile = "web";
                instance.Runtime = "global";
                instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
                instance.WslDistro = distro;
                instance.Frontend = InstanceModel.FrontendWeb;
                instance.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                instance.DshHome = String.Empty;
                instance.PreferredPort = port;
                instance.PinnedVersion = plugin.Update == null ? String.Empty : plugin.Update.BundledVersion;
                config.WslEnabled = true;
                config.WslDefaultDistro = distro;
                config.Instances.Add(instance);
                configurationStore.Save(config);
                wslController = new InstanceController(instance, plugin, configurationStore, interaction);
                wslController.Changed += ControllerChanged;
                controllers.Add(wslController);
                controllersById.Add(instance.Id, wslController);
            }
            wslController.OpenOrStart();
            RaiseChanged();
            return wslController.Config.Id;
        }

        private int ChooseWslPreferredPort()
        {
            int port;
            for (port = 3088; port <= 3099; port++)
            {
                bool configured = false;
                foreach (InstanceConfig instance in config.Instances)
                    if (instance.PreferredPort == port) configured = true;
                if (!configured && PortMap.GetListenerProcessIds(port).Count == 0) return port;
            }
            throw new InvalidOperationException("No free WSL port was found near 3080.");
        }

        public void Start(string instanceId)
        {
            InstanceController controller = GetController(instanceId);
            controller.Start(controller.Config.PreferredPort, false);
        }

        public void Stop(string instanceId, bool confirm)
        {
            GetController(instanceId).Stop(confirm);
        }

        public void Restart(string instanceId)
        {
            GetController(instanceId).Restart();
        }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(string instanceId, bool force)
        {
            InstanceController controller = GetController(instanceId);
            string id = controller.Config.Id;
            lock (updateSync)
            {
                if (updateChecksInFlight.Contains(id))
                {
                    UpdateCheckResult alreadyRunning = new UpdateCheckResult();
                    alreadyRunning.InstanceId = id;
                    alreadyRunning.InProgress = true;
                    return Task.FromResult(alreadyRunning);
                }
                updateChecksInFlight.Add(id);
            }
            RaiseChanged();
            return RunUpdateCheckAsync(controller, force);
        }

        public bool InstallUpdate(string instanceId)
        {
            bool succeeded = updateManager.ExecuteConfirmedUpdate(GetController(instanceId));
            RaiseChanged();
            return succeeded;
        }

        public void SetLanguage(string language)
        {
            config.Language = language;
            configurationStore.Save(config);
            Localization.Initialize(language);
            RaiseChanged();
        }

        public string GetInstanceDetails(string instanceId)
        {
            return GetController(instanceId).GetDetails();
        }

        public List<WindowsRunningInstance> DetectWindowsDsh()
        {
            List<WindowsRunningInstance> result = new List<WindowsRunningInstance>();
            WindowsRuntimeAdapter adapter = new WindowsRuntimeAdapter();
            foreach (PluginDefinition plugin in catalog.All)
            {
                List<WindowsRunningInstance> detected = adapter.DetectRunning(plugin);
                foreach (WindowsRunningInstance item in detected) result.Add(item);
            }
            return result;
        }

        public List<WslRunningInstance> DetectWslDsh()
        {
            List<WslRunningInstance> result = new List<WslRunningInstance>();
            HashSet<string> distros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.WslEnabled.HasValue && config.WslEnabled.Value && !String.IsNullOrWhiteSpace(config.WslDefaultDistro))
                distros.Add(config.WslDefaultDistro);
            foreach (InstanceConfig instance in config.Instances)
            {
                if (String.Equals(instance.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase) &&
                    !String.IsNullOrWhiteSpace(instance.WslDistro))
                    distros.Add(instance.WslDistro);
            }
            if (distros.Count == 0)
            {
                foreach (string distro in WslRuntimeAdapter.DetectDistros())
                {
                    if (WslRuntimeAdapter.IsUserWslDistro(distro)) distros.Add(distro);
                }
            }
            WslRuntimeAdapter adapter = new WslRuntimeAdapter();
            foreach (string distro in distros)
            {
                List<WslRunningInstance> detected = adapter.DetectRunning(distro);
                foreach (WslRunningInstance item in detected) result.Add(item);
            }
            return result;
        }

        public void RegisterDetectedWindowsInstance(WindowsRunningInstance item)
        {
            if (item == null || item.Pid <= 0 || item.Port <= 0) return;
            foreach (InstanceController existingController in controllers)
            {
                if (String.Equals(existingController.Config.RuntimeType, InstanceModel.RuntimeTypeWindows, StringComparison.OrdinalIgnoreCase) &&
                    existingController.Config.PreferredPort == item.Port)
                {
                    existingController.AdoptDetectedProcess(item.Pid);
                    detectedInstanceIds.Add(existingController.Config.Id);
                    RaiseChanged();
                    return;
                }
            }
            PluginDefinition plugin = null;
            foreach (PluginDefinition candidate in catalog.All) { plugin = candidate; break; }
            if (plugin == null) throw new InvalidOperationException("No plugin definitions are available.");
            InstanceConfig instance = new InstanceConfig();
            instance.Id = "windows-detected-" + item.Port;
            instance.Name = "DSH " + item.Port;
            instance.PluginId = plugin.Id;
            instance.Profile = "web";
            instance.Runtime = "auto";
            instance.RuntimeType = InstanceModel.RuntimeTypeWindows;
            instance.WslDistro = String.Empty;
            instance.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            instance.DshHome = String.Empty;
            instance.PreferredPort = item.Port;
            instance.PinnedVersion = plugin.Update == null ? String.Empty : plugin.Update.BundledVersion;
            InstanceController detectedController = new InstanceController(instance, plugin, configurationStore, interaction);
            detectedController.Changed += ControllerChanged;
            detectedController.AdoptDetectedProcess(item.Pid);
            controllers.Add(detectedController);
            controllersById.Add(instance.Id, detectedController);
            detectedInstanceIds.Add(instance.Id);
            RaiseChanged();
        }

        public void RegisterDetectedWslInstance(WslRunningInstance item)
        {
            if (item == null || item.Pid <= 0 || item.Port <= 0 || String.IsNullOrWhiteSpace(item.Distro)) return;
            foreach (InstanceController existingController in controllers)
            {
                if (String.Equals(existingController.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(existingController.Config.WslDistro, item.Distro, StringComparison.OrdinalIgnoreCase) &&
                    existingController.Config.PreferredPort == item.Port)
                {
                    existingController.AdoptDetectedProcess(item.Pid);
                    detectedInstanceIds.Add(existingController.Config.Id);
                    RaiseChanged();
                    return;
                }
            }

            PluginDefinition plugin = null;
            foreach (PluginDefinition candidate in catalog.All) { plugin = candidate; break; }
            if (plugin == null) throw new InvalidOperationException("No plugin definitions are available.");
            InstanceConfig instance = new InstanceConfig();
            instance.Id = "wsl-detected-" + AppPaths.SafeFileName(item.Distro) + "-" + item.Port;
            instance.Name = "WSL " + item.Distro;
            instance.PluginId = plugin.Id;
            instance.Profile = "web";
            instance.Runtime = "global";
            instance.RuntimeType = InstanceModel.RuntimeTypeWsl;
            instance.WslDistro = item.Distro;
            instance.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            instance.DshHome = String.Empty;
            instance.PreferredPort = item.Port;
            instance.PinnedVersion = plugin.Update == null ? String.Empty : plugin.Update.BundledVersion;
            InstanceController detectedController = new InstanceController(instance, plugin, configurationStore, interaction);
            detectedController.Changed += ControllerChanged;
            detectedController.AdoptDetectedProcess(item.Pid);
            controllers.Add(detectedController);
            controllersById.Add(instance.Id, detectedController);
            detectedInstanceIds.Add(instance.Id);
            RaiseChanged();
        }

        public void RemoveDetectedInstance(string instanceId)
        {
            if (!detectedInstanceIds.Contains(instanceId)) return;
            InstanceController controller;
            if (!controllersById.TryGetValue(instanceId, out controller)) return;
            controller.Changed -= ControllerChanged;
            controller.Close();
            controllers.Remove(controller);
            controllersById.Remove(instanceId);
            detectedInstanceIds.Remove(instanceId);
            RaiseChanged();
        }

        public void SaveDetectedInstance(string instanceId)
        {
            if (!detectedInstanceIds.Contains(instanceId)) return;
            InstanceController controller;
            if (!controllersById.TryGetValue(instanceId, out controller)) return;
            foreach (InstanceConfig existing in config.Instances)
            {
                if (String.Equals(existing.Id, instanceId, StringComparison.OrdinalIgnoreCase)) return;
            }
            if (String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                config.WslEnabled = true;
                if (String.IsNullOrWhiteSpace(config.WslDefaultDistro))
                    config.WslDefaultDistro = controller.Config.WslDistro ?? String.Empty;
            }
            config.Instances.Add(controller.Config);
            configurationStore.Save(config);
            detectedInstanceIds.Remove(instanceId);
            RaiseChanged();
        }

        public string GetDiagnosticsText()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            ManagerSnapshot snapshot = GetSnapshot();
            text.AppendLine(Localization.Text("Diagnostics.ManagerVersion") + ": " + ManagerControlProtocol.ManagerVersion());
            text.AppendLine(Localization.Text("Diagnostics.ManagerPid") + ": " + System.Diagnostics.Process.GetCurrentProcess().Id);
            text.AppendLine(Localization.Text("Diagnostics.TrayEnabled") + ": " + (snapshot.TrayEnabled ? "true" : "false"));
            text.AppendLine(Localization.Text("Diagnostics.DataDirectory") + ": " + AppPaths.DataDirectory);
            text.AppendLine(Localization.Text("Diagnostics.ManagerLog") + ": " + AppPaths.ManagerLog);
            foreach (InstanceSnapshot instance in snapshot.Instances)
            {
                text.AppendLine();
                text.AppendLine(Localization.Text("Diagnostics.Instance") + ": " + instance.Name + " (" + instance.Id + ")");
                text.AppendLine(Localization.Text("Diagnostics.State") + ": " + instance.State);
                text.AppendLine(Localization.Text("Diagnostics.RuntimeType") + ": " + instance.RuntimeType);
                text.AppendLine(Localization.Text("Diagnostics.Ownership") + ": " + instance.Ownership);
                text.AppendLine(Localization.Text("Diagnostics.Pid") + ": " + instance.ProcessId);
                text.AppendLine(Localization.Text("Diagnostics.Port") + ": " + (instance.ActivePort > 0 ? instance.ActivePort : instance.PreferredPort));
                text.AppendLine(Localization.Text("Diagnostics.DshVersion") + ": " + (String.IsNullOrWhiteSpace(instance.InstalledVersion) ? Localization.Text("Version.Unknown") : instance.InstalledVersion));
                text.AppendLine(Localization.Text("Diagnostics.Frontend") + ": " + instance.Frontend);
                text.AppendLine(Localization.Text("Diagnostics.DshHome") + ": " + (String.IsNullOrWhiteSpace(instance.DshHome) ? Localization.Text("Diagnostics.DshHomeDefault") : instance.DshHome));
                text.AppendLine(Localization.Text("Diagnostics.WorkingDirectory") + ": " + instance.WorkingDirectory);
                text.AppendLine(Localization.Text("Diagnostics.RuntimeBridgeState") + ": " + (String.IsNullOrWhiteSpace(instance.BridgeState) ? Localization.Text("Diagnostics.None") : instance.BridgeState));
                text.AppendLine(Localization.Text("Diagnostics.RuntimeBridgeVersion") + ": " + (String.IsNullOrWhiteSpace(instance.RuntimeBridgeVersion) ? Localization.Text("Diagnostics.None") : instance.RuntimeBridgeVersion));
                text.AppendLine(Localization.Text("Diagnostics.RuntimeBridgeProtocolVersion") + ": " + instance.RuntimeBridgeProtocolVersion);
                text.AppendLine(Localization.Text("Diagnostics.StartedAt") + ": " + (instance.StartedAtUtc.HasValue ? instance.StartedAtUtc.Value.ToString("o") : Localization.Text("Diagnostics.None")));
                text.AppendLine(Localization.Text("Diagnostics.LastStartResult") + ": " + (String.IsNullOrWhiteSpace(instance.LastStartResult) ? Localization.Text("Diagnostics.None") : instance.LastStartResult));
                text.AppendLine(Localization.Text("Diagnostics.LastExitReason") + ": " + (String.IsNullOrWhiteSpace(instance.LastExitReason) ? Localization.Text("Diagnostics.None") : instance.LastExitReason));
            }
            return text.ToString().TrimEnd();
        }

        public void OpenManagerLogs()
        {
            OpenFile(AppPaths.ManagerLog);
        }

        public void OpenDshLogs(string instanceId)
        {
            InstanceController controller = GetController(instanceId);
            if (String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                string wslLogs = Path.Combine(AppPaths.LogDirectory, "wsl", AppPaths.SafeFileName(controller.Config.Id));
                OpenFolder(wslLogs);
                return;
            }
            OpenFolder(AppPaths.LogDirectory);
        }

        public void OpenConfiguration()
        {
            OpenConfigurationFile(AppPaths.ConfigFile);
        }

        public void OpenLogs()
        {
            OpenFolder(AppPaths.LogDirectory);
        }

        public void OpenInstanceWorkspace(string instanceId)
        {
            OpenFolder(GetController(instanceId).Config.Workspace);
        }

        public void OpenDshSettings(string instanceId)
        {
            InstanceController controller = GetController(instanceId);
            if (String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                string windowsDirectory = ResolveWslSettingsDirectory(controller);
                if (!String.IsNullOrWhiteSpace(windowsDirectory)) OpenFolder(windowsDirectory);
                return;
            }
            OpenFolder(Path.GetDirectoryName(AppPaths.DshSettingsFile(controller.Config)));
        }

        private static string ResolveWslSettingsDirectory(InstanceController controller)
        {
            string distro = controller.Config.WslDistro ?? String.Empty;
            if (String.IsNullOrWhiteSpace(distro)) return String.Empty;
            string linuxHome = null;
            if (controller.BridgeRuntime != null && !String.IsNullOrWhiteSpace(controller.BridgeRuntime.DshHome))
                linuxHome = controller.BridgeRuntime.DshHome;
            if (String.IsNullOrWhiteSpace(linuxHome))
            {
                string configured = controller.Config.DshHome ?? String.Empty;
                if (!String.IsNullOrWhiteSpace(configured))
                {
                    if (configured.StartsWith("~", StringComparison.Ordinal))
                    {
                        string home = WslRuntimeAdapter.ResolveLinuxHome(distro);
                        linuxHome = configured == "~" ? home : home + configured.Substring(1);
                    }
                    else if (configured.IndexOf(':') == 1 || configured.StartsWith("\\", StringComparison.Ordinal))
                    {
                        linuxHome = WslRuntimeAdapter.ConvertToWslPath(distro, configured);
                    }
                    else
                    {
                        linuxHome = configured;
                    }
                }
            }
            if (String.IsNullOrWhiteSpace(linuxHome))
            {
                string home = WslRuntimeAdapter.ResolveLinuxHome(distro);
                linuxHome = home + "/.dsh";
            }
            string linuxSettingsPath = linuxHome.TrimEnd('/') + "/settings.yaml";
            string windowsSettingsPath = WslRuntimeAdapter.ConvertToWindowsPath(distro, linuxSettingsPath);
            return String.IsNullOrWhiteSpace(windowsSettingsPath) ? String.Empty : Path.GetDirectoryName(windowsSettingsPath);
        }

        public void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                FileLog.Error("Could not open URL " + url + ": " + exception.Message);
                interaction.Show(ManagerMessageKind.Warning, Localization.Format("Dialog.OpenUrlFailed", url, exception.Message));
            }
        }

        public void Close()
        {
            if (disposed) return;
            disposed = true;
            foreach (InstanceController controller in controllers) controller.Close();
        }

        public void Dispose()
        {
            Close();
        }

        private InstanceController GetController(string instanceId)
        {
            if (String.IsNullOrWhiteSpace(instanceId)) instanceId = config.DefaultInstanceId;
            InstanceController controller;
            if (!String.IsNullOrWhiteSpace(instanceId) && controllersById.TryGetValue(instanceId, out controller)) return controller;
            if (controllers.Count > 0) return controllers[0];
            throw new InvalidOperationException("No configured instances are available.");
        }

        private async Task<UpdateCheckResult> RunUpdateCheckAsync(InstanceController controller, bool force)
        {
            string id = controller.Config.Id;
            try
            {
                UpdateInfo info = await Task.Run(delegate { return updateManager.Check(controller, force); });
                controller.UpdateInfo = info;
                controller.InstalledVersion = info.InstalledVersion;
                SetNextUpdateCheckUtc(id, UpdateManager.NextAutomaticCheckUtc(info.CheckedAtUtc));
                RaiseChanged();
                UpdateCheckResult result = new UpdateCheckResult();
                result.InstanceId = id;
                result.Info = info;
                return result;
            }
            catch (Exception exception)
            {
                FileLog.Warn("Update check failed for " + id + ": " + exception.Message);
                SetNextUpdateCheckUtc(id, DateTime.UtcNow.AddHours(24));
                RaiseChanged();
                UpdateCheckResult result = new UpdateCheckResult();
                result.InstanceId = id;
                result.Error = exception.Message;
                return result;
            }
            finally
            {
                lock (updateSync) updateChecksInFlight.Remove(id);
            }
        }

        private void SetNextUpdateCheckUtc(string instanceId, DateTime nextUtc)
        {
            lock (updateSync) nextUpdateChecksUtc[instanceId] = nextUtc;
        }

        private bool IsUpdateCheckInFlight(string instanceId)
        {
            lock (updateSync) return updateChecksInFlight.Contains(instanceId);
        }

        private void ControllerChanged(object sender, EventArgs args)
        {
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            changeVersion++;
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseExitRequested()
        {
            EventHandler handler = ExitRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static void OpenFolder(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", CommandRunner.QuoteArgument(path)) { UseShellExecute = true });
        }

        private void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    OpenFolder(Path.GetDirectoryName(path));
                    interaction.Show(ManagerMessageKind.Information, Localization.Format("Dialog.ConfigFileMissing", path));
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", CommandRunner.QuoteArgument(path)) { UseShellExecute = true });
                }
                catch (Exception fallbackException)
                {
                    FileLog.Error("Could not open log file " + path + ": " + exception.Message + "; Notepad fallback: " + fallbackException.Message);
                    interaction.Show(ManagerMessageKind.Warning, Localization.Format("Dialog.OpenFileFailed", path, fallbackException.Message));
                }
            }
        }

        private void OpenConfigurationFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    OpenFolder(Path.GetDirectoryName(path));
                    interaction.Show(ManagerMessageKind.Information, Localization.Format("Dialog.ConfigFileMissing", path));
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", CommandRunner.QuoteArgument(path)) { UseShellExecute = true });
                }
                catch (Exception fallbackException)
                {
                    FileLog.Error("Could not open configuration file " + path + ": " + exception.Message + "; Notepad fallback: " + fallbackException.Message);
                    interaction.Show(ManagerMessageKind.Warning, Localization.Format("Dialog.OpenFileFailed", path, fallbackException.Message));
                }
            }
        }
    }
}