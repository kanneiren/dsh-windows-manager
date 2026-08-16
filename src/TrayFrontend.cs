using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    public sealed class TrayFrontend : ApplicationContext
    {
        private readonly IManagerService manager;
        private readonly string initialAction;
        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip menu;
        private readonly Timer timer;
        private readonly Dictionary<string, InstanceMenuBinding> menuBindings;
        private readonly Dictionary<InstanceStateKind, Icon> icons;
        private readonly List<Icon> ownedIcons;
        private readonly Control uiSink;
        private bool initialActionHandled;
        private bool exitRequested;
        private string lastUiSignature;

        public TrayFrontend(IManagerService managerService, string action)
        {
            manager = managerService;
            initialAction = action;
            menuBindings = new Dictionary<string, InstanceMenuBinding>(StringComparer.OrdinalIgnoreCase);
            icons = new Dictionary<InstanceStateKind, Icon>();
            ownedIcons = new List<Icon>();
            uiSink = new Control();
            uiSink.CreateControl();

            manager.Changed += ManagerChanged;
            manager.ExitRequested += ManagerExitRequested;

            LoadIcons();
            menu = new ContextMenuStrip();
            BuildMenu();
            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Icon = GetIcon(InstanceStateKind.Stopped);
            notifyIcon.Text = Localization.Text("App.Title");
            notifyIcon.DoubleClick += delegate { manager.Open(null); };
            notifyIcon.Visible = true;

            manager.TickInstances();
            RefreshUi();

            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += TimerTick;
            timer.Start();
            HandleInitialAction();
            manager.StartAutomaticUpdateChecks();
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            foreach (Icon icon in ownedIcons) icon.Dispose();
            timer.Dispose();
            uiSink.Dispose();
            base.ExitThreadCore();
        }

        private void BuildMenu()
        {
            ManagerSnapshot snapshot = manager.GetSnapshot();
            if (snapshot.Instances.Count == 1)
            {
                BuildInstanceMenu(menu.Items, snapshot.Instances[0]);
            }
            else
            {
                foreach (InstanceSnapshot instance in snapshot.Instances)
                {
                    ToolStripMenuItem instanceRoot = new ToolStripMenuItem(instance.Name);
                    menu.Items.Add(instanceRoot);
                    BuildInstanceMenu(instanceRoot.DropDownItems, instance);
                }
            }

            menu.Items.Add(new ToolStripSeparator());
            string marketplaceUrl = null;
            foreach (InstanceSnapshot instance in snapshot.Instances)
            {
                if (!String.IsNullOrWhiteSpace(instance.MarketplaceUrl))
                {
                    marketplaceUrl = instance.MarketplaceUrl;
                    break;
                }
            }
            if (!String.IsNullOrWhiteSpace(marketplaceUrl))
            {
                string url = marketplaceUrl;
                ToolStripMenuItem marketplace = new ToolStripMenuItem(Localization.Text("Menu.PluginMarketplace"));
                marketplace.Click += delegate { manager.OpenUrl(url); };
                menu.Items.Add(marketplace);
            }

            ToolStripMenuItem openManagerConfig = new ToolStripMenuItem(Localization.Text("Menu.OpenManagerConfig"));
            openManagerConfig.Click += delegate { manager.OpenConfiguration(); };
            menu.Items.Add(openManagerConfig);
            ToolStripMenuItem copyDiagnostics = new ToolStripMenuItem(Localization.Text("Menu.CopyDiagnostics"));
            copyDiagnostics.Click += delegate { CopyDiagnostics(); };
            menu.Items.Add(copyDiagnostics);
            ToolStripMenuItem openManagerLogs = new ToolStripMenuItem(Localization.Text("Menu.OpenManagerLogs"));
            openManagerLogs.Click += delegate { manager.OpenManagerLogs(); };
            menu.Items.Add(openManagerLogs);
            ToolStripMenuItem openDshLogs = new ToolStripMenuItem(Localization.Text("Menu.OpenDshLogs"));
            openDshLogs.Click += delegate { manager.OpenDshLogs(); };
            menu.Items.Add(openDshLogs);
            ToolStripMenuItem language = new ToolStripMenuItem(Localization.Text("Menu.Language"));
            ToolStripMenuItem autoLanguage = new ToolStripMenuItem(Localization.Text("Language.Auto"));
            ToolStripMenuItem chinese = new ToolStripMenuItem(Localization.Text("Language.Chinese"));
            ToolStripMenuItem english = new ToolStripMenuItem(Localization.Text("Language.English"));
            autoLanguage.Checked = snapshot.Language == "auto";
            chinese.Checked = snapshot.Language == "zh-CN";
            english.Checked = snapshot.Language == "en-US";
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
                MessageBox.Show(Localization.Format("About.Body", Application.ProductVersion),
                    Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(about);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem(Localization.Text("Menu.Exit"));
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);
        }

        private void BuildInstanceMenu(ToolStripItemCollection items, InstanceSnapshot instance)
        {
            string instanceId = instance.Id;
            InstanceMenuBinding binding = new InstanceMenuBinding();
            binding.Status = new ToolStripMenuItem(Localization.Text("Menu.Status") + ": " + Localization.Text("Menu.Checking"));
            binding.Status.Enabled = false;
            binding.Version = new ToolStripMenuItem(Localization.Text("Menu.Version") + ": " + Localization.Text("Menu.Checking"));
            binding.Version.Enabled = false;
            binding.Open = new ToolStripMenuItem(LocalizedFrontendMenu(instance));
            binding.Start = new ToolStripMenuItem(Localization.Text("Menu.Start"));
            binding.Stop = new ToolStripMenuItem(Localization.Text("Menu.Stop"));
            binding.Restart = new ToolStripMenuItem(Localization.Text("Menu.Restart"));
            binding.CheckUpdate = new ToolStripMenuItem(Localization.Text("Menu.CheckUpdate"));
            binding.UpdateNow = new ToolStripMenuItem(Localization.Text("Menu.InstallUpdate"));
            binding.Details = new ToolStripMenuItem(Localization.Text("Menu.Details"));
            binding.Workspace = new ToolStripMenuItem(Localization.Text("Menu.Workspace"));
            ToolStripMenuItem dshSettings = new ToolStripMenuItem(Localization.Text("Menu.OpenDshSettings"));

            binding.Open.Click += delegate { manager.Open(instanceId); };
            binding.Start.Click += delegate { manager.Start(instanceId); };
            binding.Stop.Click += delegate { manager.Stop(instanceId, true); };
            binding.Restart.Click += delegate { manager.Restart(instanceId); };
            binding.CheckUpdate.Click += delegate { CheckUpdateAsync(instanceId, true, true); };
            binding.UpdateNow.Click += delegate { manager.InstallUpdate(instanceId); RefreshUi(); };
            binding.Details.Click += delegate { MessageBox.Show(manager.GetInstanceDetails(instanceId), Localization.Text("Details.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information); };
            binding.Workspace.Click += delegate { manager.OpenInstanceWorkspace(instanceId); };
            dshSettings.Click += delegate { manager.OpenDshSettings(instanceId); };

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
            items.Add(dshSettings);
            menuBindings.Add(instanceId, binding);
        }

        private async void CheckUpdateAsync(string instanceId, bool force, bool reportCurrent)
        {
            InstanceMenuBinding binding = menuBindings[instanceId];
            binding.CheckUpdate.Enabled = false;
            binding.CheckUpdate.Text = Localization.Text("Menu.CheckingUpdate");
            try
            {
                UpdateCheckResult result = await manager.CheckForUpdatesAsync(instanceId, force);
                if (result == null || result.InProgress) return;
                if (!String.IsNullOrWhiteSpace(result.Error))
                {
                    if (reportCurrent)
                        MessageBox.Show(Localization.Format("Update.CheckFailed", result.Error), Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else if (result.Info.UpdateAvailable)
                {
                    notifyIcon.BalloonTipTitle = Localization.Text("Update.AvailableTitle");
                    notifyIcon.BalloonTipText = Localization.Format("Update.AvailableBody", result.Info.InstalledVersion, result.Info.LatestVersion);
                    notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                    notifyIcon.ShowBalloonTip(5000);
                }
                else if (reportCurrent)
                {
                    MessageBox.Show(Localization.Format("Update.Current", result.Info.InstalledVersion, result.Info.LatestVersion),
                        Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            finally
            {
                RefreshUi();
            }
        }

        private void TimerTick(object sender, EventArgs args)
        {
            try
            {
                manager.Tick();
                if (exitRequested) return;
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
            manager.HandleInitialAction(initialAction);
        }

        private void CopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(manager.GetDiagnosticsText());
                notifyIcon.BalloonTipTitle = Localization.Text("App.Title");
                notifyIcon.BalloonTipText = Localization.Text("Diagnostics.Copied");
                notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                notifyIcon.ShowBalloonTip(3000);
            }
            catch (Exception exception)
            {
                FileLog.Warn("Could not copy diagnostics to the clipboard: " + exception.Message);
                MessageBox.Show(Localization.Format("Diagnostics.CopyFailed", exception.Message),
                    Localization.Text("App.Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ManagerChanged(object sender, EventArgs args)
        {
            if (uiSink.IsDisposed) return;
            try
            {
                uiSink.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        manager.DrainNotifications();
                        RefreshUi();
                    }
                    catch (Exception exception)
                    {
                        FileLog.Error(exception);
                    }
                });
            }
            catch (Exception exception)
            {
                FileLog.Warn("Could not marshal manager notification to the UI: " + exception.Message);
            }
        }

        private void ManagerExitRequested(object sender, EventArgs args)
        {
            exitRequested = true;
            ExitThread();
        }

        private void RefreshUi()
        {
            ManagerSnapshot snapshot = manager.GetSnapshot();
            string signature = BuildUiSignature(snapshot);
            if (String.Equals(signature, lastUiSignature, StringComparison.Ordinal)) return;
            lastUiSignature = signature;
            foreach (InstanceSnapshot instance in snapshot.Instances)
            {
                InstanceMenuBinding binding = menuBindings[instance.Id];
                binding.Status.Text = Localization.Text("Menu.Status") + ": " + LocalizedState(instance);
                string installed = String.IsNullOrWhiteSpace(instance.InstalledVersion) ? Localization.Text("Version.Unknown") : instance.InstalledVersion;
                if (instance.UpdateAvailable)
                    binding.Version.Text = Localization.Text("Menu.Version") + ": " + installed + " (" + Localization.Format("Version.Update", instance.LatestVersion) + ")";
                else binding.Version.Text = Localization.Text("Menu.Version") + ": " + installed;
                binding.Open.Enabled = instance.State != InstanceStateKind.Updating && instance.State != InstanceStateKind.Stopping;
                binding.Start.Enabled = instance.State == InstanceStateKind.Stopped || instance.State == InstanceStateKind.Conflict || instance.State == InstanceStateKind.Error;
                binding.Stop.Enabled = instance.State == InstanceStateKind.Running || instance.State == InstanceStateKind.Starting;
                binding.Restart.Enabled = instance.State == InstanceStateKind.Running;
                binding.UpdateNow.Visible = instance.UpdateAvailable;
                binding.UpdateNow.Enabled = instance.State != InstanceStateKind.Starting && instance.State != InstanceStateKind.Stopping && instance.State != InstanceStateKind.Updating;
                binding.CheckUpdate.Enabled = !instance.UpdateCheckInProgress;
                binding.CheckUpdate.Text = instance.UpdateCheckInProgress ? Localization.Text("Menu.CheckingUpdate") : Localization.Text("Menu.CheckUpdate");
            }
            InstanceStateKind aggregate = AggregateState(snapshot);
            notifyIcon.Icon = GetIcon(aggregate);
            string text = Localization.Text("App.Title") + " - " + LocalizedStateName(aggregate);
            notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private string BuildUiSignature(ManagerSnapshot snapshot)
        {
            List<string> values = new List<string>();
            values.Add(Localization.CurrentLanguage ?? String.Empty);
            foreach (InstanceSnapshot instance in snapshot.Instances)
            {
                values.Add(instance.Id);
                values.Add(instance.State.ToString());
                values.Add(instance.ActivePort.ToString());
                values.Add(instance.InstalledVersion ?? String.Empty);
                values.Add(instance.IpcBridgeConnected ? "1" : "0");
                values.Add(instance.BridgeState ?? String.Empty);
                values.Add(instance.LatestVersion ?? String.Empty);
                values.Add(instance.UpdateAvailable ? "1" : "0");
                values.Add(instance.UpdateCheckInProgress ? "1" : "0");
            }
            return String.Join("|", values.ToArray());
        }

        private InstanceStateKind AggregateState(ManagerSnapshot snapshot)
        {
            foreach (InstanceSnapshot instance in snapshot.Instances)
                if (instance.State == InstanceStateKind.Error) return InstanceStateKind.Error;
            foreach (InstanceSnapshot instance in snapshot.Instances)
                if (instance.State == InstanceStateKind.Conflict) return InstanceStateKind.Conflict;
            foreach (InstanceSnapshot instance in snapshot.Instances)
                if (instance.State == InstanceStateKind.Starting || instance.State == InstanceStateKind.Updating) return InstanceStateKind.Starting;
            foreach (InstanceSnapshot instance in snapshot.Instances)
                if (instance.State == InstanceStateKind.Stopping) return InstanceStateKind.Stopping;
            foreach (InstanceSnapshot instance in snapshot.Instances)
                if (instance.State == InstanceStateKind.Running) return InstanceStateKind.Running;
            return InstanceStateKind.Stopped;
        }

        private string LocalizedFrontendMenu(InstanceSnapshot instance)
        {
            string frontend = String.IsNullOrWhiteSpace(instance.Frontend) ? InstanceModel.FrontendWeb : instance.Frontend;
            if (String.Equals(frontend, InstanceModel.FrontendOhDsh, StringComparison.OrdinalIgnoreCase))
                return Localization.Text("Menu.OpenOhDsh");
            if (String.Equals(frontend, InstanceModel.FrontendCustom, StringComparison.OrdinalIgnoreCase))
                return Localization.Text("Menu.OpenCustom");
            return Localization.Text("Menu.OpenWeb");
        }

        private string LocalizedState(InstanceSnapshot instance)
        {
            int port = instance.ActivePort > 0 ? instance.ActivePort : instance.PreferredPort;
            if (instance.State == InstanceStateKind.Running) return Localization.Format("State.Running", port);
            if (instance.State == InstanceStateKind.Starting) return Localization.Format("State.Starting", port);
            if (instance.State == InstanceStateKind.Stopping) return Localization.Format("State.Stopping", port);
            if (instance.State == InstanceStateKind.Conflict) return Localization.Format("State.Conflict", instance.PreferredPort);
            return LocalizedStateName(instance.State);
        }

        private string LocalizedStateName(InstanceStateKind state)
        {
            if (state == InstanceStateKind.Stopped) return Localization.Text("State.Stopped");
            if (state == InstanceStateKind.Starting) return Localization.Text("State.StartingName");
            if (state == InstanceStateKind.Running) return Localization.Text("State.RunningName");
            if (state == InstanceStateKind.Stopping) return Localization.Text("State.StoppingName");
            if (state == InstanceStateKind.Conflict) return Localization.Text("State.ConflictName");
            if (state == InstanceStateKind.Updating) return Localization.Text("State.Updating");
            if (state == InstanceStateKind.Error) return Localization.Text("State.Error");
            return Localization.Text("State.Unknown");
        }

        private void ChangeLanguage(string language)
        {
            manager.SetLanguage(language);
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
            icons[InstanceStateKind.Stopping] = icons[InstanceStateKind.Starting];
        }

        private void LoadIcon(InstanceStateKind state, string fileName)
        {
            string path = System.IO.Path.Combine(AppPaths.AssetDirectory, fileName);
            if (System.IO.File.Exists(path))
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
