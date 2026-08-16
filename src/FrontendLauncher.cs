using System;

namespace DeepSeekHarnessManager
{
    public static class FrontendLauncher
    {
        public static bool TryResolve(InstanceConfig instance, PluginDefinition plugin, int port, out string url, out string error)
        {
            url = String.Empty;
            error = String.Empty;
            string frontend = instance == null || String.IsNullOrWhiteSpace(instance.Frontend)
                ? InstanceModel.FrontendWeb
                : instance.Frontend.Trim().ToLowerInvariant();

            if (frontend == InstanceModel.FrontendWeb)
            {
                url = BuildWebUrl(instance, plugin, port);
                return true;
            }
            if (frontend == InstanceModel.FrontendOhDsh)
            {
                error = Localization.Format("Frontend.NotConfigured", InstanceModel.FrontendOhDsh);
                return false;
            }
            if (frontend == InstanceModel.FrontendCustom)
            {
                error = Localization.Format("Frontend.NotConfigured", InstanceModel.FrontendCustom);
                return false;
            }
            error = Localization.Format("Frontend.Unsupported", frontend);
            return false;
        }

        public static string BuildWebUrl(InstanceConfig instance, PluginDefinition plugin, int port)
        {
            if (plugin == null || plugin.Probe == null || String.IsNullOrWhiteSpace(plugin.Probe.UrlTemplate))
                throw new InvalidOperationException("The configured plugin has no Web URL template.");
            TokenContext context = RuntimeResolver.CreateContext(instance, plugin, port, String.Empty);
            return AppPaths.Expand(plugin.Probe.UrlTemplate, context);
        }
    }
}