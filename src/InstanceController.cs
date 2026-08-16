using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class InstanceController
    {
        private readonly ConfigurationStore configurationStore;
        private readonly IManagerInteraction interaction;
        private readonly List<Regex> processPatterns;
        private readonly object bridgeSync = new object();
        private readonly Queue<BridgeMessage> bridgeMessages = new Queue<BridgeMessage>();
        private PersistedInstanceState persistedState;
        private volatile IRuntimeProcess launchProcess;
        private IRuntimeAdapter activeAdapter;
        private volatile RuntimeBridgeLaunch bridgeLaunch;
        private DateTime startDeadlineUtc;
        private bool openWhenReady;
        private int startingPort;
        private int savedPort;
        private int savedProcessId;
        private DateTime nextInspectionUtc;
        private DateTime nextBridgeConnectUtc = DateTime.MinValue;
        private ProcessIdentity cachedProcessIdentity;
        private volatile IpcBridgeConnection bridge;
        private volatile BridgeRuntimeInfo bridgeStatus;
        private Task bridgeConnectTask;
        private ProcessIdentity launchIdentity;
        private volatile int bridgeConnectAttempts;
        private volatile int lifecycleGeneration;
        private volatile string bridgeError = String.Empty;
        private volatile bool bridgeProtocolIncompatible;
        private volatile bool managedProcessExited;
        private bool managedProcessExitHandled;
        private bool processExitJustHandled;
        private bool stopExpected;
        private InstanceOwnership ownership;
        private DateTime? startedAtUtc;

        public InstanceController(InstanceConfig config, PluginDefinition plugin, ConfigurationStore store)
            : this(config, plugin, store, SilentManagerInteraction.Instance)
        {
        }

        public InstanceController(InstanceConfig config, PluginDefinition plugin, ConfigurationStore store, IManagerInteraction managerInteraction)
        {
            Config = config;
            Plugin = plugin;
            configurationStore = store;
            interaction = managerInteraction ?? SilentManagerInteraction.Instance;
            processPatterns = new List<Regex>();
            if (plugin.ProcessPatterns != null)
            {
                foreach (string pattern in plugin.ProcessPatterns)
                    processPatterns.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            persistedState = store.ReadState(config.Id);
            if (persistedState != null)
            {
                ActivePort = persistedState.Port;
                savedPort = persistedState.Port;
                savedProcessId = persistedState.ProcessId;
                bridgeLaunch = new RuntimeBridgeLaunch();
                bridgeLaunch.PipeName = persistedState.PipeName;
                bridgeLaunch.Transport = persistedState.Transport;
                bridgeLaunch.Host = persistedState.Host;
                bridgeLaunch.Port = persistedState.BridgePort;
                bridgeLaunch.Token = persistedState.PipeToken;
                bridgeLaunch.PatchPath = persistedState.PatchPath;
            }
            ownership = ResolvePersistedOwnership(persistedState);
            startedAtUtc = ParseUtc(persistedState == null ? null : persistedState.StartedAtUtc);
            ProcessId = persistedState == null ? 0 : persistedState.ProcessId;
            State = InstanceStateKind.Stopped;
            StatusText = "Stopped";
            InstalledVersion = RuntimeAdapters.ResolveInstalledVersion(config, plugin);
        }

        public InstanceConfig Config { get; private set; }
        public PluginDefinition Plugin { get; private set; }
        public InstanceOwnership Ownership { get { return ownership; } }
        public DateTime? StartedAtUtc { get { return startedAtUtc; } }
        public int ProcessId { get; private set; }
        public InstanceStateKind State { get; private set; }
        public string StatusText { get; private set; }
        public string LastError { get; private set; }
        public string LastStartResult { get; private set; }
        public string LastExitReason { get; private set; }
        public string InstalledVersion { get; set; }
        public UpdateInfo UpdateInfo { get; set; }
        public int ActivePort { get; private set; }
        public PortInspection LastInspection { get; private set; }
        public RuntimeResolution ActiveRuntime { get; private set; }
        public BridgeRuntimeInfo BridgeRuntime { get { return bridgeStatus; } }
        public bool IpcBridgeConnected { get { return bridge != null && bridge.IsConnected; } }
        public string IpcBridgeError { get { return bridgeError ?? String.Empty; } }
        public event EventHandler Changed;

        public void Tick()
        {
            DrainBridgeMessages();
            if (processExitJustHandled)
            {
                processExitJustHandled = false;
                return;
            }
            if (State == InstanceStateKind.Updating) return;

            if (State == InstanceStateKind.Stopping)
            {
                if (launchProcess != null)
                {
                    if (IsLaunchProcessExited()) FinishStop();
                    return;
                }
            }

            if (State == InstanceStateKind.Starting && startingPort > 0)
            {
                if (TryCompleteStartFromBridge()) return;

                bool exited = IsLaunchProcessExited();
                if (exited || DateTime.UtcNow > startDeadlineUtc)
                {
                    string detail = ReadErrorTail();
                    FailStart("DeepSeek Harness did not become ready." + (detail.Length == 0 ? String.Empty : " " + detail));
                    return;
                }

                if (!IpcBridgeConnected)
                {
                    EnsureBridgeConnection();
                    DateTime now = DateTime.UtcNow;
                    if (now < nextInspectionUtc)
                    {
                        SetState(InstanceStateKind.Starting, "Starting on port " + startingPort);
                        return;
                    }
                    nextInspectionUtc = now.AddMilliseconds(InspectionIntervalMilliseconds(State));

                    PortInspection inspection = InspectPort(startingPort);
                    LastInspection = inspection;
                    if (inspection.Kind == InstanceStateKind.Running)
                    {
                        CompleteStart(inspection);
                        return;
                    }
                    if (inspection.Kind == InstanceStateKind.Conflict)
                    {
                        FailStart("Port " + startingPort + " was taken by another process during startup.");
                        return;
                    }
                }
                SetState(InstanceStateKind.Starting, "Starting on port " + startingPort);
                return;
            }

            if (State == InstanceStateKind.Running && IpcBridgeConnected)
            {
                EnsureBridgeConnection();
                return;
            }

            if (State == InstanceStateKind.Running && launchProcess != null)
            {
                // Manager-owned process: the Process.Exited event is the
                // authoritative liveness signal. Reconnect IPC in the
                // background; while it is unavailable, run only the bounded
                // fallback discovery cadence instead of a tight poll.
                EnsureBridgeConnection();
                if (IpcBridgeConnected) return;
                DateTime managerOwnedNow = DateTime.UtcNow;
                if (managerOwnedNow < nextInspectionUtc) return;
                nextInspectionUtc = managerOwnedNow.AddMilliseconds(InspectionIntervalMilliseconds(State));
                PortInspection fallback = FindCurrentInspection();
                LastInspection = fallback;
                if (fallback.Kind == InstanceStateKind.Running)
                {
                    ActivePort = fallback.Port;
                    SaveRunningState(fallback);
                }
                else if (fallback.Kind == InstanceStateKind.Conflict)
                {
                    ActivePort = 0;
                    SetState(InstanceStateKind.Conflict, "Port " + fallback.Port + " is occupied by " + SafeProcessName(fallback.Process));
                }
                return;
            }

            if (State == InstanceStateKind.Running && bridgeLaunch != null && !bridgeProtocolIncompatible)
            {
                EnsureBridgeConnection();
            }

            DateTime stableNow = DateTime.UtcNow;
            if (stableNow < nextInspectionUtc) return;
            nextInspectionUtc = stableNow.AddMilliseconds(InspectionIntervalMilliseconds(State));

            PortInspection current = FindCurrentInspection();
            LastInspection = current;
            if (current.Kind == InstanceStateKind.Running)
            {
                ActivePort = current.Port;
                SaveRunningState(current);
                SetState(InstanceStateKind.Running, "Running on port " + current.Port);
                if (bridgeLaunch != null && !bridgeProtocolIncompatible) EnsureBridgeConnection();
            }
            else if (current.Kind == InstanceStateKind.Starting)
            {
                ApplyInspectionOwnership(current);
                ActivePort = current.Port;
                SetState(InstanceStateKind.Starting, "External instance is starting on port " + current.Port);
            }
            else if (current.Kind == InstanceStateKind.Conflict)
            {
                ActivePort = 0;
                ProcessId = current.ProcessId;
                ownership = InstanceOwnership.Attached;
                SetState(InstanceStateKind.Conflict, "Port " + current.Port + " is occupied by " + SafeProcessName(current.Process));
            }
            else
            {
                ActivePort = 0;
                if (persistedState != null)
                {
                    configurationStore.DeleteState(Config.Id);
                    persistedState = null;
                    bridgeLaunch = null;
                    savedPort = 0;
                    savedProcessId = 0;
                }
                ProcessId = 0;
                startedAtUtc = null;
                ownership = InstanceOwnership.Attached;
                SetState(InstanceStateKind.Stopped, "Stopped");
            }
        }

        public PortInspection InspectPort(int port)
        {
            if (String.Equals(Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                WslRuntimeAdapter adapter = RuntimeAdapters.Get(Config) as WslRuntimeAdapter;
                if (adapter == null) throw new InvalidOperationException("The WSL runtime adapter is unavailable.");
                PortInspection wslInspection = adapter.InspectPort(Config, Plugin, port, launchProcess != null);
                if (launchProcess != null && wslInspection.HttpVerified)
                {
                    wslInspection.ProcessId = launchProcess.ProcessId;
                    wslInspection.Process = launchIdentity;
                    wslInspection.ProcessVerified = true;
                }
                return wslInspection;
            }

            int processId = PortMap.GetPreferredListenerProcessId(port);
            if (processId == 0)
            {
                cachedProcessIdentity = null;
                PortInspection free = new PortInspection();
                free.Kind = InstanceStateKind.Stopped;
                free.Port = port;
                free.Detail = "Port is free";
                return free;
            }
            ProcessIdentity basic = ProcessInspector.GetBasic(processId);
            ProcessIdentity process;
            if (cachedProcessIdentity != null && !String.IsNullOrWhiteSpace(cachedProcessIdentity.CommandLine) &&
                cachedProcessIdentity.StartTimeUtc.HasValue && basic.StartTimeUtc.HasValue &&
                !String.IsNullOrWhiteSpace(cachedProcessIdentity.ImagePath) && !String.IsNullOrWhiteSpace(basic.ImagePath) &&
                cachedProcessIdentity.SessionId >= 0 && cachedProcessIdentity.SessionId == basic.SessionId &&
                ProcessInspector.IsSame(cachedProcessIdentity, basic))
            {
                process = basic;
                process.CommandLine = cachedProcessIdentity.CommandLine;
                if (String.IsNullOrWhiteSpace(process.ImagePath)) process.ImagePath = cachedProcessIdentity.ImagePath;
            }
            else
            {
                process = ProcessInspector.Get(processId, false);
                cachedProcessIdentity = process;
            }
            bool processVerified = false;
            foreach (Regex pattern in processPatterns)
            {
                if (pattern.IsMatch(process.CommandLine ?? String.Empty))
                {
                    processVerified = true;
                    break;
                }
            }
            bool httpVerified = TestHttp(port);
            PortInspection inspection = new PortInspection();
            inspection.Port = port;
            inspection.ProcessId = processId;
            inspection.Process = process;
            inspection.ProcessVerified = processVerified;
            inspection.HttpVerified = httpVerified;
            inspection.Kind = httpVerified && processVerified ? InstanceStateKind.Running : (processVerified ? InstanceStateKind.Starting : InstanceStateKind.Conflict);
            inspection.Detail = inspection.Kind.ToString();
            return inspection;
        }

        public void OpenOrStart()
        {
            PortInspection inspection = FindCurrentInspection();
            LastInspection = inspection;
            if (inspection.Kind == InstanceStateKind.Running)
            {
                ActivePort = inspection.Port;
                SaveRunningState(inspection);
                OpenFrontend(inspection.Port);
                return;
            }
            if (inspection.Kind == InstanceStateKind.Starting)
            {
                ApplyInspectionOwnership(inspection);
                ActivePort = inspection.Port;
                startingPort = inspection.Port;
                startDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
                openWhenReady = true;
                SetState(InstanceStateKind.Starting, "Waiting for port " + inspection.Port);
                return;
            }
            if (inspection.Kind == InstanceStateKind.Conflict)
            {
                ProcessId = inspection.ProcessId;
                ownership = InstanceOwnership.Attached;
                inspection.Process = ProcessInspector.Get(inspection.ProcessId, true);
                ConflictChoice choice = interaction.ResolvePortConflict(inspection, FindFreePort());
                if (choice.Action == ConflictAction.UseAlternate)
                {
                    Start(choice.Port, true);
                }
                else if (choice.Action == ConflictAction.EndProcess)
                {
                    string error;
                    if (SafeTermination.TryCloseThenKill(inspection.Process, inspection.Port, interaction, out error)) Start(Config.PreferredPort, true);
                    else if (!String.IsNullOrWhiteSpace(error)) interaction.Show(ManagerMessageKind.Warning, error);
                }
                return;
            }
            Start(Config.PreferredPort, true);
        }

        public void Start(int port, bool openAfterStart)
        {
            if (State == InstanceStateKind.Starting || State == InstanceStateKind.Stopping || State == InstanceStateKind.Updating) return;
            PortInspection inspection = InspectPort(port);
            if (inspection.Kind != InstanceStateKind.Stopped)
            {
                if (inspection.Kind == InstanceStateKind.Running)
                {
                    ActivePort = port;
                    SaveRunningState(inspection);
                    if (openAfterStart) OpenFrontend(port);
                }
                else
                {
                    interaction.Show(ManagerMessageKind.Warning, Localization.Format("Dialog.PortUnavailable", port));
                }
                return;
            }
            try
            {
                bridgeLaunch = RuntimeBridgePatch.Create(Config, Plugin);
                string patchPath = bridgeLaunch == null ? String.Empty : bridgeLaunch.PatchPath;
                IRuntimeAdapter adapter = RuntimeAdapters.Get(Config);
                RuntimeResolution runtime = adapter.Resolve(Config, Plugin, port, patchPath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string logDirectory = AppPaths.LogDirectory;
                if (String.Equals(Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
                    logDirectory = Path.Combine(AppPaths.LogDirectory, "wsl", AppPaths.SafeFileName(Config.Id));
                Directory.CreateDirectory(logDirectory);
                string outputLog = Path.Combine(logDirectory, Config.Id + "-" + timestamp + ".out.log");
                string errorLog = Path.Combine(logDirectory, Config.Id + "-" + timestamp + ".err.log");
                FileLog.Info("Starting " + Config.Id + " with " + runtime.Description + " on port " + port);
                activeAdapter = adapter;
                launchProcess = adapter.Start(runtime, outputLog, errorLog);
                launchProcess.Exited += OnManagedProcessExited;
                if (launchProcess.HasExited) managedProcessExited = true;
                launchIdentity = adapter.CaptureIdentity(launchProcess);
                cachedProcessIdentity = launchIdentity;
                ownership = InstanceOwnership.Managed;
                ProcessId = launchProcess.ProcessId;
                startedAtUtc = launchIdentity == null ? null : launchIdentity.StartTimeUtc;
                LastStartResult = "pending";
                LastExitReason = String.Empty;
                ActiveRuntime = runtime;
                InstalledVersion = runtime.Version;
                startingPort = port;
                ActivePort = port;
                startDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
                openWhenReady = openAfterStart;
                stopExpected = false;
                managedProcessExitHandled = false;
                bridgeProtocolIncompatible = false;
                bridgeError = String.Empty;
                bridgeStatus = null;
                bridgeConnectAttempts = 0;
                lifecycleGeneration++;
                nextBridgeConnectUtc = DateTime.UtcNow;
                persistedState = new PersistedInstanceState();
                persistedState.Port = port;
                persistedState.ProcessId = launchProcess.ProcessId;
                persistedState.StartedAtUtc = startedAtUtc.HasValue ? startedAtUtc.Value.ToString("o") : String.Empty;
                persistedState.Ownership = InstanceModel.ToText(ownership);
                persistedState.RuntimeId = runtime.Definition.Id;
                persistedState.PipeName = bridgeLaunch == null ? String.Empty : bridgeLaunch.PipeName;
                persistedState.Transport = bridgeLaunch == null ? String.Empty : bridgeLaunch.Transport;
                persistedState.Host = bridgeLaunch == null ? String.Empty : bridgeLaunch.Host;
                persistedState.BridgePort = bridgeLaunch == null ? 0 : bridgeLaunch.Port;
                persistedState.PipeToken = bridgeLaunch == null ? String.Empty : bridgeLaunch.Token;
                persistedState.PatchPath = patchPath;
                persistedState.OutputLog = outputLog;
                persistedState.ErrorLog = errorLog;
                persistedState.UpdatedAt = DateTime.UtcNow.ToString("o");
                configurationStore.SaveState(Config.Id, persistedState);
                SetState(InstanceStateKind.Starting, "Starting on port " + port);
                EnsureBridgeConnection();
            }
            catch (Exception exception)
            {
                FileLog.Error(exception);
                LastError = exception.Message;
                SetState(InstanceStateKind.Error, "Start failed: " + exception.Message);
                interaction.Show(ManagerMessageKind.Error, Localization.Format("Dialog.StartFailed", exception.Message));
            }
        }

        public bool Stop(bool confirm)
        {
            PortInspection inspection = FindCurrentInspection();
            if (inspection.Kind != InstanceStateKind.Running && inspection.Kind != InstanceStateKind.Starting)
            {
                if (confirm) interaction.Show(ManagerMessageKind.Information, Localization.Text("Dialog.NotRunning"));
                return true;
            }
            if (confirm)
            {
                if (!interaction.Confirm(ManagerConfirmKind.Question,
                    Localization.Format("Dialog.StopConfirm", Config.Name, inspection.Port, inspection.ProcessId),
                    Localization.Text("App.Title"))) return false;
            }

            string bridgeError;
            if (TryGracefulStop(inspection, out bridgeError)) return true;

            if (!interaction.Confirm(ManagerConfirmKind.Warning,
                Localization.Format("Dialog.GracefulFailed", bridgeError),
                Localization.Text("App.Title"))) return false;
            if (String.Equals(Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                if (launchProcess == null)
                {
                    ProcessIdentity wslIdentity = new ProcessIdentity();
                    wslIdentity.ProcessId = ProcessId;
                    wslIdentity.Name = "DSH (WSL)";
                    wslIdentity.ImagePath = Config.WslDistro ?? String.Empty;
                    wslIdentity.SessionId = -1;
                    wslIdentity.Services = new List<string>();
                    if (!interaction.ConfirmForceEnd(wslIdentity)) return false;
                    WslRuntimeAdapter adapter = RuntimeAdapters.Get(Config) as WslRuntimeAdapter;
                    if (adapter == null)
                    {
                        interaction.Show(ManagerMessageKind.Warning, Localization.Text("Dialog.NotVerified"));
                        return false;
                    }
                    bool stillCurrent = false;
                    List<WslRunningInstance> detected = adapter.DetectRunning(Config.WslDistro);
                    foreach (WslRunningInstance item in detected)
                    {
                        if (item.Pid == ProcessId && item.Port == inspection.Port)
                        {
                            stillCurrent = true;
                            break;
                        }
                    }
                    if (!stillCurrent)
                    {
                        interaction.Show(ManagerMessageKind.Warning, Localization.Text("Safety.Changed"));
                        return false;
                    }
                    string wslTerminationError;
                    if (!adapter.TerminateLinuxProcess(Config.WslDistro, ProcessId, out wslTerminationError))
                    {
                        interaction.Show(ManagerMessageKind.Warning, wslTerminationError);
                        return false;
                    }
                    FinishStop();
                    return true;
                }
                if (!interaction.ConfirmForceEnd(launchIdentity))
                {
                    return false;
                }
                if (activeAdapter != null)
                {
                    activeAdapter.Kill(launchProcess);
                    launchProcess.WaitForExit(5000);
                }
                FinishStop();
                return true;
            }
            if (!inspection.ProcessVerified && !inspection.HttpVerified)
            {
                interaction.Show(ManagerMessageKind.Warning, Localization.Text("Dialog.NotVerified"));
                return false;
            }
            string terminationError;
            if (!SafeTermination.TryCloseThenKill(inspection.Process, inspection.Port, interaction, out terminationError))
            {
                interaction.Show(ManagerMessageKind.Warning, terminationError);
                return false;
            }
            FinishStop();
            return true;
        }

        public bool TryGracefulStop(out string error)
        {
            PortInspection inspection = FindCurrentInspection();
            if (inspection.Kind != InstanceStateKind.Running && inspection.Kind != InstanceStateKind.Starting)
            {
                error = String.Empty;
                FinishStop();
                return true;
            }
            return TryGracefulStop(inspection, out error);
        }

        public void Restart()
        {
            int port = ActivePort > 0 ? ActivePort : Config.PreferredPort;
            if (Stop(false)) Start(port, true);
        }

        public string GetDetails()
        {
            PortInspection inspection = FindCurrentInspection();
            StringBuilder text = new StringBuilder();
            text.AppendLine(Localization.Text("Details.Instance") + ": " + Config.Name + " (" + Config.Id + ")");
            text.AppendLine(Localization.Text("Details.State") + ": " + State);
            text.AppendLine(Localization.Text("Details.Profile") + ": " + Config.Profile);
            text.AppendLine(Localization.Text("Details.Frontend") + ": " + (String.IsNullOrWhiteSpace(Config.Frontend) ? InstanceModel.FrontendWeb : Config.Frontend));
            text.AppendLine(Localization.Text("Details.Runtime") + ": " + Config.Runtime);
            text.AppendLine(Localization.Text("Details.Version") + ": " + (String.IsNullOrWhiteSpace(InstalledVersion) ? Localization.Text("Version.Unknown") : InstalledVersion));
            text.AppendLine(Localization.Text("Details.Port") + ": " + inspection.Port);
            TokenContext context = RuntimeResolver.CreateContext(Config, Plugin, inspection.Port, String.Empty);
            text.AppendLine(Localization.Text("Details.Url") + ": " + AppPaths.Expand(Plugin.Probe.UrlTemplate, context));
            if (inspection.Process != null)
            {
                text.AppendLine("PID: " + inspection.ProcessId);
                text.AppendLine(Localization.Text("Details.Process") + ": " + inspection.Process.Name);
                text.AppendLine(Localization.Text("Details.Path") + ": " + inspection.Process.ImagePath);
                if (inspection.Process.Services != null && inspection.Process.Services.Count > 0)
                    text.AppendLine(Localization.Text("Details.Services") + ": " + String.Join(", ", inspection.Process.Services.ToArray()));
            }
            text.AppendLine(Localization.Text("Details.HttpFingerprint") + ": " + inspection.HttpVerified);
            text.AppendLine(Localization.Text("Details.ProcessFingerprint") + ": " + inspection.ProcessVerified);
            text.AppendLine(Localization.Text("Details.BridgeFingerprint") + ": " + inspection.BridgeVerified);
            text.AppendLine(Localization.Text("Details.IpcBridge") + ": " + (IpcBridgeConnected ? Localization.Text("Details.IpcBridgeConnected") : Localization.Text("Details.IpcBridgeDisconnected")));
            if (bridgeStatus != null)
            {
                text.AppendLine(Localization.Text("Details.IpcState") + ": " + (bridgeStatus.State ?? String.Empty));
                if (!String.IsNullOrWhiteSpace(bridgeStatus.DshVersion)) text.AppendLine(Localization.Text("Details.IpcDshVersion") + ": " + bridgeStatus.DshVersion);
                if (!String.IsNullOrWhiteSpace(bridgeStatus.DshHome)) text.AppendLine(Localization.Text("Details.IpcDshHome") + ": " + bridgeStatus.DshHome);
            }
            if (!String.IsNullOrWhiteSpace(bridgeError)) text.AppendLine(Localization.Text("Details.IpcError") + ": " + bridgeError);
            text.AppendLine(Localization.Text("Details.Workspace") + ": " + Config.Workspace);
            if (!String.IsNullOrWhiteSpace(Config.DshHome)) text.AppendLine("DSH_HOME: " + Config.DshHome);
            if (!String.IsNullOrWhiteSpace(Config.SourceRoot)) text.AppendLine(Localization.Text("Details.Source") + ": " + Config.SourceRoot);
            if (persistedState != null && !String.IsNullOrWhiteSpace(persistedState.OutputLog)) text.AppendLine(Localization.Text("Details.OutputLog") + ": " + persistedState.OutputLog);
            if (persistedState != null && !String.IsNullOrWhiteSpace(persistedState.ErrorLog)) text.AppendLine(Localization.Text("Details.ErrorLog") + ": " + persistedState.ErrorLog);
            if (!String.IsNullOrWhiteSpace(LastError)) text.AppendLine(Localization.Text("Details.LastError") + ": " + LastError);
            return text.ToString();
        }

        public int FindFreePort()
        {
            int count = Plugin.FallbackPortCount <= 0 ? 20 : Plugin.FallbackPortCount;
            int end = Math.Min(65535, Config.PreferredPort + count);
            int port;
            for (port = Config.PreferredPort + 1; port <= end; port++)
                if (PortMap.GetListenerProcessIds(port).Count == 0) return port;
            return 0;
        }

        public void SetUpdating(bool updating, string text)
        {
            SetState(updating ? InstanceStateKind.Updating : InstanceStateKind.Stopped, text);
        }

        public void Close()
        {
            CloseBridge();
        }

        private PortInspection FindCurrentInspection()
        {
            if (IpcBridgeConnected && bridgeStatus != null && bridgeStatus.IsReady)
            {
                PortInspection bridgeInspection = BuildInspectionFromBridge(bridgeStatus);
                if (bridgeInspection != null && bridgeInspection.Kind == InstanceStateKind.Running) return bridgeInspection;
            }

            List<int> ports = new List<int>();
            if (ActivePort > 0) ports.Add(ActivePort);
            if (persistedState != null && persistedState.Port > 0 && !ports.Contains(persistedState.Port)) ports.Add(persistedState.Port);
            if (!ports.Contains(Config.PreferredPort)) ports.Add(Config.PreferredPort);
            foreach (int port in ports)
            {
                PortInspection inspection = InspectPort(port);
                if (inspection.Kind == InstanceStateKind.Running || inspection.Kind == InstanceStateKind.Starting) return inspection;
            }
            return InspectPort(Config.PreferredPort);
        }

        private bool TestHttp(int port)
        {
            return RuntimeHttpProbe.Verify(Config, Plugin, port, 1200);
        }

        private void CompleteStart(PortInspection inspection)
        {
            ActivePort = inspection.Port;
            startingPort = 0;
            LastError = String.Empty;
            LastStartResult = "success";
            managedProcessExitHandled = false;
            managedProcessExited = false;
            processExitJustHandled = false;
            SaveRunningState(inspection);
            SetState(InstanceStateKind.Running, "Running on port " + inspection.Port);
            FileLog.Info(Config.Id + " is ready on port " + inspection.Port + " PID " + inspection.ProcessId);
            if (openWhenReady)
            {
                openWhenReady = false;
                OpenFrontend(inspection.Port);
            }
        }

        private void FailStart(string error)
        {
            int failedPort = startingPort;
            LastError = error;
            LastStartResult = "failed";
            LastExitReason = error;
            startingPort = 0;
            openWhenReady = false;
            try
            {
                if (launchProcess != null && !launchProcess.HasExited && activeAdapter != null)
                {
                    activeAdapter.Kill(launchProcess);
                    launchProcess.WaitForExit(3000);
                }
            }
            catch { }
            CloseBridge();
            bridgeStatus = null;
            bridgeError = String.Empty;
            stopExpected = false;
            managedProcessExitHandled = true;
            managedProcessExited = false;
            processExitJustHandled = false;
            launchIdentity = null;
            ProcessId = 0;
            startedAtUtc = null;
            ownership = InstanceOwnership.Attached;
            DisposeLaunchProcess();
            SetState(InstanceStateKind.Error, error);
            FileLog.Error(Config.Id + ": " + error);
        }

        private void FinishStop()
        {
            FileLog.Info(Config.Id + " stopped");
            LastExitReason = "manager-requested-stop";
            ActivePort = 0;
            startingPort = 0;
            openWhenReady = false;
            string patchPath = bridgeLaunch == null ? null : bridgeLaunch.PatchPath;
            CloseBridge();
            bridgeStatus = null;
            bridgeError = String.Empty;
            lifecycleGeneration++;
            persistedState = null;
            bridgeLaunch = null;
            savedPort = 0;
            savedProcessId = 0;
            stopExpected = false;
            managedProcessExitHandled = true;
            managedProcessExited = false;
            processExitJustHandled = false;
            bridgeConnectAttempts = 0;
            launchIdentity = null;
            ProcessId = 0;
            startedAtUtc = null;
            ownership = InstanceOwnership.Attached;
            configurationStore.DeleteState(Config.Id);
            try { if (!String.IsNullOrWhiteSpace(patchPath) && File.Exists(patchPath)) File.Delete(patchPath); } catch { }
            DisposeLaunchProcess();
            SetState(InstanceStateKind.Stopped, "Stopped");
        }

        private void DisposeLaunchProcess()
        {
            if (launchProcess == null) return;
            try { launchProcess.Exited -= OnManagedProcessExited; } catch { }
            try { launchProcess.Dispose(); } catch { }
            launchProcess = null;
            activeAdapter = null;
        }

        private bool TryGracefulStop(PortInspection inspection, out string error)
        {
            stopExpected = true;
            bool requested = GracefulShutdownClient.Request(bridgeLaunch, 1500, out error);
            if (!requested)
            {
                stopExpected = false;
                return false;
            }
            SetState(InstanceStateKind.Stopping, "Stopping on port " + inspection.Port);
            FileLog.Info("Graceful shutdown requested for " + Config.Id + " PID " + inspection.ProcessId);
            try
            {
                using (Process process = Process.GetProcessById(inspection.ProcessId))
                {
                    if (process.WaitForExit(7000))
                    {
                        FinishStop();
                        error = String.Empty;
                        return true;
                    }
                }
                error = "The process did not exit after the official Cordis shutdown timeout.";
            }
            catch (ArgumentException)
            {
                FinishStop();
                error = String.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            return false;
        }

        private void SaveRunningState(PortInspection inspection)
        {
            if (inspection.ProcessId == savedProcessId && inspection.Port == savedPort && persistedState != null) return;
            if (persistedState == null) persistedState = new PersistedInstanceState();
            ApplyInspectionOwnership(inspection);
            persistedState.Port = inspection.Port;
            persistedState.ProcessId = inspection.ProcessId;
            persistedState.StartedAtUtc = startedAtUtc.HasValue ? startedAtUtc.Value.ToString("o") : String.Empty;
            persistedState.Ownership = InstanceModel.ToText(ownership);
            persistedState.PipeName = bridgeLaunch == null ? persistedState.PipeName : bridgeLaunch.PipeName;
            persistedState.Transport = bridgeLaunch == null ? persistedState.Transport : bridgeLaunch.Transport;
            persistedState.Host = bridgeLaunch == null ? persistedState.Host : bridgeLaunch.Host;
            persistedState.BridgePort = bridgeLaunch == null ? persistedState.BridgePort : bridgeLaunch.Port;
            persistedState.PipeToken = bridgeLaunch == null ? persistedState.PipeToken : bridgeLaunch.Token;
            persistedState.PatchPath = bridgeLaunch == null ? persistedState.PatchPath : bridgeLaunch.PatchPath;
            persistedState.UpdatedAt = DateTime.UtcNow.ToString("o");
            configurationStore.SaveState(Config.Id, persistedState);
            savedPort = inspection.Port;
            savedProcessId = inspection.ProcessId;
        }

        private void ApplyInspectionOwnership(PortInspection inspection)
        {
            if (launchProcess != null)
            {
                ownership = InstanceOwnership.Managed;
            }
            else if (ownership == InstanceOwnership.Managed && persistedState != null && persistedState.ProcessId == inspection.ProcessId)
            {
                // A Manager-managed process is still the same PID after a
                // Manager restart; keep full lifecycle ownership.
            }
            else
            {
                ownership = InstanceOwnership.Attached;
            }
            ProcessId = inspection.ProcessId;
            if (inspection.Process != null && inspection.Process.StartTimeUtc.HasValue)
                startedAtUtc = inspection.Process.StartTimeUtc;
        }

        public void DrainBridgeMessages()
        {
            List<BridgeMessage> messages = null;
            lock (bridgeSync)
            {
                if (bridgeMessages.Count > 0)
                {
                    messages = new List<BridgeMessage>();
                    while (bridgeMessages.Count > 0) messages.Add(bridgeMessages.Dequeue());
                }
            }
            if (messages != null)
            {
                foreach (BridgeMessage message in messages) ApplyBridgeMessage(message);
            }

            if (managedProcessExited && !managedProcessExitHandled)
            {
                managedProcessExitHandled = true;
                processExitJustHandled = true;
                HandleManagedProcessExit();
            }
        }

        private void QueueBridgeMessage(BridgeMessage message)
        {
            if (message == null) return;
            lock (bridgeSync) bridgeMessages.Enqueue(message);
            RaiseChanged();
        }

        private void ApplyBridgeMessage(BridgeMessage message)
        {
            if (message == null) return;
            if (String.Equals(message.MessageType, "response", StringComparison.OrdinalIgnoreCase))
            {
                BridgeRuntimeInfo info = BridgeProtocol.ParseRuntimeInfo(message);
                if (info != null) ApplyBridgeRuntimeInfo(info);
                return;
            }
            BridgeRuntimeInfo eventInfo = BridgeProtocol.ParseRuntimeInfo(message);
            if (eventInfo != null) ApplyBridgeRuntimeInfo(eventInfo);
        }

        private void ApplyBridgeRuntimeInfo(BridgeRuntimeInfo info)
        {
            if (info == null) return;
            bridgeStatus = info;
            if (!String.IsNullOrWhiteSpace(info.DshVersion)) InstalledVersion = info.DshVersion;

            if (info.IsReady)
            {
                if (String.Equals(Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
                    ProcessId = info.Pid;
                TryCompleteStartFromBridge();
            }
            else if (info.IsStopping && State == InstanceStateKind.Running)
            {
                SetState(InstanceStateKind.Stopping, "DSH is stopping on port " + (ActivePort > 0 ? ActivePort : info.Port));
            }
        }

        private bool TryCompleteStartFromBridge()
        {
            BridgeRuntimeInfo info = bridgeStatus;
            if (info == null || !info.IsReady) return false;

            PortInspection inspection = BuildInspectionFromBridge(info);
            if (inspection == null)
            {
                FileLog.Warn("DSH IPC bridge reported ready, but process/port revalidation failed for " + Config.Id + ".");
                if (State == InstanceStateKind.Starting)
                {
                    bridgeError = "The IPC bridge reported ready, but process/port revalidation failed.";
                    CloseBridge();
                    nextBridgeConnectUtc = DateTime.UtcNow.AddSeconds(5);
                    nextInspectionUtc = DateTime.MinValue;
                }
                return false;
            }

            LastInspection = inspection;
            if (State == InstanceStateKind.Starting)
            {
                if (startingPort > 0 && inspection.Port != startingPort) return false;
                CompleteStart(inspection);
                return true;
            }
            if (State == InstanceStateKind.Running)
            {
                ActivePort = inspection.Port;
                SaveRunningState(inspection);
                SetState(InstanceStateKind.Running, "Running on port " + inspection.Port);
                return true;
            }
            if (State == InstanceStateKind.Stopped || State == InstanceStateKind.Conflict || State == InstanceStateKind.Error)
            {
                ActivePort = inspection.Port;
                SaveRunningState(inspection);
                SetState(InstanceStateKind.Running, "Running on port " + inspection.Port);
                return true;
            }
            return false;
        }

        private PortInspection BuildInspectionFromBridge(BridgeRuntimeInfo info)
        {
            if (info == null || info.Pid <= 0 || info.Port <= 0 || info.Port > 65535) return null;
            if (String.Equals(Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                ProcessIdentity wslIdentity = new ProcessIdentity();
                wslIdentity.ProcessId = info.Pid;
                wslIdentity.Name = "DSH (WSL)";
                wslIdentity.ImagePath = Config.WslDistro ?? String.Empty;
                wslIdentity.CommandLine = String.Empty;
                wslIdentity.SessionId = -1;
                wslIdentity.Services = new List<string>();
                PortInspection wslInspection = new PortInspection();
                wslInspection.Kind = InstanceStateKind.Running;
                wslInspection.Port = info.Port;
                wslInspection.ProcessId = info.Pid;
                wslInspection.Process = wslIdentity;
                wslInspection.ProcessVerified = false;
                wslInspection.HttpVerified = false;
                wslInspection.BridgeVerified = true;
                wslInspection.Detail = "Authoritative WSL Runtime Bridge";
                return wslInspection;
            }
            if (PortMap.GetPreferredListenerProcessId(info.Port) != info.Pid)
            {
                FileLog.Warn("DSH IPC bridge PID/port mismatch for " + Config.Id + ": pid=" + info.Pid + " port=" + info.Port);
                return null;
            }

            ProcessIdentity basic = ProcessInspector.GetBasic(info.Pid);
            ProcessIdentity process = null;
            if (cachedProcessIdentity != null && !String.IsNullOrWhiteSpace(cachedProcessIdentity.CommandLine) &&
                ProcessInspector.IsSame(cachedProcessIdentity, basic))
            {
                process = cachedProcessIdentity;
            }
            else if (launchIdentity != null && ProcessInspector.IsSame(launchIdentity, basic))
            {
                process = ProcessInspector.Get(info.Pid, false);
                cachedProcessIdentity = process;
            }
            else
            {
                process = ProcessInspector.Get(info.Pid, false);
                cachedProcessIdentity = process;
            }

            bool processVerified = MatchesProcessPatterns(process.CommandLine);
            if (!processVerified && launchIdentity != null && ProcessInspector.IsSame(launchIdentity, process))
                processVerified = !String.IsNullOrWhiteSpace(launchIdentity.CommandLine) && MatchesProcessPatterns(launchIdentity.CommandLine);

            PortInspection inspection = new PortInspection();
            inspection.Kind = InstanceStateKind.Running;
            inspection.Port = info.Port;
            inspection.ProcessId = info.Pid;
            inspection.Process = process;
            inspection.ProcessVerified = processVerified;
            inspection.HttpVerified = false;
            inspection.BridgeVerified = true;
            inspection.Detail = "Authoritative DSH IPC bridge";
            return inspection;
        }

        private bool MatchesProcessPatterns(string commandLine)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            foreach (Regex pattern in processPatterns)
                if (pattern.IsMatch(commandLine)) return true;
            return false;
        }

        private void EnsureBridgeConnection()
        {
            if (bridgeProtocolIncompatible || bridgeLaunch == null) return;
            if (String.Equals(bridgeLaunch.Transport, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(bridgeLaunch.Host) || bridgeLaunch.Port < 1 || bridgeLaunch.Port > 65535 || String.IsNullOrWhiteSpace(bridgeLaunch.Token)) return;
            }
            else if (String.IsNullOrWhiteSpace(bridgeLaunch.PipeName) || String.IsNullOrWhiteSpace(bridgeLaunch.Token)) return;
            if (bridge != null)
            {
                if (bridge.IsConnected) return;
                CloseBridge();
            }
            if (bridgeConnectTask != null && !bridgeConnectTask.IsCompleted) return;
            if (DateTime.UtcNow < nextBridgeConnectUtc) return;

            bridgeConnectTask = Task.Run(new Action(BridgeConnectWorker));
        }

        private void BridgeConnectWorker()
        {
            RuntimeBridgeLaunch launch = bridgeLaunch;
            int generation = lifecycleGeneration;
            if (launch == null || generation != lifecycleGeneration) return;

            string error;
            IpcBridgeConnection connection = String.Equals(launch.Transport, "tcp", StringComparison.OrdinalIgnoreCase)
                ? IpcBridgeConnection.ConnectTcp(launch.Host, launch.Port, launch.Token, 2500, out error)
                : IpcBridgeConnection.Connect(launch.PipeName, launch.Token, 2500, out error);
            if (connection == null)
            {
                if (generation == lifecycleGeneration)
                {
                    bridgeError = error;
                    bridgeConnectAttempts++;
                    nextBridgeConnectUtc = DateTime.UtcNow.AddMilliseconds(BridgeRetryMilliseconds());
                    RaiseChanged();
                }
                return;
            }
            if (generation != lifecycleGeneration)
            {
                connection.Close();
                return;
            }
            if (!connection.IsConnected)
            {
                connection.Close();
                if (generation == lifecycleGeneration)
                {
                    bridgeError = "The DSH IPC bridge disconnected before authentication completed.";
                    bridgeConnectAttempts++;
                    nextBridgeConnectUtc = DateTime.UtcNow.AddMilliseconds(BridgeRetryMilliseconds());
                    RaiseChanged();
                }
                return;
            }

            connection.EventReceived += OnBridgeEvent;
            connection.Disconnected += OnBridgeDisconnected;
            bridge = connection;
            if (generation != lifecycleGeneration)
            {
                CloseBridge();
                return;
            }
            bridgeConnectAttempts = 0;
            bridgeError = String.Empty;

            BridgeMessage ping = connection.Request("ping", null, 1500);
            if (!IsBridgeAccepted(ping))
            {
                RejectBridgeConnection(connection, ping);
                return;
            }

            BridgeMessage status = connection.Request("getStatus", null, 1500);
            if (!IsBridgeAccepted(status))
            {
                RejectBridgeConnection(connection, status);
                return;
            }
            QueueBridgeMessage(status);

            BridgeMessage runtime = connection.Request("getRuntimeInfo", null, 1500);
            if (!IsBridgeAccepted(runtime))
            {
                RejectBridgeConnection(connection, runtime);
                return;
            }
            QueueBridgeMessage(runtime);
            RaiseChanged();
        }

        private bool IsBridgeAccepted(BridgeMessage response)
        {
            return response != null && response.Ok;
        }

        private void RejectBridgeConnection(IpcBridgeConnection connection, BridgeMessage response)
        {
            if (connection == null) return;
            connection.EventReceived -= OnBridgeEvent;
            connection.Disconnected -= OnBridgeDisconnected;
            if (ReferenceEquals(bridge, connection)) bridge = null;
            connection.Close();

            if (response != null && String.Equals(BridgeProtocol.ErrorCode(response), "protocol-version-unsupported", StringComparison.OrdinalIgnoreCase))
            {
                bridgeProtocolIncompatible = true;
                bridgeError = BridgeProtocol.DescribeError(response);
                FileLog.Warn("DSH IPC bridge is protocol-incompatible for " + Config.Id + ": " + bridgeError);
                RaiseChanged();
                return;
            }

            bridgeError = response == null ? Localization.Text("Bridge.Rejected") : BridgeProtocol.DescribeError(response);
            FileLog.Warn("DSH IPC bridge rejected " + Config.Id + ": " + bridgeError);
            bridgeConnectAttempts++;
            nextBridgeConnectUtc = DateTime.UtcNow.AddMilliseconds(BridgeRetryMilliseconds());
            RaiseChanged();
        }

        private int BridgeRetryMilliseconds()
        {
            int attempt = Math.Min(bridgeConnectAttempts, 6);
            return Math.Min(30000, 1000 * (1 << attempt));
        }

        private void OnBridgeEvent(object sender, BridgeEventReceivedEventArgs args)
        {
            QueueBridgeMessage(args == null ? null : args.Message);
        }

        private void OnBridgeDisconnected(object sender, BridgeDisconnectedEventArgs args)
        {
            IpcBridgeConnection connection = sender as IpcBridgeConnection;
            if (connection == null) return;
            connection.EventReceived -= OnBridgeEvent;
            connection.Disconnected -= OnBridgeDisconnected;
            if (!ReferenceEquals(bridge, connection)) return;
            bridge = null;
            bridgeError = args == null ? "The DSH IPC bridge disconnected." : args.Reason;
            bridgeConnectAttempts++;
            nextBridgeConnectUtc = DateTime.UtcNow.AddMilliseconds(BridgeRetryMilliseconds());
            FileLog.Warn("DSH IPC bridge disconnected for " + Config.Id + ": " + bridgeError);
            if (State != InstanceStateKind.Stopping && State != InstanceStateKind.Stopped) RaiseChanged();
        }

        private void OnManagedProcessExited(object sender, EventArgs args)
        {
            IRuntimeProcess runtime = sender as IRuntimeProcess;
            if (runtime == null || !ReferenceEquals(runtime, launchProcess)) return;
            managedProcessExited = true;
            RaiseChanged();
        }

        private void HandleManagedProcessExit()
        {
            if (launchProcess == null) return;
            if (State == InstanceStateKind.Stopped || State == InstanceStateKind.Updating) return;
            if (stopExpected || State == InstanceStateKind.Stopping)
            {
                FinishStop();
                return;
            }
            if (State == InstanceStateKind.Starting)
            {
                FailStart("DeepSeek Harness exited before becoming ready.");
                return;
            }

            FileLog.Error(Config.Id + ": DSH process exited unexpectedly (PID " + SafeProcessId() + ").");
            LastError = "DSH process exited unexpectedly.";
            LastStartResult = "failed";
            LastExitReason = "unexpected-exit";
            CloseBridge();
            bridgeStatus = null;
            bridgeError = String.Empty;
            lifecycleGeneration++;
            stopExpected = false;
            launchIdentity = null;
            ProcessId = 0;
            startedAtUtc = null;
            ownership = InstanceOwnership.Attached;
            ActivePort = 0;
            startingPort = 0;
            openWhenReady = false;
            string patchPath = bridgeLaunch == null ? null : bridgeLaunch.PatchPath;
            persistedState = null;
            bridgeLaunch = null;
            savedPort = 0;
            savedProcessId = 0;
            configurationStore.DeleteState(Config.Id);
            try { if (!String.IsNullOrWhiteSpace(patchPath) && File.Exists(patchPath)) File.Delete(patchPath); } catch { }
            DisposeLaunchProcess();
            SetState(InstanceStateKind.Error, "DSH process exited unexpectedly");
        }

        private bool IsLaunchProcessExited()
        {
            if (managedProcessExited) return true;
            return launchProcess == null || launchProcess.HasExited;
        }

        private void CloseBridge()
        {
            IpcBridgeConnection connection = bridge;
            bridge = null;
            if (connection == null) return;
            connection.EventReceived -= OnBridgeEvent;
            connection.Disconnected -= OnBridgeDisconnected;
            connection.Close();
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private string SafeProcessId()
        {
            int processId = launchProcess == null ? 0 : launchProcess.ProcessId;
            return processId <= 0 ? "unknown" : processId.ToString();
        }


        private static InstanceOwnership ResolvePersistedOwnership(PersistedInstanceState state)
        {
            if (state == null || String.IsNullOrWhiteSpace(state.Ownership)) return InstanceOwnership.Attached;
            return InstanceModel.ParseOwnership(state.Ownership);
        }

        private static DateTime? ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : (DateTime?)null;
        }

        public void AdoptDetectedProcess(int processId)
        {
            ownership = InstanceOwnership.Attached;
            ProcessId = processId;
            startedAtUtc = null;
            LastError = String.Empty;
            nextInspectionUtc = DateTime.MinValue;
            if (State != InstanceStateKind.Starting && State != InstanceStateKind.Running)
            {
                SetState(InstanceStateKind.Stopped, "Detected WSL DSH on port " + Config.PreferredPort);
            }
        }

        private void SetState(InstanceStateKind state, string text)
        {
            bool changed = State != state || !String.Equals(StatusText, text, StringComparison.Ordinal);
            State = state;
            StatusText = text;
            if (changed) nextInspectionUtc = DateTime.MinValue;
            if (changed && Changed != null) Changed(this, EventArgs.Empty);
        }

        internal static int InspectionIntervalMilliseconds(InstanceStateKind state)
        {
            return state == InstanceStateKind.Starting ? 1000 : 5000;
        }

        private void OpenFrontend(int port)
        {
            string url;
            string frontendError;
            if (!FrontendLauncher.TryResolve(Config, Plugin, port, out url, out frontendError))
            {
                LastError = frontendError;
                FileLog.Error("Could not open frontend for " + Config.Id + ": " + frontendError);
                interaction.Show(ManagerMessageKind.Warning, frontendError);
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                Process.Start(info);
                FileLog.Info("Opened " + Config.Frontend + " frontend for " + Config.Id + ": " + url);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                FileLog.Error("Could not open " + Config.Frontend + " frontend for " + Config.Id + ": " + exception.Message);
                interaction.Show(ManagerMessageKind.Warning, Localization.Format("Dialog.OpenBrowserFailed", url, exception.Message));
            }
        }

        private string ReadErrorTail()
        {
            string path = persistedState == null ? null : persistedState.ErrorLog;
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return String.Empty;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - 5);
                return String.Join(" | ", new List<string>(lines).GetRange(start, lines.Length - start).ToArray());
            }
            catch { return String.Empty; }
        }

        private static string SafeProcessName(ProcessIdentity process)
        {
            return process == null || String.IsNullOrWhiteSpace(process.Name) ? "an unknown process" : process.Name + " (PID " + process.ProcessId + ")";
        }
    }
}
