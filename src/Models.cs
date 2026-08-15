using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DeepSeekHarnessManager
{
    public enum InstanceStateKind
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Conflict,
        Updating,
        Error
    }

    public sealed class ManagerConfig
    {
        public int SchemaVersion { get; set; }
        public string Language { get; set; }
        public string DefaultInstanceId { get; set; }
        public List<InstanceConfig> Instances { get; set; }
    }

    public sealed class InstanceConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PluginId { get; set; }
        public string Profile { get; set; }
        public string Runtime { get; set; }
        public string SourceRoot { get; set; }
        public string Workspace { get; set; }
        public string DshHome { get; set; }
        public int PreferredPort { get; set; }
        public string PinnedVersion { get; set; }
    }

    public sealed class PluginDefinition
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string MarketplaceUrl { get; set; }
        public int DefaultPort { get; set; }
        public int FallbackPortCount { get; set; }
        public ProbeDefinition Probe { get; set; }
        public List<string> ProcessPatterns { get; set; }
        public List<RuntimeDefinition> Runtimes { get; set; }
        public UpdateDefinition Update { get; set; }
        public CompanionDefinition Companion { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public string DirectoryPath { get; set; }
    }

    public sealed class ProbeDefinition
    {
        public string UrlTemplate { get; set; }
        public List<string> Markers { get; set; }
    }

    public sealed class RuntimeDefinition
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Kind { get; set; }
        public List<string> CommandCandidates { get; set; }
        public List<string> RequiredPaths { get; set; }
        public List<string> PrefixArguments { get; set; }
        public List<string> LauncherArguments { get; set; }
        public List<string> ApplicationArguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string VersionFile { get; set; }
    }

    public sealed class UpdateDefinition
    {
        public string PackageName { get; set; }
        public string RegistryUrl { get; set; }
        public string ReleaseUrl { get; set; }
        public string GithubRepository { get; set; }
        public string GithubBranch { get; set; }
        public string BundledVersion { get; set; }
    }

    public sealed class CompanionDefinition
    {
        public bool Enabled { get; set; }
        public string Module { get; set; }
        public string EntryId { get; set; }
        public int BridgeProtocolVersion { get; set; }
    }

    public sealed class TokenContext
    {
        public string AppDirectory { get; set; }
        public string PluginDirectory { get; set; }
        public string CommandDirectory { get; set; }
        public string SourceRoot { get; set; }
        public string Workspace { get; set; }
        public string Profile { get; set; }
        public string PinnedVersion { get; set; }
        public string PatchPath { get; set; }
        public int Port { get; set; }
    }

    public sealed class RuntimeResolution
    {
        public RuntimeDefinition Definition { get; set; }
        public string CommandPath { get; set; }
        public string WorkingDirectory { get; set; }
        public List<string> Arguments { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; }
    }

    public sealed class ProcessIdentity
    {
        public int ProcessId { get; set; }
        public string Name { get; set; }
        public string ImagePath { get; set; }
        public string CommandLine { get; set; }
        public DateTime? StartTimeUtc { get; set; }
        public int SessionId { get; set; }
        public List<string> Services { get; set; }
    }

    public sealed class PortInspection
    {
        public InstanceStateKind Kind { get; set; }
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public ProcessIdentity Process { get; set; }
        public bool HttpVerified { get; set; }
        public bool ProcessVerified { get; set; }
        public bool BridgeVerified { get; set; }
        public string Detail { get; set; }
    }

    public sealed class PersistedInstanceState
    {
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string ProcessImagePath { get; set; }
        public string ProcessStartTimeUtc { get; set; }
        public string RuntimeId { get; set; }
        public string PipeName { get; set; }
        public string PipeToken { get; set; }
        public string PatchPath { get; set; }
        public string OutputLog { get; set; }
        public string ErrorLog { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    public sealed class UpdateInfo
    {
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool UpdateAvailable { get; set; }
        public string Detail { get; set; }
        public DateTime CheckedAtUtc { get; set; }
    }

    public sealed class UpdateOutcome
    {
        public bool Succeeded { get; set; }
        public string PreviousVersion { get; set; }
        public string TargetVersion { get; set; }
        public string FinalVersion { get; set; }
        public string Error { get; set; }
        public bool RollbackAttempted { get; set; }
        public bool RollbackSucceeded { get; set; }
        public string RollbackError { get; set; }
    }

    public sealed class UpdateJournalRecord
    {
        public string PluginId { get; set; }
        public string InstanceId { get; set; }
        public string RuntimeKind { get; set; }
        public string PreviousVersion { get; set; }
        public string TargetVersion { get; set; }
        public string PreviousPinnedVersion { get; set; }
        public string PreviousSourceSha { get; set; }
        public string Phase { get; set; }
        public string StartedAtUtc { get; set; }
        public string LastError { get; set; }
    }

    public sealed class UpdateCacheRecord
    {
        public string PluginId { get; set; }
        public string InstanceId { get; set; }
        public string RuntimeKind { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool UpdateAvailable { get; set; }
        public string CheckedAtUtc { get; set; }
        public string LastAttemptAtUtc { get; set; }
        public string LastSuccessAtUtc { get; set; }
        public string LastError { get; set; }
        public string Detail { get; set; }
    }

    public sealed class ManagedProcess
    {
        public Process RootProcess { get; set; }
        public string OutputLog { get; set; }
        public string ErrorLog { get; set; }
        public event EventHandler Exited;
        public bool ExitObserved { get; private set; }

        public void SignalExit()
        {
            ExitObserved = true;
            EventHandler handler = Exited;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); }
                catch { }
            }
        }
    }
}
