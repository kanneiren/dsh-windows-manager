using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DeepSeekHarnessManager
{
    public sealed class UpdateManager
    {
        private readonly ConfigurationStore configurationStore;
        private readonly ManagerConfig managerConfig;
        private readonly Func<string, IList<string>, string, int, CommandResult> commandExecutor;
        private readonly Func<InstanceConfig, PluginDefinition, ConfigurationStore, string, string, CommandResult> smokeTester;
        private readonly IManagerInteraction interaction;

        public UpdateManager(ConfigurationStore store, ManagerConfig config)
            : this(store, config, SilentManagerInteraction.Instance)
        {
        }

        public UpdateManager(ConfigurationStore store, ManagerConfig config, IManagerInteraction managerInteraction)
            : this(store, config, managerInteraction, CommandRunner.RunCapture, RuntimeSmokeTest.Run)
        {
        }

        internal UpdateManager(
            ConfigurationStore store,
            ManagerConfig config,
            Func<string, IList<string>, string, int, CommandResult> executeCommand,
            Func<InstanceConfig, PluginDefinition, ConfigurationStore, string, string, CommandResult> runSmokeTest)
            : this(store, config, SilentManagerInteraction.Instance, executeCommand, runSmokeTest)
        {
        }

        internal UpdateManager(
            ConfigurationStore store,
            ManagerConfig config,
            IManagerInteraction managerInteraction,
            Func<string, IList<string>, string, int, CommandResult> executeCommand,
            Func<InstanceConfig, PluginDefinition, ConfigurationStore, string, string, CommandResult> runSmokeTest)
        {
            configurationStore = store;
            managerConfig = config;
            interaction = managerInteraction ?? SilentManagerInteraction.Instance;
            commandExecutor = executeCommand;
            smokeTester = runSmokeTest;
            LogPendingJournals();
        }

        public Task<UpdateInfo> CheckAsync(InstanceController controller, bool force)
        {
            return Task.Factory.StartNew(delegate { return Check(controller, force); });
        }

        public static DateTime NextAutomaticCheckUtc(DateTime lastAttemptUtc)
        {
            DateTime normalized = lastAttemptUtc.Kind == DateTimeKind.Utc ? lastAttemptUtc : lastAttemptUtc.ToUniversalTime();
            DateTime next = normalized.AddHours(24);
            return next > DateTime.UtcNow ? next : DateTime.UtcNow.AddMinutes(1);
        }

        public UpdateInfo Check(InstanceController controller, bool force)
        {
            RuntimeResolution runtime = RuntimeAdapters.Resolve(controller.Config, controller.Plugin, controller.Config.PreferredPort, String.Empty);
            if (String.Equals(runtime.Definition.Kind, "source", StringComparison.OrdinalIgnoreCase))
                return CheckSource(controller, runtime, force);
            return CheckRegistry(controller, runtime, force);
        }

        public bool ExecuteConfirmedUpdate(InstanceController controller)
        {
            UpdateInfo info = controller.UpdateInfo;
            if (info == null || !info.UpdateAvailable)
            {
                interaction.Show(ManagerMessageKind.Information, Localization.Text("Update.None"));
                return false;
            }
            RuntimeResolution runtime;
            try { runtime = RuntimeAdapters.Resolve(controller.Config, controller.Plugin, controller.Config.PreferredPort, String.Empty); }
            catch (Exception exception)
            {
                interaction.Show(ManagerMessageKind.Error, exception.Message);
                return false;
            }
            if (!interaction.Confirm(ManagerConfirmKind.Question,
                Localization.Format("Update.Confirm", controller.Config.Name, info.InstalledVersion, info.LatestVersion),
                Localization.Text("Update.ConfirmTitle"))) return false;
            bool wasRunning = controller.State == InstanceStateKind.Running || controller.State == InstanceStateKind.Starting || controller.State == InstanceStateKind.Stopping;
            int restartPort = controller.ActivePort > 0 ? controller.ActivePort : controller.Config.PreferredPort;
            if (wasRunning && !controller.Stop(false)) return false;

            controller.SetUpdating(true, "Updating to " + info.LatestVersion);
            Task<UpdateOutcome> task = Task.Factory.StartNew(delegate { return ExecuteTransaction(runtime, controller, info.LatestVersion); });
            UpdateOutcome outcome = interaction.WaitForUpdate(Localization.Text("Update.ProgressTitle"), task);
            if (outcome == null) outcome = new UpdateOutcome { Error = Localization.Text("Update.Incomplete") };
            WriteFinalCache(controller, runtime, info.LatestVersion, outcome);

            controller.InstalledVersion = String.IsNullOrWhiteSpace(outcome.FinalVersion)
                ? RuntimeAdapters.ResolveInstalledVersion(controller.Config, controller.Plugin)
                : outcome.FinalVersion;
            controller.UpdateInfo = new UpdateInfo
            {
                InstalledVersion = controller.InstalledVersion,
                LatestVersion = info.LatestVersion,
                UpdateAvailable = !outcome.Succeeded,
                Detail = outcome.Succeeded ? "Updated and verified" : "Update failed",
                CheckedAtUtc = DateTime.UtcNow
            };
            controller.SetUpdating(false, outcome.Succeeded ? "Stopped after update" : "Update failed");

            if (outcome.Succeeded)
            {
                if (interaction.Confirm(ManagerConfirmKind.Information, Localization.Text("Update.Completed"), Localization.Text("App.Title")))
                    controller.Start(restartPort, true);
                return true;
            }

            if (outcome.RollbackSucceeded)
            {
                if (interaction.Confirm(ManagerConfirmKind.Warning,
                    Localization.Format("Update.RolledBack", outcome.Error, outcome.PreviousVersion),
                    Localization.Text("App.Title")))
                    controller.Start(restartPort, true);
            }
            else
            {
                string detail = outcome.RollbackAttempted
                    ? Localization.Format("Update.RollbackFailed", outcome.Error, outcome.RollbackError)
                    : outcome.Error;
                interaction.Show(ManagerMessageKind.Error, Localization.Format("Update.Failed", detail));
            }
            return false;
        }

        private UpdateInfo CheckRegistry(InstanceController controller, RuntimeResolution runtime, bool force)
        {
            string cachePath = GetCachePath(controller, runtime);
            UpdateCacheRecord cache = ReadCache(cachePath);
            if (!force && IsAutomaticCheckDeferred(cache)) return CreateCachedInfo(cache, runtime.Version);

            DateTime attempt = DateTime.UtcNow;
            if (cache == null) cache = NewCache(controller, runtime);
            cache.LastAttemptAtUtc = attempt.ToString("o");
            cache.CheckedAtUtc = cache.LastAttemptAtUtc;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                using (TimeoutWebClient client = new TimeoutWebClient(6000))
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "DeepSeekHarnessManager/1.0";
                    string json = client.DownloadString(controller.Plugin.Update.RegistryUrl);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(json);
                    object versionValue;
                    if (!value.TryGetValue("version", out versionValue)) throw new InvalidDataException("The npm registry response has no version.");
                    cache.LatestVersion = Convert.ToString(versionValue);
                }
                cache.InstalledVersion = runtime.Version;
                cache.LastSuccessAtUtc = DateTime.UtcNow.ToString("o");
                cache.LastError = String.Empty;
                cache.Detail = "Up to date";
                JsonStore.Write(cachePath, cache);
            }
            catch (Exception exception)
            {
                cache.LastError = exception.Message;
                cache.Detail = "Automatic update check failed";
                JsonStore.Write(cachePath, cache);
                throw;
            }
            string installed = runtime.Version;
            if (String.IsNullOrWhiteSpace(installed)) installed = controller.InstalledVersion;
            UpdateInfo info = new UpdateInfo();
            info.InstalledVersion = installed;
            info.LatestVersion = cache.LatestVersion;
            info.UpdateAvailable = !String.IsNullOrWhiteSpace(installed) && SemanticVersion.Compare(cache.LatestVersion, installed) > 0;
            info.Detail = String.IsNullOrWhiteSpace(installed) ? "Installed version could not be verified" : (info.UpdateAvailable ? "Update available" : "Up to date");
            info.CheckedAtUtc = DateTime.UtcNow;
            cache.UpdateAvailable = info.UpdateAvailable;
            cache.Detail = info.Detail;
            JsonStore.Write(cachePath, cache);
            return info;
        }

        private CommandResult RunCommand(InstanceController controller, string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds)
        {
            if (String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase))
                return RuntimeAdapters.Get(controller.Config).RunCommand(controller.Config, command, arguments, workingDirectory, timeoutMilliseconds);
            return commandExecutor(command, arguments, workingDirectory, timeoutMilliseconds);
        }

        private static bool IsWsl(InstanceController controller)
        {
            return String.Equals(controller.Config.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase);
        }

        private UpdateInfo CheckSource(InstanceController controller, RuntimeResolution runtime, bool force)
        {
            string cachePath = GetCachePath(controller, runtime);
            UpdateCacheRecord cache = ReadCache(cachePath);
            if (!force && IsAutomaticCheckDeferred(cache)) return CreateCachedInfo(cache, runtime.Version);
            if (cache == null) cache = NewCache(controller, runtime);
            cache.LastAttemptAtUtc = DateTime.UtcNow.ToString("o");
            cache.CheckedAtUtc = cache.LastAttemptAtUtc;
            string git = IsWsl(controller) ? "git" : (AppPaths.FindOnPath("git.exe") ?? AppPaths.FindOnPath("git"));
            if (String.IsNullOrWhiteSpace(git)) throw new InvalidOperationException("Git was not found.");
            try
            {
                CommandResult local = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "rev-parse", "HEAD" }, controller.Config.SourceRoot, 8000);
                if (local.ExitCode != 0) throw new InvalidOperationException(local.StandardError.Trim());
                string repository = controller.Plugin.Update.GithubRepository;
                string branch = controller.Plugin.Update.GithubBranch;
                string url = "https://github.com/" + repository + ".git";
                CommandResult remote = RunCommand(controller, git, new string[] { "ls-remote", url, "refs/heads/" + branch }, controller.Config.SourceRoot, 15000);
                if (remote.ExitCode != 0) throw new InvalidOperationException(remote.TimedOut ? "Git source update check timed out after 15 seconds." : remote.StandardError.Trim());
                string localSha = local.StandardOutput.Trim();
                string[] remoteParts = remote.StandardOutput.Trim().Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string remoteSha = remoteParts.Length == 0 ? String.Empty : remoteParts[0];
                cache.InstalledVersion = runtime.Version + "+" + ShortSha(localSha);
                cache.LatestVersion = ShortSha(remoteSha);
                cache.UpdateAvailable = !String.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase);
                cache.Detail = cache.UpdateAvailable ? "Source update available" : "Source checkout matches " + branch;
                cache.LastSuccessAtUtc = DateTime.UtcNow.ToString("o");
                cache.LastError = String.Empty;
                JsonStore.Write(cachePath, cache);
                UpdateInfo info = new UpdateInfo();
                info.InstalledVersion = cache.InstalledVersion;
                info.LatestVersion = cache.LatestVersion;
                info.UpdateAvailable = cache.UpdateAvailable;
                info.Detail = cache.Detail;
                info.CheckedAtUtc = DateTime.UtcNow;
                return info;
            }
            catch (Exception exception)
            {
                cache.LastError = exception.Message;
                cache.Detail = "Automatic source update check failed";
                JsonStore.Write(cachePath, cache);
                throw;
            }
        }

        private static string GetCachePath(InstanceController controller, RuntimeResolution runtime)
        {
            string name = controller.Plugin.Id + "-" + controller.Config.Id + "-" + runtime.Definition.Kind;
            return Path.Combine(AppPaths.UpdateDirectory, AppPaths.SafeFileName(name) + ".json");
        }

        private static UpdateCacheRecord ReadCache(string path)
        {
            if (!File.Exists(path)) return null;
            try { return JsonStore.Read<UpdateCacheRecord>(path); }
            catch { return null; }
        }

        private static UpdateCacheRecord NewCache(InstanceController controller, RuntimeResolution runtime)
        {
            UpdateCacheRecord cache = new UpdateCacheRecord();
            cache.PluginId = controller.Plugin.Id;
            cache.InstanceId = controller.Config.Id;
            cache.RuntimeKind = runtime.Definition.Kind;
            cache.InstalledVersion = runtime.Version;
            return cache;
        }

        private static bool IsAutomaticCheckDeferred(UpdateCacheRecord cache)
        {
            if (cache == null) return false;
            string value = !String.IsNullOrWhiteSpace(cache.LastAttemptAtUtc) ? cache.LastAttemptAtUtc : cache.CheckedAtUtc;
            DateTime attempt;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out attempt) &&
                DateTime.UtcNow - attempt.ToUniversalTime() < TimeSpan.FromHours(24);
        }

        private static UpdateInfo CreateCachedInfo(UpdateCacheRecord cache, string fallbackInstalledVersion)
        {
            UpdateInfo info = new UpdateInfo();
            info.InstalledVersion = String.IsNullOrWhiteSpace(cache.InstalledVersion) ? fallbackInstalledVersion : cache.InstalledVersion;
            info.LatestVersion = cache.LatestVersion ?? String.Empty;
            info.UpdateAvailable = cache.UpdateAvailable;
            info.Detail = String.IsNullOrWhiteSpace(cache.LastError) ? (cache.Detail ?? "Cached update result") : "Last check failed; automatic retry deferred for 24 hours";
            DateTime checkedAt;
            info.CheckedAtUtc = DateTime.TryParse(cache.LastAttemptAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out checkedAt) ? checkedAt.ToUniversalTime() : DateTime.UtcNow;
            return info;
        }

        internal UpdateOutcome ExecuteTransaction(RuntimeResolution runtime, InstanceController controller, string latestVersion)
        {
            string kind = runtime.Definition.Kind;
            UpdateOutcome outcome = new UpdateOutcome();
            outcome.PreviousVersion = runtime.Version ?? String.Empty;
            outcome.TargetVersion = latestVersion;
            outcome.FinalVersion = outcome.PreviousVersion;
            string previousPinnedVersion = controller.Config.PinnedVersion ?? String.Empty;
            string previousSourceSha = String.Empty;

            if (String.Equals(kind, "global", StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(outcome.PreviousVersion))
            {
                outcome.Error = "The installed version is unknown, so an exact rollback cannot be guaranteed. The update was not started.";
                return outcome;
            }

            if (String.Equals(kind, "source", StringComparison.OrdinalIgnoreCase))
            {
                string git = IsWsl(controller) ? "git" : (AppPaths.FindOnPath("git.exe") ?? AppPaths.FindOnPath("git"));
                if (String.IsNullOrWhiteSpace(git))
                {
                    outcome.Error = "Git was not found.";
                    return outcome;
                }
                CommandResult status = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "status", "--porcelain" }, controller.Config.SourceRoot, 10000);
                if (!Succeeded(status) || !String.IsNullOrWhiteSpace(status.StandardOutput))
                {
                    outcome.Error = "The source checkout has local changes. Automatic update was refused.";
                    return outcome;
                }
                CommandResult sha = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "rev-parse", "HEAD" }, controller.Config.SourceRoot, 10000);
                if (!Succeeded(sha))
                {
                    outcome.Error = Describe(sha);
                    return outcome;
                }
                previousSourceSha = sha.StandardOutput.Trim();
                outcome.PreviousVersion = (runtime.Version ?? String.Empty) + "+" + ShortSha(previousSourceSha);
                outcome.FinalVersion = outcome.PreviousVersion;
            }

            string journalPath = GetJournalPath(controller, runtime);
            UpdateJournalRecord journal = new UpdateJournalRecord
            {
                PluginId = controller.Plugin.Id,
                InstanceId = controller.Config.Id,
                RuntimeKind = kind,
                PreviousVersion = outcome.PreviousVersion,
                TargetVersion = latestVersion,
                PreviousPinnedVersion = previousPinnedVersion,
                PreviousSourceSha = previousSourceSha,
                Phase = "updating",
                StartedAtUtc = DateTime.UtcNow.ToString("o")
            };
            JsonStore.Write(journalPath, journal);

            try
            {
                CommandResult update = ExecuteUpdate(runtime, controller, latestVersion);
                if (!Succeeded(update)) throw new InvalidOperationException(Describe(update));
                journal.Phase = "smoke-testing";
                JsonStore.Write(journalPath, journal);
                string finalSourceSha = String.Empty;
                if (String.Equals(kind, "source", StringComparison.OrdinalIgnoreCase))
                {
                    finalSourceSha = ReadSourceSha(controller);
                    if (String.IsNullOrWhiteSpace(finalSourceSha) || !finalSourceSha.StartsWith(latestVersion, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The updated source commit does not match the checked target " + latestVersion + ".");
                }
                CommandResult smoke = smokeTester(controller.Config, controller.Plugin, configurationStore,
                    String.Equals(kind, "source", StringComparison.OrdinalIgnoreCase) ? String.Empty : latestVersion,
                    runtime.Definition.Id);
                if (!Succeeded(smoke)) throw new InvalidOperationException("Compatibility smoke test failed: " + Describe(smoke));

                outcome.Succeeded = true;
                if (String.Equals(kind, "source", StringComparison.OrdinalIgnoreCase))
                {
                    string resolvedVersion = RuntimeAdapters.ResolveInstalledVersion(controller.Config, controller.Plugin);
                    outcome.FinalVersion = (resolvedVersion ?? String.Empty) + "+" + ShortSha(finalSourceSha);
                }
                else outcome.FinalVersion = latestVersion;
                DeleteJournal(journalPath);
                return outcome;
            }
            catch (Exception exception)
            {
                outcome.Error = exception.Message;
                outcome.RollbackAttempted = true;
                journal.Phase = "rolling-back";
                journal.LastError = outcome.Error;
                JsonStore.Write(journalPath, journal);
                try
                {
                    CommandResult rollback = ExecuteRollback(runtime, controller, previousPinnedVersion, previousSourceSha, outcome.PreviousVersion);
                    if (!Succeeded(rollback)) throw new InvalidOperationException(Describe(rollback));
                    CommandResult rollbackSmoke = smokeTester(controller.Config, controller.Plugin, configurationStore,
                        String.Equals(kind, "source", StringComparison.OrdinalIgnoreCase) ? String.Empty : BaseVersion(outcome.PreviousVersion),
                        runtime.Definition.Id);
                    if (!Succeeded(rollbackSmoke)) throw new InvalidOperationException("Rollback smoke test failed: " + Describe(rollbackSmoke));
                    outcome.RollbackSucceeded = true;
                    outcome.FinalVersion = outcome.PreviousVersion;
                    DeleteJournal(journalPath);
                }
                catch (Exception rollbackException)
                {
                    outcome.RollbackError = rollbackException.Message;
                    journal.Phase = "rollback-failed";
                    journal.LastError = outcome.Error + " | " + outcome.RollbackError;
                    JsonStore.Write(journalPath, journal);
                }
                return outcome;
            }
        }

        private CommandResult ExecuteUpdate(RuntimeResolution runtime, InstanceController controller, string latestVersion)
        {
            if (String.Equals(runtime.Definition.Kind, "npx", StringComparison.OrdinalIgnoreCase))
            {
                controller.Config.PinnedVersion = latestVersion;
                configurationStore.Save(managerConfig);
                return Success("Pinned npx version updated.");
            }
            if (String.Equals(runtime.Definition.Kind, "global", StringComparison.OrdinalIgnoreCase))
            {
                string npm = IsWsl(controller) ? "npm" : AppPaths.FindOnPath("npm.cmd");
                if (String.IsNullOrWhiteSpace(npm)) return Failure("npm was not found.");
                return RunCommand(controller, npm, new string[] { "install", "--global", controller.Plugin.Update.PackageName + "@" + latestVersion }, controller.Config.Workspace, 300000);
            }
            if (String.Equals(runtime.Definition.Kind, "source", StringComparison.OrdinalIgnoreCase))
            {
                string git = IsWsl(controller) ? "git" : (AppPaths.FindOnPath("git.exe") ?? AppPaths.FindOnPath("git"));
                string pnpm = IsWsl(controller) ? "pnpm" : AppPaths.FindOnPath("pnpm.cmd");
                if (String.IsNullOrWhiteSpace(git) || String.IsNullOrWhiteSpace(pnpm)) return Failure("Git and pnpm are required for source updates.");
                CommandResult pull = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "pull", "--ff-only" }, controller.Config.SourceRoot, 120000);
                if (!Succeeded(pull)) return pull;
                CommandResult install = RunCommand(controller, pnpm, new string[] { "install", "--frozen-lockfile" }, controller.Config.SourceRoot, 300000);
                if (!Succeeded(install)) return install;
                return RunCommand(controller, pnpm, new string[] { "run", "build" }, controller.Config.SourceRoot, 600000);
            }
            return Failure("Unsupported update runtime: " + runtime.Definition.Kind);
        }

        private CommandResult ExecuteRollback(RuntimeResolution runtime, InstanceController controller, string previousPinnedVersion, string previousSourceSha, string previousVersion)
        {
            if (String.Equals(runtime.Definition.Kind, "npx", StringComparison.OrdinalIgnoreCase))
            {
                controller.Config.PinnedVersion = previousPinnedVersion;
                configurationStore.Save(managerConfig);
                return Success("Pinned npx version restored.");
            }
            if (String.Equals(runtime.Definition.Kind, "global", StringComparison.OrdinalIgnoreCase))
            {
                string npm = IsWsl(controller) ? "npm" : AppPaths.FindOnPath("npm.cmd");
                if (String.IsNullOrWhiteSpace(npm)) return Failure("npm was not found for rollback.");
                return RunCommand(controller, npm, new string[] { "install", "--global", controller.Plugin.Update.PackageName + "@" + BaseVersion(previousVersion) }, controller.Config.Workspace, 300000);
            }
            if (String.Equals(runtime.Definition.Kind, "source", StringComparison.OrdinalIgnoreCase))
            {
                string git = IsWsl(controller) ? "git" : (AppPaths.FindOnPath("git.exe") ?? AppPaths.FindOnPath("git"));
                string pnpm = IsWsl(controller) ? "pnpm" : AppPaths.FindOnPath("pnpm.cmd");
                if (String.IsNullOrWhiteSpace(git) || String.IsNullOrWhiteSpace(pnpm)) return Failure("Git and pnpm are required for source rollback.");
                CommandResult status = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "status", "--porcelain" }, controller.Config.SourceRoot, 10000);
                if (!Succeeded(status) || !String.IsNullOrWhiteSpace(status.StandardOutput)) return Failure("Source rollback was refused because the checkout changed during the update.");
                CommandResult reset = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "reset", "--hard", previousSourceSha }, controller.Config.SourceRoot, 30000);
                if (!Succeeded(reset)) return reset;
                CommandResult install = RunCommand(controller, pnpm, new string[] { "install", "--frozen-lockfile" }, controller.Config.SourceRoot, 300000);
                if (!Succeeded(install)) return install;
                return RunCommand(controller, pnpm, new string[] { "run", "build" }, controller.Config.SourceRoot, 600000);
            }
            return Failure("Unsupported rollback runtime: " + runtime.Definition.Kind);
        }

        private static bool Succeeded(CommandResult result)
        {
            return result != null && result.ExitCode == 0 && !result.TimedOut;
        }

        private static string Describe(CommandResult result)
        {
            if (result == null) return "The command did not return a result.";
            string detail = ((result.StandardError ?? String.Empty) + Environment.NewLine + (result.StandardOutput ?? String.Empty)).Trim();
            if (result.TimedOut) return "The command timed out." + (detail.Length == 0 ? String.Empty : " " + detail);
            return detail.Length == 0 ? "The command failed with exit code " + result.ExitCode + "." : detail;
        }

        private static CommandResult Success(string text)
        {
            return new CommandResult { ExitCode = 0, StandardOutput = text, StandardError = String.Empty };
        }

        private static CommandResult Failure(string text)
        {
            return new CommandResult { ExitCode = 1, StandardOutput = String.Empty, StandardError = text };
        }

        private static string BaseVersion(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            string normalized = value.Trim();
            int plus = normalized.IndexOf('+');
            return plus < 0 ? normalized : normalized.Substring(0, plus);
        }

        private string ReadSourceSha(InstanceController controller)
        {
            string git = IsWsl(controller) ? "git" : (AppPaths.FindOnPath("git.exe") ?? AppPaths.FindOnPath("git"));
            if (String.IsNullOrWhiteSpace(git)) return String.Empty;
            CommandResult result = RunCommand(controller, git, new string[] { "-C", controller.Config.SourceRoot, "rev-parse", "HEAD" }, controller.Config.SourceRoot, 10000);
            return Succeeded(result) ? result.StandardOutput.Trim() : String.Empty;
        }

        private static string GetJournalPath(InstanceController controller, RuntimeResolution runtime)
        {
            string name = controller.Plugin.Id + "-" + controller.Config.Id + "-" + runtime.Definition.Kind + ".journal.json";
            return Path.Combine(AppPaths.UpdateDirectory, AppPaths.SafeFileName(name));
        }

        private static void DeleteJournal(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) { FileLog.Warn("Could not delete completed update journal: " + exception.Message); }
        }

        private static void LogPendingJournals()
        {
            try
            {
                if (!Directory.Exists(AppPaths.UpdateDirectory)) return;
                foreach (string path in Directory.GetFiles(AppPaths.UpdateDirectory, "*.journal.json"))
                    FileLog.Warn("Incomplete update journal requires review: " + path);
            }
            catch (Exception exception) { FileLog.Warn("Could not inspect update journals: " + exception.Message); }
        }

        private static void WriteFinalCache(InstanceController controller, RuntimeResolution runtime, string latestVersion, UpdateOutcome outcome)
        {
            try
            {
                string path = GetCachePath(controller, runtime);
                UpdateCacheRecord cache = ReadCache(path) ?? NewCache(controller, runtime);
                string now = DateTime.UtcNow.ToString("o");
                cache.InstalledVersion = outcome.FinalVersion ?? String.Empty;
                cache.LatestVersion = latestVersion ?? String.Empty;
                cache.UpdateAvailable = !outcome.Succeeded;
                cache.CheckedAtUtc = now;
                cache.LastAttemptAtUtc = now;
                cache.LastSuccessAtUtc = outcome.Succeeded || outcome.RollbackSucceeded ? now : cache.LastSuccessAtUtc;
                cache.LastError = outcome.Succeeded ? String.Empty : outcome.Error ?? String.Empty;
                cache.Detail = outcome.Succeeded ? "Updated and compatibility-tested" : (outcome.RollbackSucceeded ? "Update failed; previous version restored" : "Update and rollback failed");
                JsonStore.Write(path, cache);
            }
            catch (Exception exception) { FileLog.Warn("Could not update the post-update cache: " + exception.Message); }
        }

        private static string ShortSha(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "unknown";
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int timeoutMilliseconds;

            public TimeoutWebClient(int timeout)
            {
                timeoutMilliseconds = timeout;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                request.Timeout = timeoutMilliseconds;
                HttpWebRequest http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = timeoutMilliseconds;
                return request;
            }
        }
    }

    public static class SemanticVersion
    {
        public static int Compare(string left, string right)
        {
            VersionParts first = Parse(left);
            VersionParts second = Parse(right);
            int value = first.Major.CompareTo(second.Major);
            if (value != 0) return value;
            value = first.Minor.CompareTo(second.Minor);
            if (value != 0) return value;
            value = first.Patch.CompareTo(second.Patch);
            if (value != 0) return value;
            if (first.PreRelease.Count == 0 && second.PreRelease.Count > 0) return 1;
            if (first.PreRelease.Count > 0 && second.PreRelease.Count == 0) return -1;
            int length = Math.Max(first.PreRelease.Count, second.PreRelease.Count);
            int i;
            for (i = 0; i < length; i++)
            {
                if (i >= first.PreRelease.Count) return -1;
                if (i >= second.PreRelease.Count) return 1;
                string a = first.PreRelease[i];
                string b = second.PreRelease[i];
                int numberA;
                int numberB;
                bool numericA = Int32.TryParse(a, out numberA);
                bool numericB = Int32.TryParse(b, out numberB);
                if (numericA && numericB)
                {
                    value = numberA.CompareTo(numberB);
                    if (value != 0) return value;
                }
                else if (numericA != numericB) return numericA ? -1 : 1;
                else
                {
                    value = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                    if (value != 0) return value;
                }
            }
            return 0;
        }

        private static VersionParts Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return new VersionParts();
            string normalized = value.Trim().Trim('"');
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            int plus = normalized.IndexOf('+');
            if (plus >= 0) normalized = normalized.Substring(0, plus);
            string prerelease = String.Empty;
            int dash = normalized.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = normalized.Substring(dash + 1);
                normalized = normalized.Substring(0, dash);
            }
            string[] core = normalized.Split('.');
            VersionParts parts = new VersionParts();
            Int32.TryParse(core.Length > 0 ? core[0] : "0", out parts.Major);
            Int32.TryParse(core.Length > 1 ? core[1] : "0", out parts.Minor);
            Int32.TryParse(core.Length > 2 ? core[2] : "0", out parts.Patch);
            if (!String.IsNullOrWhiteSpace(prerelease)) parts.PreRelease.AddRange(prerelease.Split('.'));
            return parts;
        }

        private sealed class VersionParts
        {
            public int Major;
            public int Minor;
            public int Patch;
            public readonly List<string> PreRelease = new List<string>();
        }
    }
}
