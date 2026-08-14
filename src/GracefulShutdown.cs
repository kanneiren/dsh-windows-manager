using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class CompanionLaunch
    {
        public string PipeName { get; set; }
        public string Token { get; set; }
        public string PatchPath { get; set; }
    }

    public static class CompanionPatch
    {
        public static CompanionLaunch Create(InstanceConfig instance, PluginDefinition plugin)
        {
            if (plugin.Companion == null || !plugin.Companion.Enabled) return null;
            string modulePath = Path.GetFullPath(Path.Combine(plugin.DirectoryPath, plugin.Companion.Module));
            if (!File.Exists(modulePath)) throw new FileNotFoundException("Companion module not found.", modulePath);
            byte[] tokenBytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(tokenBytes);
            string token = BitConverter.ToString(tokenBytes).Replace("-", String.Empty).ToLowerInvariant();
            string pipeName = "DeepSeekHarnessManager-" + AppPaths.SafeFileName(instance.Id) + "-" + token.Substring(0, 12);
            string directory = AppPaths.InstanceRuntimeDirectory(instance.Id);
            string patchPath = Path.Combine(directory, "windows-lifecycle.patch.yml");
            string entryId = String.IsNullOrWhiteSpace(plugin.Companion.EntryId) ? "windows-lifecycle" : plugin.Companion.EntryId;
            StringBuilder yaml = new StringBuilder();
            yaml.AppendLine("- insert:");
            yaml.AppendLine("    - id: " + YamlSingle(entryId));
            yaml.AppendLine("      name: " + YamlSingle(new Uri(modulePath).AbsoluteUri));
            yaml.AppendLine("      config:");
            yaml.AppendLine("        pipeName: " + YamlSingle(pipeName));
            yaml.AppendLine("        token: " + YamlSingle(token));
            File.WriteAllText(patchPath, yaml.ToString(), new UTF8Encoding(false));
            CompanionLaunch launch = new CompanionLaunch();
            launch.PipeName = pipeName;
            launch.Token = token;
            launch.PatchPath = patchPath;
            return launch;
        }

        private static string YamlSingle(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "''") + "'";
        }
    }

    public static class GracefulShutdownClient
    {
        public static bool Request(string pipeName, string token, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(pipeName) || String.IsNullOrWhiteSpace(token))
            {
                error = Localization.Text("Bridge.Unavailable");
                return false;
            }
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(timeoutMilliseconds);
                    pipe.ReadMode = PipeTransmissionMode.Byte;
                    string request = JsonStore.Serialize(new Dictionary<string, string> { { "action", "shutdown" }, { "token", token } }) + "\n";
                    byte[] requestBytes = Encoding.UTF8.GetBytes(request);
                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.Flush();
                    Task<string> responseTask = Task.Factory.StartNew(delegate
                    {
                        using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true)) return reader.ReadLine();
                    });
                    if (!responseTask.Wait(timeoutMilliseconds))
                    {
                        error = Localization.Text("Bridge.Timeout");
                        return false;
                    }
                    string response = responseTask.Result ?? String.Empty;
                    if (response.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        error = Localization.Text("Bridge.Rejected");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
