using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
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
                    Dictionary<string, object> controlResponse;
                    string controlError = String.Empty;
                    int attempt;
                    for (attempt = 0; attempt < 10; attempt++)
                    {
                        if (ManagerControlClient.TryRequest(action, null, ManagerControlProtocol.GetDefaultPipeName(), out controlResponse, out controlError))
                            return 0;
                        Thread.Sleep(150);
                    }
                    FileLog.Error("Could not reach the primary Manager control pipe: " + controlError);
                    return 1;
                }
                if (String.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)) return 0;
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
                    using (ManagerService managerService = new ManagerService(config, catalog, store, interaction))
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

        private static string ParseAction(string[] args)
        {
            if (args == null) return "tray";
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], "--action", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string value = args[i + 1].ToLowerInvariant();
                    if (value == "open" || value == "start" || value == "stop" || value == "restart" || value == "exit" || value == "tray") return value;
                }
            }
            return "tray";
        }
    }
}
