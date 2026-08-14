using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    public sealed class InstanceController
    {
        private readonly ConfigurationStore configurationStore;
        private readonly List<Regex> processPatterns;
        private PersistedInstanceState persistedState;
        private ManagedProcess launchProcess;
        private CompanionLaunch companion;
        private DateTime startDeadlineUtc;
        private bool openWhenReady;
        private int startingPort;
        private int savedPort;
        private int savedProcessId;
        private DateTime nextInspectionUtc;
        private ProcessIdentity cachedProcessIdentity;

        public InstanceController(InstanceConfig config, PluginDefinition plugin, ConfigurationStore store)
        {
            Config = config;
            Plugin = plugin;
            configurationStore = store;
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
                companion = new CompanionLaunch();
                companion.PipeName = persistedState.PipeName;
                companion.Token = persistedState.PipeToken;
                companion.PatchPath = persistedState.PatchPath;
            }
            State = InstanceStateKind.Stopped;
            StatusText = "Stopped";
            InstalledVersion = RuntimeResolver.ResolveInstalledVersion(config, plugin);
        }

        public InstanceConfig Config { get; private set; }
        public PluginDefinition Plugin { get; private set; }
        public InstanceStateKind State { get; private set; }
        public string StatusText { get; private set; }
        public string LastError { get; private set; }
        public string InstalledVersion { get; set; }
        public UpdateInfo UpdateInfo { get; set; }
        public int ActivePort { get; private set; }
        public PortInspection LastInspection { get; private set; }
        public RuntimeResolution ActiveRuntime { get; private set; }
        public event EventHandler Changed;

        public void Tick()
        {
            if (State == InstanceStateKind.Updating) return;
            if (State == InstanceStateKind.Starting && startingPort > 0)
            {
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
                bool exited = false;
                if (launchProcess != null && launchProcess.RootProcess != null)
                {
                    try { exited = launchProcess.RootProcess.HasExited; } catch { exited = true; }
                }
                if (exited || DateTime.UtcNow > startDeadlineUtc)
                {
                    string detail = ReadErrorTail();
                    FailStart("DeepSeek Harness did not become ready." + (detail.Length == 0 ? String.Empty : " " + detail));
                    return;
                }
                SetState(InstanceStateKind.Starting, "Starting on port " + startingPort);
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < nextInspectionUtc) return;
            nextInspectionUtc = now.AddMilliseconds(InspectionIntervalMilliseconds(State));

            PortInspection current = FindCurrentInspection();
            LastInspection = current;
            if (current.Kind == InstanceStateKind.Running)
            {
                ActivePort = current.Port;
                SaveRunningState(current);
                SetState(InstanceStateKind.Running, "Running on port " + current.Port);
            }
            else if (current.Kind == InstanceStateKind.Starting)
            {
                ActivePort = current.Port;
                SetState(InstanceStateKind.Starting, "External instance is starting on port " + current.Port);
            }
            else if (current.Kind == InstanceStateKind.Conflict)
            {
                ActivePort = 0;
                SetState(InstanceStateKind.Conflict, "Port " + current.Port + " is occupied by " + SafeProcessName(current.Process));
            }
            else
            {
                ActivePort = 0;
                if (persistedState != null)
                {
                    configurationStore.DeleteState(Config.Id);
                    persistedState = null;
                    companion = null;
                    savedPort = 0;
                    savedProcessId = 0;
                }
                SetState(InstanceStateKind.Stopped, "Stopped");
            }
        }

        public PortInspection InspectPort(int port)
        {
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

        public void OpenOrStart(IWin32Window owner)
        {
            PortInspection inspection = FindCurrentInspection();
            LastInspection = inspection;
            if (inspection.Kind == InstanceStateKind.Running)
            {
                ActivePort = inspection.Port;
                SaveRunningState(inspection);
                OpenWebUi(inspection.Port);
                return;
            }
            if (inspection.Kind == InstanceStateKind.Starting)
            {
                ActivePort = inspection.Port;
                startingPort = inspection.Port;
                startDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
                openWhenReady = true;
                SetState(InstanceStateKind.Starting, "Waiting for port " + inspection.Port);
                return;
            }
            if (inspection.Kind == InstanceStateKind.Conflict)
            {
                inspection.Process = ProcessInspector.Get(inspection.ProcessId, true);
                ConflictChoice choice = ManagerDialogs.ShowPortConflict(owner, inspection, FindFreePort());
                if (choice.Action == ConflictAction.UseAlternate)
                {
                    Start(choice.Port, true, owner);
                }
                else if (choice.Action == ConflictAction.EndProcess)
                {
                    string error;
                    if (SafeTermination.TryCloseThenKill(inspection.Process, inspection.Port, owner, out error)) Start(Config.PreferredPort, true, owner);
                    else if (!String.IsNullOrWhiteSpace(error)) MessageBox.Show(owner, error, Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            Start(Config.PreferredPort, true, owner);
        }

        public void Start(int port, bool openAfterStart, IWin32Window owner)
        {
            if (State == InstanceStateKind.Starting) return;
            PortInspection inspection = InspectPort(port);
            if (inspection.Kind != InstanceStateKind.Stopped)
            {
                if (inspection.Kind == InstanceStateKind.Running)
                {
                    ActivePort = port;
                    SaveRunningState(inspection);
                    if (openAfterStart) OpenWebUi(port);
                }
                else
                {
                    MessageBox.Show(owner, Localization.Format("Dialog.PortUnavailable", port), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            try
            {
                companion = CompanionPatch.Create(Config, Plugin);
                string patchPath = companion == null ? String.Empty : companion.PatchPath;
                RuntimeResolution runtime = RuntimeResolver.Resolve(Config, Plugin, port, patchPath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string runtimeDirectory = AppPaths.InstanceRuntimeDirectory(Config.Id);
                string outputLog = Path.Combine(AppPaths.LogDirectory, Config.Id + "-" + timestamp + ".out.log");
                string errorLog = Path.Combine(AppPaths.LogDirectory, Config.Id + "-" + timestamp + ".err.log");
                FileLog.Info("Starting " + Config.Id + " with " + runtime.Description + " on port " + port);
                launchProcess = CommandRunner.StartService(runtime, outputLog, errorLog);
                ActiveRuntime = runtime;
                InstalledVersion = runtime.Version;
                startingPort = port;
                ActivePort = port;
                startDeadlineUtc = DateTime.UtcNow.AddSeconds(90);
                openWhenReady = openAfterStart;
                persistedState = new PersistedInstanceState();
                persistedState.Port = port;
                persistedState.ProcessId = launchProcess.RootProcess.Id;
                persistedState.RuntimeId = runtime.Definition.Id;
                persistedState.PipeName = companion == null ? String.Empty : companion.PipeName;
                persistedState.PipeToken = companion == null ? String.Empty : companion.Token;
                persistedState.PatchPath = patchPath;
                persistedState.OutputLog = outputLog;
                persistedState.ErrorLog = errorLog;
                persistedState.UpdatedAt = DateTime.UtcNow.ToString("o");
                configurationStore.SaveState(Config.Id, persistedState);
                SetState(InstanceStateKind.Starting, "Starting on port " + port);
            }
            catch (Exception exception)
            {
                FileLog.Error(exception);
                LastError = exception.Message;
                SetState(InstanceStateKind.Error, "Start failed: " + exception.Message);
                MessageBox.Show(owner, Localization.Format("Dialog.StartFailed", exception.Message), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public bool Stop(IWin32Window owner, bool confirm)
        {
            PortInspection inspection = FindCurrentInspection();
            if (inspection.Kind != InstanceStateKind.Running && inspection.Kind != InstanceStateKind.Starting)
            {
                if (confirm) MessageBox.Show(owner, Localization.Text("Dialog.NotRunning"), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if (confirm)
            {
                DialogResult result = MessageBox.Show(owner,
                    Localization.Format("Dialog.StopConfirm", Config.Name, inspection.Port, inspection.ProcessId),
                    Localization.Text("App.Title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes) return false;
            }

            string bridgeError;
            if (TryGracefulStop(inspection, out bridgeError)) return true;

            DialogResult fallback = MessageBox.Show(owner,
                Localization.Format("Dialog.GracefulFailed", bridgeError),
                Localization.Text("App.Title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (fallback != DialogResult.Yes) return false;
            if (!inspection.ProcessVerified && !inspection.HttpVerified)
            {
                MessageBox.Show(owner, Localization.Text("Dialog.NotVerified"), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string terminationError;
            if (!SafeTermination.TryCloseThenKill(inspection.Process, inspection.Port, owner, out terminationError))
            {
                MessageBox.Show(owner, terminationError, Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        public void Restart(IWin32Window owner)
        {
            int port = ActivePort > 0 ? ActivePort : Config.PreferredPort;
            if (Stop(owner, false)) Start(port, true, owner);
        }

        public string GetDetails()
        {
            PortInspection inspection = FindCurrentInspection();
            StringBuilder text = new StringBuilder();
            text.AppendLine(Localization.Text("Details.Instance") + ": " + Config.Name + " (" + Config.Id + ")");
            text.AppendLine(Localization.Text("Details.State") + ": " + inspection.Kind);
            text.AppendLine(Localization.Text("Details.Profile") + ": " + Config.Profile);
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

        private PortInspection FindCurrentInspection()
        {
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
            try
            {
                TokenContext context = RuntimeResolver.CreateContext(Config, Plugin, port, String.Empty);
                string url = AppPaths.Expand(Plugin.Probe.UrlTemplate, context);
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
                    foreach (string marker in Plugin.Probe.Markers)
                        if (content.IndexOf(marker, StringComparison.Ordinal) < 0) return false;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void CompleteStart(PortInspection inspection)
        {
            ActivePort = inspection.Port;
            startingPort = 0;
            LastError = String.Empty;
            SaveRunningState(inspection);
            SetState(InstanceStateKind.Running, "Running on port " + inspection.Port);
            FileLog.Info(Config.Id + " is ready on port " + inspection.Port + " PID " + inspection.ProcessId);
            if (openWhenReady)
            {
                openWhenReady = false;
                OpenWebUi(inspection.Port);
            }
        }

        private void FailStart(string error)
        {
            int failedPort = startingPort;
            LastError = error;
            startingPort = 0;
            openWhenReady = false;
            try
            {
                PortInspection inspection = launchProcess != null && failedPort > 0 ? InspectPort(failedPort) : null;
                if (launchProcess != null && inspection != null && inspection.ProcessVerified && inspection.ProcessId > 0)
                {
                    using (Process process = Process.GetProcessById(inspection.ProcessId))
                    {
                        CommandRunner.KillProcessTree(process);
                        process.WaitForExit(3000);
                    }
                }
                else if (launchProcess != null && launchProcess.RootProcess != null && !launchProcess.RootProcess.HasExited)
                {
                    CommandRunner.KillProcessTree(launchProcess.RootProcess);
                    launchProcess.RootProcess.WaitForExit(3000);
                }
            }
            catch { }
            DisposeLaunchProcess();
            SetState(InstanceStateKind.Error, error);
            FileLog.Error(Config.Id + ": " + error);
        }

        private void FinishStop()
        {
            FileLog.Info(Config.Id + " stopped");
            ActivePort = 0;
            startingPort = 0;
            openWhenReady = false;
            string patchPath = companion == null ? null : companion.PatchPath;
            persistedState = null;
            companion = null;
            savedPort = 0;
            savedProcessId = 0;
            configurationStore.DeleteState(Config.Id);
            try { if (!String.IsNullOrWhiteSpace(patchPath) && File.Exists(patchPath)) File.Delete(patchPath); } catch { }
            DisposeLaunchProcess();
            SetState(InstanceStateKind.Stopped, "Stopped");
        }

        private void DisposeLaunchProcess()
        {
            if (launchProcess == null) return;
            try { if (launchProcess.RootProcess != null) launchProcess.RootProcess.Dispose(); } catch { }
            launchProcess = null;
        }

        private bool TryGracefulStop(PortInspection inspection, out string error)
        {
            bool requested = GracefulShutdownClient.Request(
                companion == null ? null : companion.PipeName,
                companion == null ? null : companion.Token,
                1500,
                out error);
            if (!requested) return false;
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
            persistedState.Port = inspection.Port;
            persistedState.ProcessId = inspection.ProcessId;
            persistedState.ProcessImagePath = inspection.Process == null ? String.Empty : inspection.Process.ImagePath;
            persistedState.ProcessStartTimeUtc = inspection.Process != null && inspection.Process.StartTimeUtc.HasValue ? inspection.Process.StartTimeUtc.Value.ToString("o") : String.Empty;
            persistedState.PipeName = companion == null ? persistedState.PipeName : companion.PipeName;
            persistedState.PipeToken = companion == null ? persistedState.PipeToken : companion.Token;
            persistedState.PatchPath = companion == null ? persistedState.PatchPath : companion.PatchPath;
            persistedState.UpdatedAt = DateTime.UtcNow.ToString("o");
            configurationStore.SaveState(Config.Id, persistedState);
            savedPort = inspection.Port;
            savedProcessId = inspection.ProcessId;
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

        private void OpenWebUi(int port)
        {
            TokenContext context = RuntimeResolver.CreateContext(Config, Plugin, port, String.Empty);
            string url = AppPaths.Expand(Plugin.Probe.UrlTemplate, context);
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                Process.Start(info);
                FileLog.Info("Opened Web UI for " + Config.Id + ": " + url);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                FileLog.Error("Could not open Web UI for " + Config.Id + ": " + exception.Message);
                MessageBox.Show(Localization.Format("Dialog.OpenBrowserFailed", url, exception.Message), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
