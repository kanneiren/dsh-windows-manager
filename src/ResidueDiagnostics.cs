using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessManager
{
    public enum ResidueKind
    {
        None,
        StaleManagerState,
        DshOrphanProcess,
        WslForwardingResidue,
        ExternalProcess
    }

    public enum InDistroListenerProbe
    {
        NoListener,
        ListenerExists,
        Unavailable
    }

    public enum ResidueRepairAction
    {
        Cancel,
        ResetState,
        ClearOrphanAndRestart,
        RestartDistroAndRestart,
        UseAlternatePort
    }

    public sealed class ResidueDiagnosis
    {
        public ResidueKind Kind;
        public int Port;
        public ProcessIdentity Holder;
        public int PersistedProcessId;
        public bool DistroProbeVerified;
        public string Evidence = String.Empty;
    }

    public sealed class ResidueRepairChoice
    {
        public ResidueRepairAction Action;
        public int Port;
    }

    /// <summary>
    /// On-demand port residue classifier. Nothing here runs on a timer; the
    /// tray diagnosis action is the only entry point, so WSL probes execute
    /// only after an explicit user click.
    /// </summary>
    public static class ResidueInspector
    {
        public static ResidueDiagnosis Diagnose(InstanceConfig config, PluginDefinition plugin, PersistedInstanceState persisted)
        {
            ResidueDiagnosis diagnosis = new ResidueDiagnosis();
            diagnosis.Port = config.PreferredPort;
            diagnosis.PersistedProcessId = persisted == null ? 0 : persisted.ProcessId;
            StringBuilder evidence = new StringBuilder();
            evidence.AppendLine(Localization.Format("Residue.EvidencePort", config.PreferredPort));

            bool stateStale = IsStateStale(persisted, evidence);

            bool httpAlive = RuntimeHttpProbe.Verify(config, plugin, config.PreferredPort, 1500);
            evidence.AppendLine(httpAlive
                ? Localization.Text("Residue.EvidenceHttpAlive")
                : Localization.Text("Residue.EvidenceHttpDead"));

            int holderPid = PortMap.GetPreferredListenerProcessId(config.PreferredPort);
            if (holderPid == 0)
            {
                diagnosis.Kind = stateStale ? ResidueKind.StaleManagerState : ResidueKind.None;
                diagnosis.Evidence = evidence.ToString();
                return diagnosis;
            }

            diagnosis.Holder = ProcessInspector.Get(holderPid, true);
            AppendHolder(evidence, diagnosis.Holder);

            if (String.Equals(config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
            {
                if (IsWslForwardingProcess(diagnosis.Holder))
                {
                    InDistroListenerProbe probe = ProbeDistroListener(config.WslDistro, config.PreferredPort);
                    AppendDistroProbe(evidence, probe, config.PreferredPort);
                    diagnosis.DistroProbeVerified = probe != InDistroListenerProbe.Unavailable;
                    diagnosis.Kind = DecideWslRelayKind(probe);
                }
                else diagnosis.Kind = ResidueKind.ExternalProcess;
            }
            else
            {
                diagnosis.Kind = ReferencesDshRuntime(diagnosis.Holder, config, plugin)
                    ? ResidueKind.DshOrphanProcess
                    : ResidueKind.ExternalProcess;
            }

            diagnosis.Evidence = evidence.ToString();
            return diagnosis;
        }

        internal static ResidueKind DecideWslRelayKind(InDistroListenerProbe probe)
        {
            return probe == InDistroListenerProbe.ListenerExists
                ? ResidueKind.ExternalProcess
                : ResidueKind.WslForwardingResidue;
        }

        internal static bool IsWslForwardingProcess(ProcessIdentity identity)
        {
            string name = identity == null ? String.Empty : (identity.Name ?? String.Empty);
            string image = identity == null ? String.Empty : (identity.ImagePath ?? String.Empty);
            int slash = image.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0 && slash + 1 < image.Length) name = image.Substring(slash + 1);
            return String.Equals(name, "wslrelay.exe", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "wslhost.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ReferencesDshRuntime(ProcessIdentity identity, InstanceConfig config, PluginDefinition plugin)
        {
            string commandLine = identity == null ? String.Empty : (identity.CommandLine ?? String.Empty);
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            if (plugin != null && plugin.ProcessPatterns != null)
            {
                foreach (string pattern in plugin.ProcessPatterns)
                {
                    try
                    {
                        if (Regex.IsMatch(commandLine, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
                    }
                    catch (ArgumentException) { }
                }
            }
            if (!String.IsNullOrWhiteSpace(config.SourceRoot) && commandLine.IndexOf(config.SourceRoot, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (commandLine.IndexOf("@deepseek-ai", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (commandLine.IndexOf(AppPaths.RuntimeDirectory, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static InDistroListenerProbe ProbeDistroListener(string distro, int port)
        {
            if (String.IsNullOrWhiteSpace(distro)) return InDistroListenerProbe.Unavailable;
            try
            {
                WslRuntimeAdapter adapter = new WslRuntimeAdapter();
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                CommandResult result = adapter.RunCommand(distro, "ss", new string[] { "-ltn" }, home, 8000);
                if (result == null || result.TimedOut || result.ExitCode != 0) return InDistroListenerProbe.Unavailable;
                string suffix = ":" + Convert.ToString(port, CultureInfo.InvariantCulture);
                string[] lines = (result.StandardOutput ?? String.Empty).Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.IndexOf("LISTEN", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (line.IndexOf(suffix + " ", StringComparison.Ordinal) >= 0 || line.TrimEnd().EndsWith(suffix, StringComparison.Ordinal))
                        return InDistroListenerProbe.ListenerExists;
                }
                return InDistroListenerProbe.NoListener;
            }
            catch
            {
                return InDistroListenerProbe.Unavailable;
            }
        }

        private static bool IsStateStale(PersistedInstanceState persisted, StringBuilder evidence)
        {
            if (persisted == null || persisted.ProcessId <= 0) return false;
            ProcessIdentity current = ProcessInspector.GetBasic(persisted.ProcessId);
            if (current == null || current.ProcessId <= 0
                || String.Equals(current.Name, "unknown", StringComparison.Ordinal))
            {
                evidence.AppendLine(Localization.Format("Residue.EvidenceStateStale", persisted.ProcessId));
                return true;
            }
            DateTime parsed;
            if (current.StartTimeUtc.HasValue
                && !String.IsNullOrWhiteSpace(persisted.StartedAtUtc)
                && DateTime.TryParse(persisted.StartedAtUtc, null, DateTimeStyles.RoundtripKind, out parsed))
            {
                DateTime recorded = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                if (Math.Abs((current.StartTimeUtc.Value - recorded).TotalSeconds) > 2)
                {
                    evidence.AppendLine(Localization.Format("Residue.EvidenceStateStale", persisted.ProcessId));
                    return true;
                }
            }
            return false;
        }

        private static void AppendHolder(StringBuilder evidence, ProcessIdentity holder)
        {
            if (holder == null) return;
            evidence.AppendLine(Localization.Format("Residue.EvidenceHolder",
                String.IsNullOrWhiteSpace(holder.Name) ? Localization.Text("Version.Unknown") : holder.Name,
                holder.ProcessId));
            if (!String.IsNullOrWhiteSpace(holder.ImagePath))
                evidence.AppendLine(Localization.Format("Residue.EvidenceImage", holder.ImagePath));
            if (holder.StartTimeUtc.HasValue)
                evidence.AppendLine(Localization.Format("Residue.EvidenceStarted", holder.StartTimeUtc.Value.ToLocalTime().ToString("G")));
            if (!String.IsNullOrWhiteSpace(holder.CommandLine))
                evidence.AppendLine(Localization.Format("Residue.EvidenceCommand", holder.CommandLine));
        }

        private static void AppendDistroProbe(StringBuilder evidence, InDistroListenerProbe probe, int port)
        {
            if (probe == InDistroListenerProbe.NoListener)
                evidence.AppendLine(Localization.Format("Residue.EvidenceDistroNoListener", port));
            else if (probe == InDistroListenerProbe.ListenerExists)
                evidence.AppendLine(Localization.Format("Residue.EvidenceDistroListener", port));
            else
                evidence.AppendLine(Localization.Text("Residue.EvidenceDistroUnavailable"));
        }
    }
}
