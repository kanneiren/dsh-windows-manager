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

        ManagerSnapshot GetSnapshot();
        void TickInstances();
        void Tick();
        void DrainNotifications();
        void RequestExit();
        void HandleInitialAction(string action);
        void StartAutomaticUpdateChecks();
        void Open(string instanceId);
        void Start(string instanceId);
        void Stop(string instanceId, bool confirm);
        void Restart(string instanceId);
        Task<UpdateCheckResult> CheckForUpdatesAsync(string instanceId, bool force);
        bool InstallUpdate(string instanceId);
        void SetLanguage(string language);
        string GetInstanceDetails(string instanceId);
        string GetDiagnosticsText();
        void OpenConfiguration();
        void OpenManagerLogs();
        void OpenDshLogs();
        void OpenLogs();
        void OpenInstanceWorkspace(string instanceId);
        void OpenDshSettings(string instanceId);
        void OpenUrl(string url);
    }

    public sealed class ManagerService : IManagerService, IDisposable
    {
        private readonly ManagerConfig config;
        private readonly ConfigurationStore configurationStore;
        private readonly UpdateManager updateManager;
        private readonly IManagerInteraction interaction;
        private readonly List<InstanceController> controllers;
        private readonly Dictionary<string, InstanceController> controllersById;
        private readonly Dictionary<string, DateTime> nextUpdateChecksUtc;
        private readonly HashSet<string> updateChecksInFlight;
        private readonly object updateSync = new object();
        private bool disposed;

        public ManagerService(ManagerConfig managerConfig, PluginCatalog catalog, ConfigurationStore store, IManagerInteraction managerInteraction)
        {
            config = managerConfig;
            configurationStore = store;
            interaction = managerInteraction ?? SilentManagerInteraction.Instance;
            controllers = new List<InstanceController>();
            controllersById = new Dictionary<string, InstanceController>(StringComparer.OrdinalIgnoreCase);
            nextUpdateChecksUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            updateChecksInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        public ManagerSnapshot GetSnapshot()
        {
            ManagerSnapshot snapshot = new ManagerSnapshot();
            snapshot.Language = config.Language ?? String.Empty;
            snapshot.TrayEnabled = !config.TrayEnabled.HasValue || config.TrayEnabled.Value;
            snapshot.StartWithWindows = config.StartWithWindows.HasValue && config.StartWithWindows.Value;
            snapshot.DesktopShortcut = config.DesktopShortcut.HasValue && config.DesktopShortcut.Value;
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
            action = String.IsNullOrWhiteSpace(action) ? "open" : action;
            if (String.Equals(action, "start", StringComparison.OrdinalIgnoreCase)) Start(null);
            else if (String.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                Stop(null, false);
                RaiseExitRequested();
            }
            else if (String.Equals(action, "restart", StringComparison.OrdinalIgnoreCase)) Restart(null);
            else if (String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)) RaiseExitRequested();
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

        public void OpenDshLogs()
        {
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
            OpenFolder(Path.GetDirectoryName(AppPaths.DshSettingsFile(GetController(instanceId).Config)));
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