using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class RuntimeBridgeLaunch
    {
        public string PipeName { get; set; }
        public string Token { get; set; }
        public string PatchPath { get; set; }
    }

    public static class RuntimeBridgePatch
    {
        public static RuntimeBridgeLaunch Create(InstanceConfig instance, PluginDefinition plugin)
        {
            if (plugin.RuntimeBridge == null || !plugin.RuntimeBridge.Enabled) return null;
            string modulePath = Path.GetFullPath(Path.Combine(plugin.DirectoryPath, plugin.RuntimeBridge.Module));
            if (!File.Exists(modulePath)) throw new FileNotFoundException("Runtime Bridge module not found.", modulePath);
            byte[] tokenBytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(tokenBytes);
            string token = BitConverter.ToString(tokenBytes).Replace("-", String.Empty).ToLowerInvariant();
            string pipeName = "DeepSeekHarnessManager-" + AppPaths.SafeFileName(instance.Id) + "-" + token.Substring(0, 12);
            string directory = AppPaths.InstanceRuntimeDirectory(instance.Id);
            string patchPath = Path.Combine(directory, "windows-lifecycle.patch.yml");
            string entryId = String.IsNullOrWhiteSpace(plugin.RuntimeBridge.EntryId) ? "windows-lifecycle" : plugin.RuntimeBridge.EntryId;
            StringBuilder yaml = new StringBuilder();
            yaml.AppendLine("- insert:");
            yaml.AppendLine("    - id: " + YamlSingle(entryId));
            yaml.AppendLine("      name: " + YamlSingle(new Uri(modulePath).AbsoluteUri));
            yaml.AppendLine("      config:");
            yaml.AppendLine("        pipeName: " + YamlSingle(pipeName));
            yaml.AppendLine("        token: " + YamlSingle(token));
            if (!String.IsNullOrWhiteSpace(instance.Profile))
                yaml.AppendLine("        profile: " + YamlSingle(instance.Profile));
            File.WriteAllText(patchPath, yaml.ToString(), new UTF8Encoding(false));
            RuntimeBridgeLaunch launch = new RuntimeBridgeLaunch();
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

            BridgeMessage response = RequestVersioned(pipeName, token, timeoutMilliseconds, out error);
            if (response != null && response.Ok) return true;
            if (response != null && BridgeProtocol.IsVersioned(response) &&
                !String.Equals(BridgeProtocol.ErrorCode(response), "bridge-timeout", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(BridgeProtocol.ErrorCode(response), "bridge-disconnected", StringComparison.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(error)) error = BridgeProtocol.DescribeError(response);
                return false;
            }

            // Old DSH builds only understand the original single-purpose
            // shutdown envelope. Try that compatibility path once before
            // reporting failure to the user.
            string legacyError;
            bool legacyAccepted = RequestLegacy(pipeName, token, timeoutMilliseconds, out legacyError);
            if (legacyAccepted)
            {
                error = String.Empty;
                return true;
            }
            if (String.IsNullOrWhiteSpace(error)) error = legacyError;
            return false;
        }

        private static BridgeMessage RequestVersioned(string pipeName, string token, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            int connectTimeout = Math.Max(200, timeoutMilliseconds / 2);
            int requestTimeout = Math.Max(200, timeoutMilliseconds - connectTimeout);
            IpcBridgeConnection connection = IpcBridgeConnection.Connect(pipeName, token, connectTimeout, out error);
            if (connection == null) return null;
            try
            {
                BridgeMessage response = connection.Request("shutdown", null, requestTimeout);
                if (response == null)
                {
                    error = Localization.Text("Bridge.Rejected");
                    return null;
                }
                return response;
            }
            finally
            {
                connection.Close();
            }
        }

        private static bool RequestLegacy(string pipeName, string token, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
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
