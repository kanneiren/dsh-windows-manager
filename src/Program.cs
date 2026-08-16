using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    public sealed class SignalSet : IDisposable
    {
        public EventWaitHandle Open { get; private set; }
        public EventWaitHandle Start { get; private set; }
        public EventWaitHandle Stop { get; private set; }
        public EventWaitHandle Restart { get; private set; }
        public EventWaitHandle Exit { get; private set; }

        public SignalSet(string prefix, bool create)
        {
            if (!create) return;
            Open = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Open");
            Start = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Start");
            Stop = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Stop");
            Restart = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Restart");
            Exit = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Exit");
        }

        public static bool Signal(string prefix, string action)
        {
            string suffix = "-Open";
            if (String.Equals(action, "start", StringComparison.OrdinalIgnoreCase)) suffix = "-Start";
            else if (String.Equals(action, "stop", StringComparison.OrdinalIgnoreCase)) suffix = "-Stop";
            else if (String.Equals(action, "restart", StringComparison.OrdinalIgnoreCase)) suffix = "-Restart";
            else if (String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)) suffix = "-Exit";
            int attempt;
            for (attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (EventWaitHandle handle = EventWaitHandle.OpenExisting(prefix + suffix)) return handle.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(150);
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (Open != null) Open.Dispose();
            if (Start != null) Start.Dispose();
            if (Stop != null) Stop.Dispose();
            if (Restart != null) Restart.Dispose();
            if (Exit != null) Exit.Dispose();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string action = ParseAction(args);
            AppPaths.EnsureDirectories();
            FileLog.EnforceRetention();
            Localization.Initialize("auto");
            FileLog.Info("Manager invocation, action=" + action);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs eventArgs)
            {
                FileLog.Error(eventArgs.Exception);
                MessageBox.Show(Localization.Format("Error.Unhandled", eventArgs.Exception.Message, AppPaths.ManagerLog),
                    Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
            {
                Exception exception = eventArgs.ExceptionObject as Exception;
                FileLog.Error(exception ?? new Exception(Convert.ToString(eventArgs.ExceptionObject)));
            };

            string sid = WindowsIdentity.GetCurrent().User.Value.Replace('-', '_');
            string prefix = "Local\\DeepSeekHarnessManager-" + sid;
            bool createdNew;
            using (Mutex mutex = new Mutex(true, prefix, out createdNew))
            {
                if (!createdNew)
                {
                    if (!String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> controlResponse;
                        string controlError;
                        if (ManagerControlClient.TryRequest(action, null, ManagerControlProtocol.GetDefaultPipeName(), out controlResponse, out controlError))
                            return 0;
                        FileLog.Warn("Manager control pipe unavailable, falling back to legacy event signal: " + controlError);
                    }
                    SignalSet.Signal(prefix, action);
                    return 0;
                }
                if (String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)) return 0;
                using (SignalSet signals = new SignalSet(prefix, true))
                {
                    try
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        PluginCatalog catalog = PluginCatalog.Load();
                        ConfigurationStore store = new ConfigurationStore(catalog);
                        ManagerConfig config = store.LoadOrCreate();
                        Localization.Initialize(config.Language);
                        bool trayEnabled = !config.TrayEnabled.HasValue || config.TrayEnabled.Value;
                        IManagerInteraction interaction = trayEnabled
                            ? (IManagerInteraction)new WinFormsManagerInteraction()
                            : SilentManagerInteraction.Instance;
                        FileLog.Info("Tray mode: " + (trayEnabled ? "enabled" : "disabled"));
                        using (ManagerService managerService = new ManagerService(config, catalog, store, signals, interaction))
                        {
                            if (trayEnabled)
                            {
                                using (TrayFrontend frontend = new TrayFrontend(managerService, action))
                                using (ManagerControlServer controlServer = new ManagerControlServer(managerService))
                                {
                                    controlServer.Start();
                                    Application.Run(frontend);
                                }
                            }
                            else
                            {
                                using (HeadlessFrontend frontend = new HeadlessFrontend(managerService, action))
                                using (ManagerControlServer controlServer = new ManagerControlServer(managerService))
                                {
                                    controlServer.Start();
                                    Application.Run(frontend);
                                }
                            }
                        }
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        FileLog.Error(exception);
                        MessageBox.Show(Localization.Format("Error.StartManager", exception.Message, AppPaths.ManagerLog),
                            Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                    finally
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        private static string ParseAction(string[] args)
        {
            if (args == null) return "open";
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], "--action", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string value = args[i + 1].ToLowerInvariant();
                    if (value == "open" || value == "start" || value == "stop" || value == "restart" || value == "exit") return value;
                }
            }
            return "open";
        }
    }
}
