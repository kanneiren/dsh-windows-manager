using System;
using System.IO;
using System.Net;
using System.Text;

namespace DeepSeekHarnessManager
{
    public static class RuntimeHttpProbe
    {
        public static bool Verify(InstanceConfig instance, PluginDefinition plugin, int port, int timeoutMilliseconds)
        {
            try
            {
                if (plugin == null || plugin.Probe == null || String.IsNullOrWhiteSpace(plugin.Probe.UrlTemplate)) return false;
                InstanceConfig probeInstance = instance ?? new InstanceConfig();
                if (String.IsNullOrWhiteSpace(probeInstance.Profile)) probeInstance.Profile = "web";
                TokenContext context = RuntimeResolver.CreateContext(probeInstance, plugin, port, String.Empty);
                string url = AppPaths.Expand(plugin.Probe.UrlTemplate, context);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = timeoutMilliseconds;
                request.ReadWriteTimeout = timeoutMilliseconds;
                request.Method = "GET";
                request.UserAgent = "DeepSeekHarnessManager/1.0";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    if ((int)response.StatusCode != 200) return false;
                    string content = reader.ReadToEnd();
                    foreach (string marker in plugin.Probe.Markers)
                        if (content.IndexOf(marker, StringComparison.Ordinal) < 0) return false;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
