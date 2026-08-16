using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessManager
{
    public sealed class PluginCatalog
    {
        private readonly Dictionary<string, PluginDefinition> plugins;

        private PluginCatalog(Dictionary<string, PluginDefinition> pluginsById)
        {
            plugins = pluginsById;
        }

        public IEnumerable<PluginDefinition> All { get { return plugins.Values; } }

        public PluginDefinition Get(string id)
        {
            PluginDefinition plugin;
            if (!plugins.TryGetValue(id ?? String.Empty, out plugin))
                throw new InvalidOperationException("Plugin not found: " + id);
            return plugin;
        }

        public static PluginCatalog Load()
        {
            if (!Directory.Exists(AppPaths.PluginDirectory))
                throw new DirectoryNotFoundException("Plugin directory not found: " + AppPaths.PluginDirectory);
            Dictionary<string, PluginDefinition> result = new Dictionary<string, PluginDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(AppPaths.PluginDirectory, "plugin.json", SearchOption.AllDirectories))
            {
                PluginDefinition plugin = JsonStore.Read<PluginDefinition>(path);
                if (plugin == null) throw new InvalidDataException("Empty plugin manifest: " + path);
                plugin.DirectoryPath = Path.GetDirectoryName(path);
                ValidatePlugin(plugin, path);
                if (result.ContainsKey(plugin.Id)) throw new InvalidDataException("Duplicate plugin id: " + plugin.Id);
                result.Add(plugin.Id, plugin);
            }
            if (result.Count == 0) throw new InvalidDataException("No plugin manifests were found.");
            return new PluginCatalog(result);
        }

        private static void ValidatePlugin(PluginDefinition plugin, string path)
        {
            if (plugin.SchemaVersion != 1) throw new InvalidDataException("Unsupported plugin schema in " + path);
            if (!IsSafeId(plugin.Id)) throw new InvalidDataException("Invalid plugin id in " + path);
            if (String.IsNullOrWhiteSpace(plugin.DisplayName)) throw new InvalidDataException("Missing plugin display name in " + path);
            if (plugin.DefaultPort < 1 || plugin.DefaultPort > 65535) throw new InvalidDataException("Invalid default port in " + path);
            if (plugin.Probe == null || String.IsNullOrWhiteSpace(plugin.Probe.UrlTemplate)) throw new InvalidDataException("Missing HTTP probe in " + path);
            if (plugin.Probe.Markers == null || plugin.Probe.Markers.Count == 0) throw new InvalidDataException("Missing HTTP markers in " + path);
            if (plugin.Runtimes == null || plugin.Runtimes.Count == 0) throw new InvalidDataException("No runtimes in " + path);
            HashSet<string> runtimeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RuntimeDefinition runtime in plugin.Runtimes)
            {
                if (!IsSafeId(runtime.Id)) throw new InvalidDataException("Invalid runtime id in " + path);
                if (!runtimeIds.Add(runtime.Id)) throw new InvalidDataException("Duplicate runtime id " + runtime.Id + " in " + path);
                if (runtime.CommandCandidates == null || runtime.CommandCandidates.Count == 0) throw new InvalidDataException("Runtime " + runtime.Id + " has no command candidates.");
            }
            if (plugin.ProcessPatterns != null)
            {
                foreach (string pattern in plugin.ProcessPatterns) new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            if (plugin.RuntimeBridge != null && plugin.RuntimeBridge.Enabled)
            {
                string modulePath = Path.GetFullPath(Path.Combine(plugin.DirectoryPath, plugin.RuntimeBridge.Module ?? String.Empty));
                if (!File.Exists(modulePath)) throw new FileNotFoundException("Runtime Bridge module not found.", modulePath);
            }
        }

        internal static bool IsSafeId(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        }
    }

    public sealed class ConfigurationStore
    {
        private readonly PluginCatalog catalog;
        private readonly object syncRoot = new object();

        public ConfigurationStore(PluginCatalog pluginCatalog)
        {
            catalog = pluginCatalog;
        }

        public ManagerConfig LoadOrCreate()
        {
            AppPaths.EnsureDirectories();
            ManagerConfig config;
            if (File.Exists(AppPaths.ConfigFile)) config = JsonStore.Read<ManagerConfig>(AppPaths.ConfigFile);
            else
            {
                config = CreateDefault();
                Save(config);
            }
            NormalizeAndValidate(config);
            return config;
        }

        public void Save(ManagerConfig config)
        {
            NormalizeAndValidate(config);
            lock (syncRoot) JsonStore.Write(AppPaths.ConfigFile, config);
        }

        public PersistedInstanceState ReadState(string instanceId)
        {
            string path = AppPaths.StateFile(instanceId);
            if (!File.Exists(path)) return null;
            try { return JsonStore.Read<PersistedInstanceState>(path); }
            catch (Exception exception)
            {
                FileLog.Warn("Ignoring unreadable state file " + path + ": " + exception.Message);
                return null;
            }
        }

        public void SaveState(string instanceId, PersistedInstanceState state)
        {
            lock (syncRoot) JsonStore.Write(AppPaths.StateFile(instanceId), state);
        }

        public void DeleteState(string instanceId)
        {
            string path = AppPaths.StateFile(instanceId);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) { FileLog.Warn("Could not delete state file: " + exception.Message); }
        }

        private ManagerConfig CreateDefault()
        {
            PluginDefinition plugin = catalog.All.First();
            string workspace = Environment.GetEnvironmentVariable("DSH_MANAGER_DEFAULT_WORKSPACE");
            if (String.IsNullOrWhiteSpace(workspace)) workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            InstanceConfig instance = new InstanceConfig();
            instance.Id = "web";
            instance.Name = plugin.DisplayName;
            instance.PluginId = plugin.Id;
            instance.Profile = "web";
            instance.Runtime = "auto";
            instance.RuntimeType = InstanceModel.RuntimeTypeWindows;
            instance.WslDistro = String.Empty;
            instance.Frontend = InstanceModel.FrontendWeb;
            instance.SourceRoot = String.Empty;
            instance.Workspace = workspace;
            instance.DshHome = String.Empty;
            instance.PreferredPort = plugin.DefaultPort;
            instance.PinnedVersion = plugin.Update == null ? String.Empty : plugin.Update.BundledVersion;
            ManagerConfig config = new ManagerConfig();
            config.SchemaVersion = 1;
            config.Language = "auto";
            config.TrayEnabled = true;
            config.StartWithWindows = false;
            config.DesktopShortcut = false;
            config.WslEnabled = false;
            config.WslDefaultDistro = String.Empty;
            config.DefaultInstanceId = instance.Id;
            config.Instances = new List<InstanceConfig>();
            config.Instances.Add(instance);
            return config;
        }

        private void NormalizeAndValidate(ManagerConfig config)
        {
            if (config == null) throw new InvalidDataException("Configuration is empty.");
            if (config.SchemaVersion == 0) config.SchemaVersion = 1;
            if (config.SchemaVersion != 1) throw new InvalidDataException("Unsupported configuration schema version.");
            if (String.IsNullOrWhiteSpace(config.Language)) config.Language = "auto";
            if (config.Language != "auto" && config.Language != "zh-CN" && config.Language != "en-US")
                throw new InvalidDataException("Unsupported language: " + config.Language);
            if (!config.TrayEnabled.HasValue) config.TrayEnabled = true;
            if (!config.StartWithWindows.HasValue) config.StartWithWindows = false;
            if (!config.DesktopShortcut.HasValue) config.DesktopShortcut = false;
            if (!config.WslEnabled.HasValue) config.WslEnabled = false;
            if (config.WslDefaultDistro == null) config.WslDefaultDistro = String.Empty;
            config.WslDefaultDistro = config.WslDefaultDistro.Trim();
            if (config.Instances == null || config.Instances.Count == 0) throw new InvalidDataException("At least one instance is required.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> preferredPorts = new HashSet<int>();
            foreach (InstanceConfig instance in config.Instances)
            {
                if (!PluginCatalog.IsSafeId(instance.Id)) throw new InvalidDataException("Invalid instance id: " + instance.Id);
                if (!ids.Add(instance.Id)) throw new InvalidDataException("Duplicate instance id: " + instance.Id);
                PluginDefinition plugin = catalog.Get(instance.PluginId);
                if (String.IsNullOrWhiteSpace(instance.Name)) instance.Name = plugin.DisplayName;
                if (!PluginCatalog.IsSafeId(instance.Profile)) throw new InvalidDataException("Invalid profile name for " + instance.Id);
                if (String.IsNullOrWhiteSpace(instance.Runtime)) instance.Runtime = "auto";
                bool runtimeExists = instance.Runtime.Equals("auto", StringComparison.OrdinalIgnoreCase) || plugin.Runtimes.Any(delegate(RuntimeDefinition item) { return item.Id.Equals(instance.Runtime, StringComparison.OrdinalIgnoreCase); });
                if (!runtimeExists) throw new InvalidDataException("Unknown runtime " + instance.Runtime + " for " + instance.Id);
                if (String.IsNullOrWhiteSpace(instance.RuntimeType)) instance.RuntimeType = InstanceModel.RuntimeTypeWindows;
                instance.RuntimeType = instance.RuntimeType.ToLowerInvariant();
                if (instance.RuntimeType != InstanceModel.RuntimeTypeWindows && instance.RuntimeType != InstanceModel.RuntimeTypeWsl)
                    throw new InvalidDataException("Unsupported runtime type " + instance.RuntimeType + " for " + instance.Id);
                if (instance.WslDistro == null) instance.WslDistro = String.Empty;
                instance.WslDistro = instance.WslDistro.Trim();
                if (instance.RuntimeType == InstanceModel.RuntimeTypeWsl)
                {
                    if (!config.WslEnabled.Value)
                        throw new InvalidDataException("WSL support is disabled. Run dsh-windows-manager wsl enable before using runtime type wsl for " + instance.Id + ".");
                    string effectiveDistro = String.IsNullOrWhiteSpace(instance.WslDistro) ? config.WslDefaultDistro : instance.WslDistro;
                    if (String.IsNullOrWhiteSpace(effectiveDistro))
                        throw new InvalidDataException("A WSL distro is required for instance " + instance.Id + ". Run dsh-windows-manager wsl enable --distro <name> or configure --wsl-distro <name>.");
                }
                if (String.IsNullOrWhiteSpace(instance.Frontend)) instance.Frontend = InstanceModel.FrontendWeb;
                instance.Frontend = instance.Frontend.ToLowerInvariant();
                if (instance.Frontend != InstanceModel.FrontendWeb && instance.Frontend != InstanceModel.FrontendOhDsh && instance.Frontend != InstanceModel.FrontendCustom)
                    throw new InvalidDataException("Unsupported frontend " + instance.Frontend + " for " + instance.Id);
                if (instance.PreferredPort == 0) instance.PreferredPort = plugin.DefaultPort;
                if (instance.PreferredPort < 1 || instance.PreferredPort > 65535) throw new InvalidDataException("Invalid port for " + instance.Id);
                if (!preferredPorts.Add(instance.PreferredPort)) throw new InvalidDataException("Multiple instances cannot use preferred port " + instance.PreferredPort + ".");
                if (String.IsNullOrWhiteSpace(instance.Workspace)) instance.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                instance.Workspace = Environment.ExpandEnvironmentVariables(instance.Workspace);
                if (instance.DshHome == null) instance.DshHome = String.Empty;
                instance.DshHome = Environment.ExpandEnvironmentVariables(instance.DshHome);
                if (instance.SourceRoot == null) instance.SourceRoot = String.Empty;
                instance.SourceRoot = Environment.ExpandEnvironmentVariables(instance.SourceRoot);
                if (String.IsNullOrWhiteSpace(instance.PinnedVersion) && plugin.Update != null) instance.PinnedVersion = plugin.Update.BundledVersion;
            }
            if (String.IsNullOrWhiteSpace(config.DefaultInstanceId)) config.DefaultInstanceId = config.Instances[0].Id;
            if (!ids.Contains(config.DefaultInstanceId)) throw new InvalidDataException("Default instance does not exist: " + config.DefaultInstanceId);
        }
    }
}
