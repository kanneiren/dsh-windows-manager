using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public sealed class ManagerControlServer : IDisposable
    {
        private readonly IManagerService manager;
        private readonly string pipeName;
        private readonly PipeSecurity pipeSecurity;
        private readonly System.Threading.SynchronizationContext requestContext;
        private volatile NamedPipeServerStream currentPipe;
        private volatile bool disposing;
        private Task acceptTask;

        public ManagerControlServer(IManagerService managerService)
            : this(managerService, ManagerControlProtocol.GetDefaultPipeName())
        {
        }

        internal ManagerControlServer(IManagerService managerService, string name)
        {
            manager = managerService;
            pipeName = name;
            pipeSecurity = CreateCurrentUserPipeSecurity();
            requestContext = System.Threading.SynchronizationContext.Current;
        }

        internal string Name { get { return pipeName; } }

        public void Start()
        {
            if (acceptTask != null) return;
            acceptTask = RunAcceptLoop();
        }

        public void Dispose()
        {
            if (disposing) return;
            disposing = true;
            NamedPipeServerStream pipe = currentPipe;
            if (pipe != null)
            {
                try { pipe.Dispose(); } catch { }
            }
            if (acceptTask != null)
            {
                try { acceptTask.Wait(2000); } catch { }
            }
        }

        private async Task RunAcceptLoop()
        {
            while (!disposing)
            {
                NamedPipeServerStream pipe = null;
                bool retryDelay = false;
                try
                {
                    pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096,
                        4096,
                        pipeSecurity);
                    currentPipe = pipe;
                    await Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null);
                    currentPipe = null;
                    if (disposing)
                    {
                        pipe.Dispose();
                        return;
                    }
                    await HandleConnection(pipe);
                }
                catch (Exception exception)
                {
                    if (!disposing)
                    {
                        FileLog.Warn("Manager control pipe accept failed: " + exception.Message);
                        retryDelay = true;
                    }
                    if (pipe != null)
                    {
                        try { pipe.Dispose(); } catch { }
                    }
                }
                if (retryDelay) await Task.Delay(100);
            }
        }

        private async Task HandleConnection(NamedPipeServerStream pipe)
        {
            try
            {
                string line = await ReadLineAsync(pipe, ManagerControlProtocol.MaxRequestBytes, 5000);
                if (String.IsNullOrEmpty(line))
                {
                    await WriteErrorIfPossible(pipe, null, "malformed-message", "The request is empty.");
                    return;
                }

                Dictionary<string, object> request = null;
                string parseError = null;
                try
                {
                    request = ManagerControlProtocol.Deserialize(line);
                }
                catch (Exception exception)
                {
                    FileLog.Warn("Ignoring malformed Manager control message: " + exception.Message);
                    parseError = exception.Message;
                }
                if (parseError != null)
                {
                    await WriteErrorIfPossible(pipe, null, "invalid-json", "The request is not valid JSON.");
                    return;
                }
                if (request == null)
                {
                    await WriteErrorIfPossible(pipe, null, "malformed-message", "The request is empty.");
                    return;
                }

                string command = BridgeProtocol.GetString(request, "command").ToLowerInvariant();
                bool exitAfterResponse = String.Equals(command, "exit", StringComparison.OrdinalIgnoreCase);
                Dictionary<string, object> response = await ExecuteRequestOnContext(request);
                await WriteResponse(pipe, response);
                if (exitAfterResponse) ExecuteExitOnContext();
            }
            catch (Exception exception)
            {
                FileLog.Warn("Manager control connection failed: " + exception.Message);
            }
            finally
            {
                try { pipe.Dispose(); } catch { }
            }
        }

        private void ExecuteExitOnContext()
        {
            if (requestContext == null || System.Threading.SynchronizationContext.Current == requestContext)
            {
                manager.RequestExit();
                return;
            }
            requestContext.Post(delegate { manager.RequestExit(); }, null);
        }

        private async Task<Dictionary<string, object>> ExecuteRequestOnContext(Dictionary<string, object> request)
        {
            if (requestContext == null || System.Threading.SynchronizationContext.Current == requestContext)
                return ExecuteRequest(request);
            TaskCompletionSource<Dictionary<string, object>> completion = new TaskCompletionSource<Dictionary<string, object>>();
            requestContext.Post(delegate
            {
                try
                {
                    completion.TrySetResult(ExecuteRequest(request));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return await completion.Task;
        }

        private Dictionary<string, object> ExecuteRequest(Dictionary<string, object> request)
        {
            int protocolVersion = BridgeProtocol.GetInt(request, "protocolVersion");
            string command = BridgeProtocol.GetString(request, "command").ToLowerInvariant();
            if (protocolVersion != ManagerControlProtocol.CurrentProtocolVersion)
            {
                Dictionary<string, object> unsupported = ManagerControlProtocol.ErrorResponse(command, "protocol-version-unsupported",
                    "Unsupported protocol version. This Manager speaks version " + ManagerControlProtocol.CurrentProtocolVersion + ".");
                unsupported["supportedProtocolVersion"] = ManagerControlProtocol.CurrentProtocolVersion;
                return unsupported;
            }

            string instanceId = BridgeProtocol.GetString(request, "instanceId");
            if (String.IsNullOrWhiteSpace(instanceId)) instanceId = null;

            if (command == "getversion") return BuildGetVersionResponse(command);
            if (command == "listinstances") return BuildListInstancesResponse(command);
            if (command == "getstatus") return BuildGetStatusResponse(command);
            if (command == "start" || command == "stop" || command == "restart" || command == "open")
                return ExecuteInstanceAction(command, instanceId);
            if (command == "openwsl")
            {
                string instanceIdValue = manager.OpenOrStartWsl();
                Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
                ManagerSnapshot snapshot = manager.GetSnapshot();
                Dictionary<string, object> instance = FindInstancePayload(snapshot, instanceIdValue);
                response["instance"] = instance;
                if (instance != null)
                {
                    response["instanceId"] = instance["instanceId"];
                    response["state"] = instance["state"];
                    response["pid"] = instance["pid"];
                    response["port"] = instance["port"];
                    response["ownership"] = instance["ownership"];
                }
                return response;
            }
            if (command == "exit")
            {
                Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
                response["message"] = "The primary Manager will exit.";
                return response;
            }

            return ManagerControlProtocol.ErrorResponse(command, "unknown-command", "The command is not supported by Manager Control Protocol v1.");
        }

        private Dictionary<string, object> BuildGetVersionResponse(string command)
        {
            Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
            response["managerVersion"] = ManagerControlProtocol.ManagerVersion();
            response["protocolVersion"] = ManagerControlProtocol.CurrentProtocolVersion;
            return response;
        }

        private Dictionary<string, object> BuildListInstancesResponse(string command)
        {
            Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
            response["instances"] = BuildInstancesPayload();
            return response;
        }

        private Dictionary<string, object> BuildGetStatusResponse(string command)
        {
            Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
            response["managerPid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            response["managerVersion"] = ManagerControlProtocol.ManagerVersion();
            ManagerSnapshot snapshot = manager.GetSnapshot();
            response["trayEnabled"] = snapshot.TrayEnabled;
            response["startWithWindows"] = snapshot.StartWithWindows;
            response["desktopShortcut"] = snapshot.DesktopShortcut;
            response["wslEnabled"] = snapshot.WslEnabled;
            response["wslDefaultDistro"] = snapshot.WslDefaultDistro ?? String.Empty;
            List<Dictionary<string, object>> instances = BuildInstancesPayload(snapshot);
            response["instances"] = instances;
            foreach (Dictionary<string, object> instance in instances)
            {
                string instanceId = Convert.ToString(instance["instanceId"], CultureInfo.InvariantCulture);
                if (String.Equals(instanceId, snapshot.DefaultInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    response["instanceId"] = instance["instanceId"];
                    response["state"] = instance["state"];
                    response["runtime"] = instance["runtime"];
                    response["ownership"] = instance["ownership"];
                    response["pid"] = instance["pid"];
                    response["port"] = instance["port"];
                    response["frontend"] = instance["frontend"];
                    break;
                }
            }
            return response;
        }

        private Dictionary<string, object> ExecuteInstanceAction(string command, string instanceId)
        {
            if (command == "start") manager.Start(instanceId);
            else if (command == "stop") manager.Stop(instanceId, false);
            else if (command == "restart") manager.Restart(instanceId);
            else manager.Open(instanceId);

            Dictionary<string, object> response = ManagerControlProtocol.NewResponse(command, true);
            ManagerSnapshot snapshot = manager.GetSnapshot();
            Dictionary<string, object> instance = FindInstancePayload(snapshot, instanceId);
            response["instance"] = instance;
            if (instance != null)
            {
                response["instanceId"] = instance["instanceId"];
                response["state"] = instance["state"];
                response["pid"] = instance["pid"];
                response["port"] = instance["port"];
                response["ownership"] = instance["ownership"];
            }
            return response;
        }

        private List<Dictionary<string, object>> BuildInstancesPayload()
        {
            return BuildInstancesPayload(manager.GetSnapshot());
        }

        private static List<Dictionary<string, object>> BuildInstancesPayload(ManagerSnapshot snapshot)
        {
            List<Dictionary<string, object>> instances = new List<Dictionary<string, object>>();
            if (snapshot == null || snapshot.Instances == null) return instances;
            foreach (InstanceSnapshot item in snapshot.Instances) instances.Add(BuildInstancePayload(item));
            return instances;
        }

        private static Dictionary<string, object> FindInstancePayload(ManagerSnapshot snapshot, string instanceId)
        {
            if (snapshot == null || snapshot.Instances == null) return null;
            foreach (Dictionary<string, object> item in BuildInstancesPayload(snapshot))
            {
                string id = Convert.ToString(item["instanceId"], CultureInfo.InvariantCulture);
                if (String.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)) return item;
            }
            if (snapshot.Instances.Count > 0) return BuildInstancePayload(snapshot.Instances[0]);
            return null;
        }

        private static Dictionary<string, object> BuildInstancePayload(InstanceSnapshot instance)
        {
            Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            data["instanceId"] = instance.Id ?? String.Empty;
            data["displayName"] = instance.Name ?? String.Empty;
            data["state"] = instance.State.ToString().ToLowerInvariant();
            data["runtime"] = instance.RuntimeType ?? InstanceModel.RuntimeTypeWindows;
            data["wslDistro"] = instance.WslDistro ?? String.Empty;
            data["ownership"] = instance.Ownership ?? InstanceModel.OwnershipAttached;
            data["pid"] = instance.ProcessId;
            data["port"] = instance.ActivePort > 0 ? instance.ActivePort : instance.PreferredPort;
            data["profile"] = instance.Profile ?? String.Empty;
            data["frontend"] = instance.Frontend ?? InstanceModel.FrontendWeb;
            data["workingDirectory"] = instance.WorkingDirectory ?? String.Empty;
            data["dshHome"] = instance.DshHome ?? String.Empty;
            data["startedAt"] = instance.StartedAtUtc.HasValue ? instance.StartedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : null;
            data["dshVersion"] = instance.InstalledVersion ?? String.Empty;
            data["lastStartResult"] = instance.LastStartResult ?? String.Empty;
            data["lastExitReason"] = instance.LastExitReason ?? String.Empty;
            data["runtimeBridgeState"] = instance.BridgeState ?? String.Empty;
            data["runtimeBridgeVersion"] = instance.RuntimeBridgeVersion ?? String.Empty;
            data["runtimeBridgeProtocolVersion"] = instance.RuntimeBridgeProtocolVersion;
            return data;
        }

        private async Task WriteErrorIfPossible(NamedPipeServerStream pipe, string command, string code, string message)
        {
            try
            {
                await WriteResponse(pipe, ManagerControlProtocol.ErrorResponse(command ?? String.Empty, code, message));
            }
            catch
            {
            }
        }

        private static async Task WriteResponse(NamedPipeServerStream pipe, Dictionary<string, object> response)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ManagerControlProtocol.Serialize(response));
            await WriteWithTimeout(pipe, bytes, 3000);
        }

        private static async Task<string> ReadLineAsync(Stream stream, int maxBytes, int timeoutMilliseconds)
        {
            List<byte> bytes = new List<byte>();
            byte[] buffer = new byte[1024];
            while (bytes.Count <= maxBytes)
            {
                Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                Task completed = await Task.WhenAny(readTask, Task.Delay(timeoutMilliseconds));
                if (completed != readTask) return null;
                int count = await readTask;
                if (count == 0) return bytes.Count == 0 ? null : Decode(bytes);
                int i;
                for (i = 0; i < count; i++)
                {
                    if (buffer[i] == (byte)'\n') return Decode(bytes);
                    bytes.Add(buffer[i]);
                }
            }
            return null;
        }

        private static string Decode(List<byte> bytes)
        {
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static async Task WriteWithTimeout(Stream stream, byte[] bytes, int timeoutMilliseconds)
        {
            Task writeTask = stream.WriteAsync(bytes, 0, bytes.Length);
            Task completed = await Task.WhenAny(writeTask, Task.Delay(timeoutMilliseconds));
            if (completed != writeTask) throw new TimeoutException("The Manager control response timed out.");
            await writeTask;
            await stream.FlushAsync();
        }

        private static PipeSecurity CreateCurrentUserPipeSecurity()
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));
            security.SetOwner(user);
            return security;
        }
    }
}