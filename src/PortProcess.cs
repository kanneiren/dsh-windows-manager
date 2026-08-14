using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace DeepSeekHarnessManager
{
    public static class PortMap
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

        public static IList<int> GetListenerProcessIds(int port)
        {
            HashSet<int> processIds = new HashSet<int>();
            ReadTable(AfInet, port, processIds);
            ReadTable(AfInet6, port, processIds);
            return new List<int>(processIds);
        }

        public static int GetPreferredListenerProcessId(int port)
        {
            IList<int> values = GetListenerProcessIds(port);
            if (values.Count == 0) return 0;
            return values[0];
        }

        private static void ReadTable(int addressFamily, int targetPort, HashSet<int> result)
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
            if (status != ErrorInsufficientBuffer && status != 0) return;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int attempts;
                for (attempts = 0; attempts < 3; attempts++)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(size);
                    status = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
                    if (status == 0) break;
                    if (status != ErrorInsufficientBuffer) return;
                }
                if (status != 0) return;
                int count = Marshal.ReadInt32(buffer, 0);
                int rowSize = addressFamily == AfInet ? 24 : 56;
                int stateOffset = addressFamily == AfInet ? 0 : 48;
                int portOffset = addressFamily == AfInet ? 8 : 20;
                int processOffset = addressFamily == AfInet ? 20 : 52;
                long firstRow = buffer.ToInt64() + 4;
                int i;
                for (i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(firstRow + (long)i * rowSize);
                    int state = Marshal.ReadInt32(row, stateOffset);
                    if (state != 2) continue;
                    int rawPort = Marshal.ReadInt32(row, portOffset);
                    byte[] bytes = BitConverter.GetBytes(rawPort);
                    int localPort = (bytes[0] << 8) + bytes[1];
                    if (localPort != targetPort) continue;
                    int processId = Marshal.ReadInt32(row, processOffset);
                    if (processId > 0) result.Add(processId);
                }
            }
            catch (Exception exception)
            {
                FileLog.Warn("Port lookup failed: " + exception.Message);
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static class ProcessInspector
    {
        public static ProcessIdentity Get(int processId)
        {
            return Get(processId, true, true);
        }

        public static ProcessIdentity Get(int processId, bool includeServices)
        {
            return Get(processId, true, includeServices);
        }

        public static ProcessIdentity GetBasic(int processId)
        {
            return Get(processId, false, false);
        }

        private static ProcessIdentity Get(int processId, bool includeCommandLine, bool includeServices)
        {
            ProcessIdentity identity = new ProcessIdentity();
            identity.ProcessId = processId;
            identity.Name = "unknown";
            identity.ImagePath = String.Empty;
            identity.CommandLine = String.Empty;
            identity.SessionId = -1;
            identity.Services = new List<string>();

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    identity.Name = process.ProcessName;
                    try { identity.ImagePath = process.MainModule.FileName; } catch { }
                    try { identity.StartTimeUtc = process.StartTime.ToUniversalTime(); } catch { }
                    try { identity.SessionId = process.SessionId; } catch { }
                }
            }
            catch
            {
                return identity;
            }

            if (includeCommandLine)
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId=" + processId))
                    {
                        foreach (ManagementObject value in searcher.Get())
                        {
                            string path = Convert.ToString(value["ExecutablePath"]);
                            string commandLine = Convert.ToString(value["CommandLine"]);
                            if (!String.IsNullOrWhiteSpace(path)) identity.ImagePath = path;
                            if (!String.IsNullOrWhiteSpace(commandLine)) identity.CommandLine = commandLine;
                        }
                    }
                }
                catch (Exception exception)
                {
                    FileLog.Warn("Could not read process command line for PID " + processId + ": " + exception.Message);
                }
            }

            if (!includeServices) return identity;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE ProcessId=" + processId))
                {
                    foreach (ManagementObject value in searcher.Get())
                    {
                        string serviceName = Convert.ToString(value["Name"]);
                        if (!String.IsNullOrWhiteSpace(serviceName)) identity.Services.Add(serviceName);
                    }
                }
            }
            catch
            {
            }
            return identity;
        }

        public static bool IsSame(ProcessIdentity first, ProcessIdentity second)
        {
            try
            {
                if (first == null || second == null) return false;
                if (first.ProcessId != second.ProcessId) return false;
                if (!String.IsNullOrWhiteSpace(first.ImagePath) && !String.IsNullOrWhiteSpace(second.ImagePath) &&
                    !String.Equals(Path.GetFullPath(first.ImagePath), Path.GetFullPath(second.ImagePath), StringComparison.OrdinalIgnoreCase)) return false;
                if (first.StartTimeUtc.HasValue && second.StartTimeUtc.HasValue)
                {
                    if (Math.Abs((first.StartTimeUtc.Value - second.StartTimeUtc.Value).TotalSeconds) > 1) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static bool IsProtected(ProcessIdentity identity, out string reason)
        {
            reason = String.Empty;
            if (identity == null || identity.ProcessId <= 4)
            {
                reason = Localization.Text("Safety.SystemProcess");
                return true;
            }
            if (identity.ProcessId == Process.GetCurrentProcess().Id)
            {
                reason = Localization.Text("Safety.ManagerProcess");
                return true;
            }
            if (identity.SessionId >= 0 && identity.SessionId != Process.GetCurrentProcess().SessionId)
            {
                reason = Localization.Text("Safety.OtherSession");
                return true;
            }
            if (String.IsNullOrWhiteSpace(identity.ImagePath))
            {
                reason = Localization.Text("Safety.PathUnverified");
                return true;
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!String.IsNullOrWhiteSpace(windows))
            {
                string normalizedWindows = Path.GetFullPath(windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string normalizedImage = Path.GetFullPath(identity.ImagePath);
                if (normalizedImage.StartsWith(normalizedWindows, StringComparison.OrdinalIgnoreCase))
                {
                    reason = Localization.Text("Safety.WindowsPath");
                    return true;
                }
            }
            if (identity.Services != null && identity.Services.Count > 0)
            {
                reason = Localization.Format("Safety.WindowsService", String.Join(", ", identity.Services.ToArray()));
                return true;
            }
            return false;
        }
    }

    public static class SafeTermination
    {
        public static bool TryCloseThenKill(ProcessIdentity captured, int port, System.Windows.Forms.IWin32Window owner, out string error)
        {
            error = String.Empty;
            if (captured == null)
            {
                error = Localization.Text("Safety.NoIdentity");
                return false;
            }
            int currentOwner = PortMap.GetPreferredListenerProcessId(port);
            if (currentOwner != captured.ProcessId)
            {
                error = Localization.Text("Safety.PortChanged");
                return false;
            }
            ProcessIdentity current = ProcessInspector.Get(captured.ProcessId);
            if (!ProcessInspector.IsSame(captured, current))
            {
                error = Localization.Text("Safety.PidChanged");
                return false;
            }
            string protectedReason;
            if (ProcessInspector.IsProtected(current, out protectedReason))
            {
                error = Localization.Format("Safety.Protected", protectedReason);
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(current.ProcessId))
                {
                    bool closeRequested = false;
                    try { closeRequested = process.CloseMainWindow(); } catch { }
                    if (closeRequested && process.WaitForExit(3000)) return true;
                    System.Windows.Forms.DialogResult result = System.Windows.Forms.MessageBox.Show(
                        owner,
                        Localization.Format("Dialog.ForceEnd", current.ProcessId, current.Name, current.ImagePath),
                        Localization.Text("Dialog.ForceEndTitle"),
                        System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    if (result != System.Windows.Forms.DialogResult.Yes)
                    {
                        error = Localization.Text("Safety.Cancelled");
                        return false;
                    }
                    currentOwner = PortMap.GetPreferredListenerProcessId(port);
                    ProcessIdentity finalIdentity = ProcessInspector.Get(current.ProcessId);
                    if (currentOwner != current.ProcessId || !ProcessInspector.IsSame(current, finalIdentity))
                    {
                        error = Localization.Text("Safety.Changed");
                        return false;
                    }
                    process.Kill();
                    if (!process.WaitForExit(5000))
                    {
                        error = Localization.Text("Safety.ExitTimeout");
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
