using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class BridgeMessage
    {
        public int ProtocolVersion { get; set; }
        public string MessageType { get; set; }
        public string RequestId { get; set; }
        public string Type { get; set; }
        public string Token { get; set; }
        public Dictionary<string, object> Payload { get; set; }
        public object Error { get; set; }
        public bool Ok { get; set; }
    }

    public sealed class BridgeRuntimeInfo
    {
        public string State { get; set; }
        public int Pid { get; set; }
        public int Port { get; set; }
        public string Host { get; set; }
        public string DshVersion { get; set; }
        public string Profile { get; set; }
        public string DshHome { get; set; }
        public string NodeVersion { get; set; }
        public string Cwd { get; set; }
        public string RuntimeBridgeVersion { get; set; }

        public bool IsReady
        {
            get { return String.Equals(State, "ready", StringComparison.OrdinalIgnoreCase) && Port > 0 && Pid > 0; }
        }

        public bool IsStopping
        {
            get { return String.Equals(State, "stopping", StringComparison.OrdinalIgnoreCase) || String.Equals(State, "exiting", StringComparison.OrdinalIgnoreCase); }
        }
    }

    public sealed class BridgeEventReceivedEventArgs : EventArgs
    {
        public BridgeEventReceivedEventArgs(BridgeMessage message)
        {
            Message = message;
        }

        public BridgeMessage Message { get; private set; }
        public string EventName { get { return Message == null ? String.Empty : Message.Type ?? String.Empty; } }
    }

    public sealed class BridgeDisconnectedEventArgs : EventArgs
    {
        public BridgeDisconnectedEventArgs(string reason)
        {
            Reason = reason ?? String.Empty;
        }

        public string Reason { get; private set; }
    }

    public static class BridgeProtocol
    {
        public const int CurrentProtocolVersion = 1;

        public static BridgeMessage Command(string type, string token, Dictionary<string, object> payload)
        {
            BridgeMessage message = new BridgeMessage();
            message.ProtocolVersion = CurrentProtocolVersion;
            message.MessageType = "command";
            message.RequestId = Guid.NewGuid().ToString("N");
            message.Type = type ?? String.Empty;
            message.Token = token ?? String.Empty;
            message.Payload = payload;
            message.Error = null;
            message.Ok = false;
            return message;
        }

        public static string Serialize(BridgeMessage message)
        {
            if (message == null) return "null";
            Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal);
            data["protocolVersion"] = message.ProtocolVersion;
            data["messageType"] = message.MessageType ?? String.Empty;
            data["type"] = message.Type ?? String.Empty;
            if (!String.IsNullOrWhiteSpace(message.RequestId)) data["requestId"] = message.RequestId;
            if (!String.IsNullOrWhiteSpace(message.Token)) data["token"] = message.Token;
            if (message.Payload != null) data["payload"] = message.Payload;
            if (message.Error != null) data["error"] = message.Error;
            if (String.Equals(message.MessageType, "response", StringComparison.OrdinalIgnoreCase)) data["ok"] = message.Ok;
            return JsonStore.Serialize(data);
        }

        public static BridgeMessage Deserialize(string json)
        {
            Dictionary<string, object> data = JsonStore.Deserialize<Dictionary<string, object>>(json);
            if (data == null) return null;
            BridgeMessage message = new BridgeMessage();
            message.ProtocolVersion = GetInt(data, "protocolVersion");
            message.MessageType = GetString(data, "messageType");
            message.RequestId = GetString(data, "requestId");
            message.Type = GetString(data, "type");
            message.Token = GetString(data, "token");
            message.Payload = GetValue(data, "payload") as Dictionary<string, object>;
            message.Error = GetValue(data, "error");
            object okValue = GetValue(data, "ok");
            message.Ok = okValue != null && Convert.ToBoolean(okValue, CultureInfo.InvariantCulture);
            return message;
        }

        public static BridgeMessage LocalError(string type, string requestId, string code, string detail)
        {
            BridgeMessage message = new BridgeMessage();
            message.ProtocolVersion = CurrentProtocolVersion;
            message.MessageType = "response";
            message.RequestId = requestId ?? String.Empty;
            message.Type = type ?? "error";
            message.Ok = false;
            message.Payload = null;
            Dictionary<string, object> error = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            error["code"] = code ?? "internal-error";
            error["message"] = detail ?? String.Empty;
            message.Error = error;
            return message;
        }

        public static string ErrorCode(BridgeMessage message)
        {
            if (message == null || message.Error == null) return String.Empty;
            Dictionary<string, object> error = message.Error as Dictionary<string, object>;
            if (error != null) return GetString(error, "code");
            return Convert.ToString(message.Error, CultureInfo.InvariantCulture);
        }

        public static string ErrorText(BridgeMessage message)
        {
            if (message == null || message.Error == null) return String.Empty;
            Dictionary<string, object> error = message.Error as Dictionary<string, object>;
            if (error != null) return GetString(error, "message");
            return Convert.ToString(message.Error, CultureInfo.InvariantCulture);
        }

        public static string DescribeError(BridgeMessage message)
        {
            string code = ErrorCode(message);
            string text = ErrorText(message);
            if (!String.IsNullOrWhiteSpace(code) && !String.IsNullOrWhiteSpace(text))
                return String.Equals(code, text, StringComparison.Ordinal) ? code : code + ": " + text;
            if (!String.IsNullOrWhiteSpace(text)) return text;
            if (!String.IsNullOrWhiteSpace(code)) return code;
            return "The IPC bridge rejected the request.";
        }

        public static BridgeRuntimeInfo ParseRuntimeInfo(BridgeMessage message)
        {
            if (message == null || message.Payload == null) return null;
            BridgeRuntimeInfo info = new BridgeRuntimeInfo();
            info.State = GetString(message.Payload, "state");
            info.Pid = GetInt(message.Payload, "pid");
            info.Port = GetInt(message.Payload, "port");
            info.Host = GetString(message.Payload, "host");
            info.DshVersion = GetString(message.Payload, "dshVersion");
            info.Profile = GetString(message.Payload, "profile");
            info.DshHome = GetString(message.Payload, "dshHome");
            info.NodeVersion = GetString(message.Payload, "nodeVersion");
            info.Cwd = GetString(message.Payload, "cwd");
            info.RuntimeBridgeVersion = GetString(message.Payload, "runtimeBridgeVersion");
            return info;
        }

        public static object GetValue(Dictionary<string, object> values, string key)
        {
            object value;
            if (values == null || String.IsNullOrWhiteSpace(key)) return null;
            return values.TryGetValue(key, out value) ? value : null;
        }

        public static string GetString(Dictionary<string, object> values, string key)
        {
            object value = GetValue(values, key);
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> values, string key)
        {
            object value = GetValue(values, key);
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }

    public sealed class IpcBridgeConnection : IDisposable
    {
        private readonly Stream stream;
        private readonly Func<bool> isConnected;
        private readonly Action closeStream;
        private readonly StreamWriter writer;
        private readonly StreamReader reader;
        private readonly string token;
        private readonly object sync = new object();
        private readonly Dictionary<string, TaskCompletionSource<BridgeMessage>> pending =
            new Dictionary<string, TaskCompletionSource<BridgeMessage>>(StringComparer.Ordinal);
        private readonly Task readerTask;
        private volatile bool closing;
        private volatile bool disconnected;
        private string disconnectReason = String.Empty;

        private IpcBridgeConnection(Stream connectedStream, Func<bool> connected, Action close, string bridgeToken)
        {
            stream = connectedStream;
            isConnected = connected;
            closeStream = close;
            token = bridgeToken ?? String.Empty;
            writer = new StreamWriter(stream, new UTF8Encoding(false), 4096);
            writer.AutoFlush = true;
            writer.NewLine = "\n";
            reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            readerTask = RunReaderLoop();
        }

        public event EventHandler<BridgeEventReceivedEventArgs> EventReceived;
        public event EventHandler<BridgeDisconnectedEventArgs> Disconnected;

        public bool IsConnected
        {
            get
            {
                if (closing || disconnected) return false;
                try { return isConnected == null || isConnected(); }
                catch { return false; }
            }
        }

        public static IpcBridgeConnection Connect(string pipeName, string bridgeToken, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(pipeName) || String.IsNullOrWhiteSpace(bridgeToken))
            {
                error = Localization.Text("Bridge.Unavailable");
                return null;
            }
            try
            {
                NamedPipeClientStream connectedPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                connectedPipe.Connect(timeoutMilliseconds);
                connectedPipe.ReadMode = PipeTransmissionMode.Byte;
                return new IpcBridgeConnection(connectedPipe, delegate { return connectedPipe.IsConnected; }, delegate { connectedPipe.Dispose(); }, bridgeToken);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        public static IpcBridgeConnection ConnectTcp(string host, int port, string bridgeToken, int timeoutMilliseconds, out string error)
        {
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(host) || port < 1 || port > 65535 || String.IsNullOrWhiteSpace(bridgeToken))
            {
                error = Localization.Text("Bridge.Unavailable");
                return null;
            }
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                {
                    client.Close();
                    error = Localization.Text("Bridge.Timeout");
                    return null;
                }
                client.EndConnect(result);
                NetworkStream network = client.GetStream();
                return new IpcBridgeConnection(network, delegate { return client.Connected; }, delegate { client.Close(); }, bridgeToken);
            }
            catch (Exception exception)
            {
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
                error = exception.Message;
                return null;
            }
        }

        public BridgeMessage Request(string type, Dictionary<string, object> payload, int timeoutMilliseconds)
        {
            if (closing || disconnected)
            {
                return BridgeProtocol.LocalError(type, String.Empty, "bridge-disconnected", disconnectReason.Length == 0 ? "The IPC bridge is disconnected." : disconnectReason);
            }

            BridgeMessage request = BridgeProtocol.Command(type, token, payload);
            TaskCompletionSource<BridgeMessage> completion = new TaskCompletionSource<BridgeMessage>();
            lock (sync)
            {
                if (closing || disconnected)
                    return BridgeProtocol.LocalError(type, request.RequestId, "bridge-disconnected", disconnectReason.Length == 0 ? "The IPC bridge is disconnected." : disconnectReason);
                pending[request.RequestId] = completion;
                try
                {
                    writer.WriteLine(BridgeProtocol.Serialize(request));
                }
                catch (Exception exception)
                {
                    pending.Remove(request.RequestId);
                    return BridgeProtocol.LocalError(type, request.RequestId, "bridge-write-failed", exception.Message);
                }
            }

            if (!completion.Task.Wait(timeoutMilliseconds))
            {
                lock (sync) pending.Remove(request.RequestId);
                return BridgeProtocol.LocalError(type, request.RequestId, "bridge-timeout", Localization.Text("Bridge.Timeout"));
            }
            try
            {
                return completion.Task.Result;
            }
            catch (Exception exception)
            {
                return BridgeProtocol.LocalError(type, request.RequestId, "bridge-request-failed", exception.Message);
            }
        }

        public void Close()
        {
            if (closing) return;
            closing = true;
            try { writer.Dispose(); } catch { }
            try { reader.Dispose(); } catch { }
            if (closeStream != null)
            {
                try { closeStream(); } catch { }
            }
            try { stream.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }

        private async Task RunReaderLoop()
        {
            while (!closing)
            {
                string line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    MarkDisconnected(exception.Message);
                    break;
                }
                if (line == null)
                {
                    MarkDisconnected("The DSH IPC bridge closed the pipe.");
                    break;
                }
                if (line.Length == 0) continue;

                BridgeMessage message;
                try
                {
                    message = BridgeProtocol.Deserialize(line);
                }
                catch (Exception exception)
                {
                    FileLog.Warn("Ignoring malformed DSH IPC message: " + exception.Message);
                    continue;
                }
                if (message == null) continue;

                if (!String.Equals(message.MessageType, "event", StringComparison.OrdinalIgnoreCase))
                {
                    CompletePending(message);
                    continue;
                }

                EventHandler<BridgeEventReceivedEventArgs> handler = EventReceived;
                if (handler != null)
                {
                    try { handler(this, new BridgeEventReceivedEventArgs(message)); }
                    catch (Exception exception) { FileLog.Warn("IPC event handler failed: " + exception.Message); }
                }
            }
            Close();
        }

        private void CompletePending(BridgeMessage message)
        {
            TaskCompletionSource<BridgeMessage> completion = null;
            lock (sync)
            {
                if (!String.IsNullOrWhiteSpace(message.RequestId) && pending.TryGetValue(message.RequestId, out completion))
                {
                    pending.Remove(message.RequestId);
                }
                else if (String.IsNullOrWhiteSpace(message.RequestId) && pending.Count == 1)
                {
                    foreach (KeyValuePair<string, TaskCompletionSource<BridgeMessage>> entry in pending)
                    {
                        completion = entry.Value;
                        break;
                    }
                    pending.Clear();
                }
            }
            if (completion != null)
            {
                try { completion.TrySetResult(message); }
                catch { }
            }
        }

        private void MarkDisconnected(string reason)
        {
            if (disconnected) return;
            disconnected = true;
            disconnectReason = reason ?? String.Empty;
            lock (sync)
            {
                foreach (KeyValuePair<string, TaskCompletionSource<BridgeMessage>> entry in pending)
                {
                    try
                    {
                        entry.Value.TrySetResult(BridgeProtocol.LocalError(entry.Key, entry.Key, "bridge-disconnected", disconnectReason));
                    }
                    catch { }
                }
                pending.Clear();
            }
            EventHandler<BridgeDisconnectedEventArgs> handler = Disconnected;
            if (handler != null)
            {
                try { handler(this, new BridgeDisconnectedEventArgs(disconnectReason)); }
                catch (Exception exception) { FileLog.Warn("IPC disconnect handler failed: " + exception.Message); }
            }
        }
    }
}
