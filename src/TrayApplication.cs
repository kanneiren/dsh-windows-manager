using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    public sealed class TrayApplication : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip menu;
        private readonly Timer timer;
        private readonly List<InstanceController> controllers;
        private readonly Dictionary<string, InstanceMenuBinding> menuBindings;
        private readonly Dictionary<InstanceStateKind, Icon> icons;
        private readonly Dictionary<string, DateTime> nextUpdateChecksUtc;
        private readonly HashSet<string> updateChecksInFlight;
        private readonly List<Icon> ownedIcons;
        private readonly ManagerConfig config;
        private readonly ConfigurationStore configurationStore;
        private readonly UpdateManager updateManager;
        private readonly SignalSet signals;
        private readonly string initialAction;
        private bool initialActionHandled;
        private string lastUiSignature;

        public TrayApplication(ManagerConfig managerConfig, PluginCatalog catalog, ConfigurationStore store, SignalSet signalSet, string action)
        {
            config = managerConfig;
            configurationStore = store;
            signals = signalSet;
            initialAction = action;
            controllers = new List<InstanceController>();
            menuBindings = new Dictionary<string, InstanceMenuBinding>(StringComparer.OrdinalIgnoreCase);
            icons = new Dictionary<InstanceStateKind, Icon>();
            nextUpdateChecksUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            updateChecksInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ownedIcons = new List<Icon>();
            updateManager = new UpdateManager(store, config);

            foreach (InstanceConfig instance in config.Instances)
            {
                InstanceController controller = new InstanceController(instance, catalog.Get(instance.PluginId), store);
                controller.Changed += ControllerChanged;
                controllers.Add(controller);
            }

            LoadIcons();
            menu = new ContextMenuStrip();
            BuildMenu();
            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Icon = GetIcon(InstanceStateKind.Stopped);
            notifyIcon.Text = Localization.Text("App.Title");
            notifyIcon.DoubleClick += delegate { DefaultController.OpenOrStart(null); };
            notifyIcon.Visible = true;

            foreach (InstanceController controller in controllers) controller.Tick();
            RefreshUi();

            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += TimerTick;
            timer.Start();
            HandleInitialAction();
            foreach (InstanceController controller in controllers) CheckUpdateAsync(controller, false, false);
        }

        private InstanceController DefaultController
        {
            get
            {
                InstanceController controller = controllers.FirstOrDefault(delegate(InstanceController item) { return item.Config.Id.Equals(config.DefaultInstanceId, StringComparison.OrdinalIgnoreCase); });
                return controller ?? controllers[0];
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            foreach (Icon icon in ownedIcons) icon.Dispose();
            timer.Dispose();
            base.ExitThreadCore();
        }

        private void BuildMenu()
        {
            if (controllers.Count == 1)
            {
                BuildInstanceMenu(menu.Items, controllers[0]);
            }
            else
            {
                foreach (InstanceController controller in controllers)
                {
                    ToolStripMenuItem instanceRoot = new ToolStripMenuItem(controller.Config.Name);
                    menu.Items.Add(instanceRoot);
                    BuildInstanceMenu(instanceRoot.DropDownItems, controller);
                }
            }
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem openConfig = new ToolStripMenuItem(Localization.Text("Menu.OpenConfig"));
            openConfig.Click += delegate { OpenFile(AppPaths.ConfigFile); };
            menu.Items.Add(openConfig);
            ToolStripMenuItem openLogs = new ToolStripMenuItem(Localization.Text("Menu.OpenLogs"));
            openLogs.Click += delegate { OpenFolder(AppPaths.LogDirectory); };
            menu.Items.Add(openLogs);
            ToolStripMenuItem language = new ToolStripMenuItem(Localization.Text("Menu.Language"));
            ToolStripMenuItem autoLanguage = new ToolStripMenuItem(Localization.Text("Language.Auto"));
            ToolStripMenuItem chinese = new ToolStripMenuItem(Localization.Text("Language.Chinese"));
            ToolStripMenuItem english = new ToolStripMenuItem(Localization.Text("Language.English"));
            autoLanguage.Checked = config.Language == "auto";
            chinese.Checked = config.Language == "zh-CN";
            english.Checked = config.Language == "en-US";
            autoLanguage.Click += delegate { ChangeLanguage("auto"); };
            chinese.Click += delegate { ChangeLanguage("zh-CN"); };
            english.Click += delegate { ChangeLanguage("en-US"); };
            language.DropDownItems.Add(autoLanguage);
            language.DropDownItems.Add(chinese);
            language.DropDownItems.Add(english);
            menu.Items.Add(language);
            ToolStripMenuItem about = new ToolStripMenuItem(Localization.Text("Menu.About"));
            about.Click += delegate
            {
                MessageBox.Show(Localization.Format("About.Body", String.Join(", ", controllers.Select(delegate(InstanceController item) { return item.Plugin.Id; }).Distinct().ToArray())),
                    Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(about);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem(Localization.Text("Menu.Exit"));
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);
        }

        private void BuildInstanceMenu(ToolStripItemCollection items, InstanceController controller)
        {
            InstanceMenuBinding binding = new InstanceMenuBinding();
            binding.Status = new ToolStripMenuItem(Localization.Text("Menu.Status") + ": " + Localization.Text("Menu.Checking"));
            binding.Status.Enabled = false;
            binding.Version = new ToolStripMenuItem(Localization.Text("Menu.Version") + ": " + Localization.Text("Menu.Checking"));
            binding.Version.Enabled = false;
            binding.Open = new ToolStripMenuItem(Localization.Text("Menu.OpenWeb"));
            binding.Start = new ToolStripMenuItem(Localization.Text("Menu.Start"));
            binding.Stop = new ToolStripMenuItem(Localization.Text("Menu.Stop"));
            binding.Restart = new ToolStripMenuItem(Localization.Text("Menu.Restart"));
            binding.CheckUpdate = new ToolStripMenuItem(Localization.Text("Menu.CheckUpdate"));
            binding.UpdateNow = new ToolStripMenuItem(Localization.Text("Menu.InstallUpdate"));
            binding.Details = new ToolStripMenuItem(Localization.Text("Menu.Details"));
            binding.Workspace = new ToolStripMenuItem(Localization.Text("Menu.Workspace"));

            binding.Open.Click += delegate { controller.OpenOrStart(null); };
            binding.Start.Click += delegate { controller.Start(controller.Config.PreferredPort, false, null); };
            binding.Stop.Click += delegate { controller.Stop(null, true); };
            binding.Restart.Click += delegate { controller.Restart(null); };
            binding.CheckUpdate.Click += delegate { CheckUpdateAsync(controller, true, true); };
            binding.UpdateNow.Click += delegate { updateManager.ExecuteConfirmedUpdate(controller, null); RefreshUi(); };
            binding.Details.Click += delegate { MessageBox.Show(controller.GetDetails(), Localization.Text("Details.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information); };
            binding.Workspace.Click += delegate { OpenFolder(controller.Config.Workspace); };

            items.Add(binding.Status);
            items.Add(binding.Version);
            items.Add(new ToolStripSeparator());
            items.Add(binding.Open);
            items.Add(binding.Start);
            items.Add(binding.Stop);
            items.Add(binding.Restart);
            items.Add(new ToolStripSeparator());
            items.Add(binding.CheckUpdate);
            items.Add(binding.UpdateNow);
            items.Add(new ToolStripSeparator());
            items.Add(binding.Details);
            items.Add(binding.Workspace);
            menuBindings.Add(controller.Config.Id, binding);
        }

        private async void CheckUpdateAsync(InstanceController controller, bool force, bool reportCurrent)
        {
            if (updateChecksInFlight.Contains(controller.Config.Id)) return;
            updateChecksInFlight.Add(controller.Config.Id);
            InstanceMenuBinding binding = menuBindings[controller.Config.Id];
            binding.CheckUpdate.Enabled = false;
            binding.CheckUpdate.Text = Localization.Text("Menu.CheckingUpdate");
            try
            {
                UpdateInfo info = await updateManager.CheckAsync(controller, force);
                controller.UpdateInfo = info;
                controller.InstalledVersion = info.InstalledVersion;
                nextUpdateChecksUtc[controller.Config.Id] = UpdateManager.NextAutomaticCheckUtc(info.CheckedAtUtc);
                if (info.UpdateAvailable)
                {
                    notifyIcon.BalloonTipTitle = Localization.Text("Update.AvailableTitle");
                    notifyIcon.BalloonTipText = Localization.Format("Update.AvailableBody", info.InstalledVersion, info.LatestVersion);
                    notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                    notifyIcon.ShowBalloonTip(5000);
                }
                else if (reportCurrent)
                {
                    MessageBox.Show(Localization.Format("Update.Current", info.InstalledVersion, info.LatestVersion),
                        Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception exception)
            {
                FileLog.Warn("Update check failed for " + controller.Config.Id + ": " + exception.Message);
                nextUpdateChecksUtc[controller.Config.Id] = DateTime.UtcNow.AddHours(24);
                if (reportCurrent) MessageBox.Show(Localization.Format("Update.CheckFailed", exception.Message), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                updateChecksInFlight.Remove(controller.Config.Id);
                binding.CheckUpdate.Enabled = true;
                binding.CheckUpdate.Text = Localization.Text("Menu.CheckUpdate");
                RefreshUi();
            }
        }

        private void TimerTick(object sender, EventArgs args)
        {
            try
            {
                if (signals.Open.WaitOne(0)) DefaultController.OpenOrStart(null);
                if (signals.Start.WaitOne(0)) DefaultController.Start(DefaultController.Config.PreferredPort, false, null);
                if (signals.Stop.WaitOne(0)) DefaultController.Stop(null, false);
                if (signals.Restart.WaitOne(0)) DefaultController.Restart(null);
                if (signals.Exit.WaitOne(0))
                {
                    ExitThread();
                    return;
                }
                foreach (InstanceController controller in controllers) controller.Tick();
                foreach (InstanceController controller in controllers)
                {
                    DateTime next;
                    if (nextUpdateChecksUtc.TryGetValue(controller.Config.Id, out next) && DateTime.UtcNow >= next)
                        CheckUpdateAsync(controller, false, false);
                }
                RefreshUi();
            }
            catch (Exception exception)
            {
                FileLog.Error(exception);
            }
        }

        private void HandleInitialAction()
        {
            if (initialActionHandled) return;
            initialActionHandled = true;
            if (String.Equals(initialAction, "start", StringComparison.OrdinalIgnoreCase)) DefaultController.Start(DefaultController.Config.PreferredPort, false, null);
            else if (String.Equals(initialAction, "stop", StringComparison.OrdinalIgnoreCase))
            {
                DefaultController.Stop(null, false);
                ExitThread();
            }
            else if (String.Equals(initialAction, "restart", StringComparison.OrdinalIgnoreCase)) DefaultController.Restart(null);
            else if (String.Equals(initialAction, "exit", StringComparison.OrdinalIgnoreCase)) ExitThread();
            else DefaultController.OpenOrStart(null);
        }

        private void ControllerChanged(object sender, EventArgs args)
        {
            RefreshUi();
        }

        private void RefreshUi()
        {
            string signature = BuildUiSignature();
            if (String.Equals(signature, lastUiSignature, StringComparison.Ordinal)) return;
            lastUiSignature = signature;
            foreach (InstanceController controller in controllers)
            {
                InstanceMenuBinding binding = menuBindings[controller.Config.Id];
                binding.Status.Text = Localization.Text("Menu.Status") + ": " + LocalizedState(controller);
                string installed = String.IsNullOrWhiteSpace(controller.InstalledVersion) ? Localization.Text("Version.Unknown") : controller.InstalledVersion;
                if (controller.UpdateInfo != null && controller.UpdateInfo.UpdateAvailable)
                    binding.Version.Text = Localization.Text("Menu.Version") + ": " + installed + " (" + Localization.Format("Version.Update", controller.UpdateInfo.LatestVersion) + ")";
                else binding.Version.Text = Localization.Text("Menu.Version") + ": " + installed;
                binding.Open.Enabled = controller.State != InstanceStateKind.Updating;
                binding.Start.Enabled = controller.State == InstanceStateKind.Stopped || controller.State == InstanceStateKind.Conflict || controller.State == InstanceStateKind.Error;
                binding.Stop.Enabled = controller.State == InstanceStateKind.Running || controller.State == InstanceStateKind.Starting;
                binding.Restart.Enabled = controller.State == InstanceStateKind.Running;
                binding.UpdateNow.Visible = controller.UpdateInfo != null && controller.UpdateInfo.UpdateAvailable;
                binding.UpdateNow.Enabled = controller.State != InstanceStateKind.Starting && controller.State != InstanceStateKind.Updating;
            }
            InstanceStateKind aggregate = AggregateState();
            notifyIcon.Icon = GetIcon(aggregate);
            string text = Localization.Text("App.Title") + " - " + LocalizedStateName(aggregate);
            notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private string BuildUiSignature()
        {
            List<string> values = new List<string>();
            values.Add(Localization.CurrentLanguage ?? String.Empty);
            foreach (InstanceController controller in controllers)
            {
                values.Add(controller.Config.Id);
                values.Add(controller.State.ToString());
                values.Add(controller.ActivePort.ToString());
                values.Add(controller.InstalledVersion ?? String.Empty);
                values.Add(controller.UpdateInfo == null ? String.Empty : controller.UpdateInfo.LatestVersion ?? String.Empty);
                values.Add(controller.UpdateInfo != null && controller.UpdateInfo.UpdateAvailable ? "1" : "0");
            }
            return String.Join("|", values.ToArray());
        }

        private InstanceStateKind AggregateState()
        {
            if (controllers.Any(delegate(InstanceController item) { return item.State == InstanceStateKind.Error; })) return InstanceStateKind.Error;
            if (controllers.Any(delegate(InstanceController item) { return item.State == InstanceStateKind.Conflict; })) return InstanceStateKind.Conflict;
            if (controllers.Any(delegate(InstanceController item) { return item.State == InstanceStateKind.Starting || item.State == InstanceStateKind.Updating; })) return InstanceStateKind.Starting;
            if (controllers.Any(delegate(InstanceController item) { return item.State == InstanceStateKind.Running; })) return InstanceStateKind.Running;
            return InstanceStateKind.Stopped;
        }

        private string LocalizedState(InstanceController controller)
        {
            int port = controller.ActivePort > 0 ? controller.ActivePort : controller.Config.PreferredPort;
            if (controller.State == InstanceStateKind.Running) return Localization.Format("State.Running", port);
            if (controller.State == InstanceStateKind.Starting) return Localization.Format("State.Starting", port);
            if (controller.State == InstanceStateKind.Conflict) return Localization.Format("State.Conflict", controller.Config.PreferredPort);
            return LocalizedStateName(controller.State);
        }

        private string LocalizedStateName(InstanceStateKind state)
        {
            if (state == InstanceStateKind.Stopped) return Localization.Text("State.Stopped");
            if (state == InstanceStateKind.Starting) return Localization.Text("State.StartingName");
            if (state == InstanceStateKind.Running) return Localization.Text("State.RunningName");
            if (state == InstanceStateKind.Conflict) return Localization.Text("State.ConflictName");
            if (state == InstanceStateKind.Updating) return Localization.Text("State.Updating");
            if (state == InstanceStateKind.Error) return Localization.Text("State.Error");
            return Localization.Text("State.Unknown");
        }

        private void ChangeLanguage(string language)
        {
            config.Language = language;
            configurationStore.Save(config);
            Localization.Initialize(language);
            menu.SuspendLayout();
            menu.Items.Clear();
            menuBindings.Clear();
            BuildMenu();
            menu.ResumeLayout();
            lastUiSignature = null;
            RefreshUi();
        }

        private void LoadIcons()
        {
            LoadIcon(InstanceStateKind.Running, "deepseek-whale-running.ico");
            LoadIcon(InstanceStateKind.Starting, "deepseek-whale-starting.ico");
            LoadIcon(InstanceStateKind.Stopped, "deepseek-whale-stopped.ico");
            LoadIcon(InstanceStateKind.Conflict, "deepseek-whale-conflict.ico");
            LoadIcon(InstanceStateKind.Error, "deepseek-whale-error.ico");
            icons[InstanceStateKind.Updating] = icons[InstanceStateKind.Starting];
        }

        private void LoadIcon(InstanceStateKind state, string fileName)
        {
            string path = Path.Combine(AppPaths.AssetDirectory, fileName);
            if (File.Exists(path))
            {
                Icon icon = new Icon(path);
                icons[state] = icon;
                ownedIcons.Add(icon);
            }
            else icons[state] = SystemIcons.Application;
        }

        private Icon GetIcon(InstanceStateKind state)
        {
            Icon icon;
            return icons.TryGetValue(state, out icon) ? icon : SystemIcons.Application;
        }

        private static void OpenFolder(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", CommandRunner.QuoteArgument(path)) { UseShellExecute = true });
        }

        private static void OpenFile(string path)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", CommandRunner.QuoteArgument(path)) { UseShellExecute = true });
        }

        private sealed class InstanceMenuBinding
        {
            public ToolStripMenuItem Status;
            public ToolStripMenuItem Version;
            public ToolStripMenuItem Open;
            public ToolStripMenuItem Start;
            public ToolStripMenuItem Stop;
            public ToolStripMenuItem Restart;
            public ToolStripMenuItem CheckUpdate;
            public ToolStripMenuItem UpdateNow;
            public ToolStripMenuItem Details;
            public ToolStripMenuItem Workspace;
        }
    }
}
