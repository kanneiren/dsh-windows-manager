using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public static class ManagerControlProtocol
    {
        public const int CurrentProtocolVersion = 1;
        public const int MaxRequestBytes = 64 * 1024;

        public static string GetDefaultPipeName()
        {
            string sid = WindowsIdentity.GetCurrent().User.Value.Replace('-', '_');
            return "dsh-windows-manager-control-" + sid;
        }

        public static string Serialize(Dictionary<string, object> message)
        {
            return JsonStore.Serialize(message) + "\n";
        }

        public static Dictionary<string, object> Deserialize(string json)
        {
            return JsonStore.Deserialize<Dictionary<string, object>>(json);
        }

        public static Dictionary<string, object> NewResponse(string command, bool ok)
        {
            Dictionary<string, object> response = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            response["protocolVersion"] = CurrentProtocolVersion;
            response["ok"] = ok;
            response["command"] = command ?? String.Empty;
            return response;
        }

        public static Dictionary<string, object> ErrorResponse(string command, string code, string message)
        {
            Dictionary<string, object> response = NewResponse(command, false);
            Dictionary<string, object> error = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            error["code"] = code ?? "internal-error";
            error["message"] = message ?? String.Empty;
            response["error"] = error;
            return response;
        }

        public static string ManagerVersion()
        {
            try
            {
                string location = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!String.IsNullOrWhiteSpace(location))
                {
                    System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(location);
                    if (!String.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
                }
            }
            catch
            {
            }
            return "0.0.0";
        }
    }

    public static class ManagerControlClient
    {
        public static bool TryRequest(string command, string instanceId, string pipeName, out Dictionary<string, object> response, out string error)
        {
            response = null;
            error = String.Empty;
            if (String.IsNullOrWhiteSpace(pipeName) || String.IsNullOrWhiteSpace(command))
            {
                error = "The Manager control pipe is unavailable.";
                return false;
            }

            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(1500);
                    pipe.ReadMode = PipeTransmissionMode.Byte;
                    Dictionary<string, object> request = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    request["protocolVersion"] = ManagerControlProtocol.CurrentProtocolVersion;
                    request["command"] = command;
                    if (!String.IsNullOrWhiteSpace(instanceId)) request["instanceId"] = instanceId;
                    string json = JsonStore.Serialize(request) + "\n";
                    byte[] requestBytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.Flush();

                    Task<string> readTask = Task.Factory.StartNew(delegate
                    {
                        using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true)) return reader.ReadLine();
                    });
                    if (!readTask.Wait(3000))
                    {
                        error = "The Manager control request timed out.";
                        return false;
                    }
                    string line = readTask.Result ?? String.Empty;
                    if (line.Length == 0)
                    {
                        error = "The Manager control response was empty.";
                        return false;
                    }
                    Dictionary<string, object> parsed = ManagerControlProtocol.Deserialize(line);
                    if (parsed == null)
                    {
                        error = "The Manager control response was invalid.";
                        return false;
                    }
                    bool ok = BridgeProtocol.GetValue(parsed, "ok") != null && Convert.ToBoolean(BridgeProtocol.GetValue(parsed, "ok"));
                    response = parsed;
                    if (!ok)
                    {
                        Dictionary<string, object> errorValue = BridgeProtocol.GetValue(parsed, "error") as Dictionary<string, object>;
                        error = errorValue == null ? "The Manager rejected the request." : BridgeProtocol.GetString(errorValue, "message");
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