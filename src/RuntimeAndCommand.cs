using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DeepSeekHarnessManager
{
    public static class RuntimeResolver
    {
        public static RuntimeResolution Resolve(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            List<RuntimeDefinition> candidates = new List<RuntimeDefinition>();
            if (!String.Equals(instance.Runtime, "auto", StringComparison.OrdinalIgnoreCase))
            {
                RuntimeDefinition selected = plugin.Runtimes.FirstOrDefault(delegate(RuntimeDefinition item) { return item.Id.Equals(instance.Runtime, StringComparison.OrdinalIgnoreCase); });
                if (selected != null) candidates.Add(selected);
            }
            else
            {
                candidates.AddRange(plugin.Runtimes);
            }

            List<string> failures = new List<string>();
            foreach (RuntimeDefinition definition in candidates)
            {
                TokenContext context = CreateContext(instance, plugin, port, patchPath);
                if (String.Equals(definition.Kind, "source", StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(instance.SourceRoot))
                {
                    failures.Add(definition.Id + ": sourceRoot is not configured");
                    continue;
                }
                string command = AppPaths.FindExecutable(definition.CommandCandidates, context);
                if (String.IsNullOrWhiteSpace(command))
                {
                    failures.Add(definition.Id + ": command not found");
                    continue;
                }
                context.CommandDirectory = Path.GetDirectoryName(command);
                bool requirementsMet = true;
                if (definition.RequiredPaths != null)
                {
                    foreach (string requiredValue in definition.RequiredPaths)
                    {
                        string required = AppPaths.Expand(requiredValue, context);
                        if (!File.Exists(required) && !Directory.Exists(required))
                        {
                            failures.Add(definition.Id + ": missing " + required);
                            requirementsMet = false;
                            break;
                        }
                    }
                }
                if (!requirementsMet) continue;
                string workingDirectory = AppPaths.Expand(definition.WorkingDirectory, context);
                if (String.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                {
                    failures.Add(definition.Id + ": working directory not found: " + workingDirectory);
                    continue;
                }

                List<string> arguments = new List<string>();
                AddExpanded(arguments, definition.PrefixArguments, context);
                AddExpanded(arguments, definition.LauncherArguments, context);
                if (!String.IsNullOrWhiteSpace(patchPath))
                {
                    arguments.Add("--patch");
                    arguments.Add(patchPath);
                }
                AddExpanded(arguments, definition.ApplicationArguments, context);

                RuntimeResolution resolution = new RuntimeResolution();
                resolution.Definition = definition;
                resolution.CommandPath = command;
                resolution.WorkingDirectory = workingDirectory;
                resolution.Arguments = arguments;
                resolution.Version = ResolveVersion(instance, definition, context);
                resolution.Description = definition.Label + " (" + command + ")";
                resolution.EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!String.IsNullOrWhiteSpace(instance.DshHome)) resolution.EnvironmentVariables["DSH_HOME"] = instance.DshHome;
                return resolution;
            }
            throw new InvalidOperationException("No usable runtime was found. " + String.Join("; ", failures.ToArray()));
        }

        public static string ResolveInstalledVersion(InstanceConfig instance, PluginDefinition plugin)
        {
            try
            {
                RuntimeResolution runtime = Resolve(instance, plugin, instance.PreferredPort, String.Empty);
                return runtime.Version;
            }
            catch
            {
                return String.Empty;
            }
        }

        public static TokenContext CreateContext(InstanceConfig instance, PluginDefinition plugin, int port, string patchPath)
        {
            TokenContext context = new TokenContext();
            context.AppDirectory = AppPaths.AppDirectory;
            context.PluginDirectory = plugin.DirectoryPath;
            context.SourceRoot = instance.SourceRoot ?? String.Empty;
            context.Workspace = instance.Workspace ?? String.Empty;
            context.Profile = instance.Profile ?? "web";
            context.PinnedVersion = instance.PinnedVersion ?? String.Empty;
            context.PatchPath = patchPath ?? String.Empty;
            context.Port = port;
            return context;
        }

        private static void AddExpanded(List<string> target, List<string> values, TokenContext context)
        {
            if (values == null) return;
            foreach (string value in values) target.Add(AppPaths.Expand(value, context));
        }

        private static string ResolveVersion(InstanceConfig instance, RuntimeDefinition definition, TokenContext context)
        {
            if (String.Equals(definition.Kind, "npx", StringComparison.OrdinalIgnoreCase)) return instance.PinnedVersion ?? String.Empty;
            string versionFile = AppPaths.Expand(definition.VersionFile, context);
            if (String.IsNullOrWhiteSpace(versionFile) || !File.Exists(versionFile)) return String.Empty;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(versionFile, Encoding.UTF8));
                object version;
                if (value.TryGetValue("version", out version)) return Convert.ToString(version);
            }
            catch (Exception exception)
            {
                FileLog.Warn("Could not read runtime version from " + versionFile + ": " + exception.Message);
            }
            return String.Empty;
        }
    }

    public static class CommandRunner
    {
        private static readonly object LogSync = new object();

        public static ManagedProcess StartService(RuntimeResolution runtime, string outputLog, string errorLog)
        {
            ProcessStartInfo info = CreateStartInfo(runtime.CommandPath, runtime.Arguments, runtime.WorkingDirectory, true, true);
            if (runtime.EnvironmentVariables != null)
            {
                foreach (KeyValuePair<string, string> entry in runtime.EnvironmentVariables) info.EnvironmentVariables[entry.Key] = entry.Value;
            }
            Process process = new Process();
            process.StartInfo = info;
            process.EnableRaisingEvents = true;
            ManagedProcess managed = new ManagedProcess();
            managed.RootProcess = process;
            managed.OutputLog = outputLog;
            managed.ErrorLog = errorLog;
            process.Exited += delegate(object sender, EventArgs args) { managed.SignalExit(); };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { AppendLine(outputLog, args.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { AppendLine(errorLog, args.Data); };
            if (!process.Start()) throw new InvalidOperationException("The runtime process did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return managed;
        }

        public static CommandResult RunCapture(string command, IList<string> arguments, string workingDirectory, int timeoutMilliseconds)
        {
            ProcessStartInfo info = CreateStartInfo(command, arguments, workingDirectory, true, true);
            using (Process process = new Process())
            {
                process.StartInfo = info;
                if (!process.Start()) throw new InvalidOperationException("Command did not start: " + command);
                Task<string> outputTask = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadToEnd(); });
                Task<string> errorTask = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                bool exited = process.WaitForExit(timeoutMilliseconds);
                if (!exited)
                {
                    KillProcessTree(process);
                    process.WaitForExit(3000);
                }
                Task.WaitAll(new Task[] { outputTask, errorTask }, 5000);
                CommandResult result = new CommandResult();
                result.ExitCode = exited ? process.ExitCode : -1;
                result.TimedOut = !exited;
                result.StandardOutput = outputTask.IsCompleted ? outputTask.Result : String.Empty;
                result.StandardError = errorTask.IsCompleted ? errorTask.Result : String.Empty;
                return result;
            }
        }

        public static Process StartVisible(string command, IList<string> arguments, string workingDirectory)
        {
            ProcessStartInfo info = CreateStartInfo(command, arguments, workingDirectory, false, false);
            info.CreateNoWindow = false;
            info.WindowStyle = ProcessWindowStyle.Normal;
            Process process = new Process();
            process.StartInfo = info;
            if (!process.Start()) throw new InvalidOperationException("Command did not start: " + command);
            return process;
        }

        public static void KillProcessTree(Process process)
        {
            if (process == null) return;
            int processId;
            try
            {
                if (process.HasExited) return;
                processId = process.Id;
            }
            catch { return; }
            try
            {
                string taskkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                if (File.Exists(taskkill))
                {
                    ProcessStartInfo info = new ProcessStartInfo(taskkill, "/PID " + processId + " /T /F");
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    using (Process killer = Process.Start(info)) killer.WaitForExit(5000);
                }
            }
            catch { }
            try { if (!process.HasExited) process.Kill(); } catch { }
        }

        public static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') backslashes++;
                else if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                    result.Append(character);
                }
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static ProcessStartInfo CreateStartInfo(string command, IList<string> arguments, string workingDirectory, bool redirect, bool hidden)
        {
            string argumentLine = arguments == null ? String.Empty : String.Join(" ", arguments.Select(QuoteArgument).ToArray());
            ProcessStartInfo info = new ProcessStartInfo();
            string extension = Path.GetExtension(command) ?? String.Empty;
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.Arguments = "/d /s /c \"\"" + command.Replace("\"", "\"\"") + "\"" + (argumentLine.Length == 0 ? String.Empty : " " + argumentLine) + "\"";
            }
            else
            {
                info.FileName = command;
                info.Arguments = argumentLine;
            }
            info.WorkingDirectory = workingDirectory;
            info.UseShellExecute = false;
            info.CreateNoWindow = hidden;
            info.WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
            info.RedirectStandardOutput = redirect;
            info.RedirectStandardError = redirect;
            if (redirect)
            {
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
            }
            return info;
        }

        private static void AppendLine(string path, string line)
        {
            if (line == null || String.IsNullOrWhiteSpace(path)) return;
            try
            {
                lock (LogSync) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
