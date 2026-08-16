using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessManager
{
    public sealed class RuntimeBridgeLaunch
    {
        public string PipeName { get; set; }
        public string Transport { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
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
            bool wslTransport = String.Equals(instance.RuntimeType, InstanceModel.RuntimeTypeWsl, StringComparison.OrdinalIgnoreCase);
            string pipeName = wslTransport ? String.Empty : ("DeepSeekHarnessManager-" + AppPaths.SafeFileName(instance.Id) + "-" + token.Substring(0, 12));
            int tcpPort = wslTransport ? ReserveLoopbackPort() : 0;
            string directory = AppPaths.InstanceRuntimeDirectory(instance.Id);
            string patchPath = Path.Combine(directory, "windows-lifecycle.patch.yml");
            string entryId = String.IsNullOrWhiteSpace(plugin.RuntimeBridge.EntryId) ? "windows-lifecycle" : plugin.RuntimeBridge.EntryId;
            string moduleUri;
            if (wslTransport)
            {
                try
                {
                    moduleUri = new Uri("file://" + WslRuntimeAdapter.ConvertToWslPath(instance.WslDistro, modulePath)).AbsoluteUri;
                }
                catch
                {
                    moduleUri = new Uri(modulePath).AbsoluteUri;
                }
            }
            else
            {
                moduleUri = new Uri(modulePath).AbsoluteUri;
            }
            StringBuilder yaml = new StringBuilder();
            yaml.AppendLine("- insert:");
            yaml.AppendLine("    - id: " + YamlSingle(entryId));
            yaml.AppendLine("      name: " + YamlSingle(moduleUri));
            yaml.AppendLine("      config:");
            if (wslTransport)
            {
                yaml.AppendLine("        transport: " + YamlSingle("tcp"));
                yaml.AppendLine("        host: " + YamlSingle("0.0.0.0"));
                yaml.AppendLine("        port: " + tcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                yaml.AppendLine("        pipeName: " + YamlSingle(pipeName));
            }
            yaml.AppendLine("        token: " + YamlSingle(token));
            if (!String.IsNullOrWhiteSpace(instance.Profile))
                yaml.AppendLine("        profile: " + YamlSingle(instance.Profile));
            File.WriteAllText(patchPath, yaml.ToString(), new UTF8Encoding(false));
            RuntimeBridgeLaunch launch = new RuntimeBridgeLaunch();
            launch.PipeName = pipeName;
            launch.Transport = wslTransport ? "tcp" : "pipe";
            launch.Host = wslTransport ? "127.0.0.1" : String.Empty;
            launch.Port = tcpPort;
            launch.Token = token;
            launch.PatchPath = patchPath;
            return launch;
        }

        private static int ReserveLoopbackPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
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
            return Accept(RequestVersionedPipe(pipeName, token, timeoutMilliseconds, out error), ref error);
        }

        public static bool Request(RuntimeBridgeLaunch launch, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            if (launch == null || String.IsNullOrWhiteSpace(launch.Token))
            {
                error = Localization.Text("Bridge.Unavailable");
                return false;
            }
            if (String.Equals(launch.Transport, "tcp", StringComparison.OrdinalIgnoreCase))
                return Accept(RequestVersionedTcp(launch.Host, launch.Port, launch.Token, timeoutMilliseconds, out error), ref error);
            return Accept(RequestVersionedPipe(launch.PipeName, launch.Token, timeoutMilliseconds, out error), ref error);
        }

        private static bool Accept(BridgeMessage response, ref string error)
        {
            if (response != null && response.Ok) return true;
            if (response != null && !String.Equals(BridgeProtocol.ErrorCode(response), "bridge-timeout", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(BridgeProtocol.ErrorCode(response), "bridge-disconnected", StringComparison.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(error)) error = BridgeProtocol.DescribeError(response);
            }
            return false;
        }

        private static BridgeMessage RequestVersionedPipe(string pipeName, string token, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            int connectTimeout = Math.Max(200, timeoutMilliseconds / 2);
            int requestTimeout = Math.Max(200, timeoutMilliseconds - connectTimeout);
            IpcBridgeConnection connection = IpcBridgeConnection.Connect(pipeName, token, connectTimeout, out error);
            if (connection == null) return null;
            return RequestOnConnection(connection, requestTimeout, out error);
        }

        private static BridgeMessage RequestVersionedTcp(string host, int port, string token, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            int connectTimeout = Math.Max(200, timeoutMilliseconds / 2);
            int requestTimeout = Math.Max(200, timeoutMilliseconds - connectTimeout);
            IpcBridgeConnection connection = IpcBridgeConnection.ConnectTcp(host, port, token, connectTimeout, out error);
            if (connection == null) return null;
            return RequestOnConnection(connection, requestTimeout, out error);
        }

        private static BridgeMessage RequestOnConnection(IpcBridgeConnection connection, int requestTimeout, out string error)
        {
            error = String.Empty;
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

    }
}