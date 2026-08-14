using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepSeekHarnessManager
{
    public static class Localization
    {
        private static Dictionary<string, string> fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string CurrentLanguage { get; private set; }
        public static string ConfiguredLanguage { get; private set; }

        public static void Initialize(string configuredLanguage)
        {
            ConfiguredLanguage = String.IsNullOrWhiteSpace(configuredLanguage) ? "auto" : configuredLanguage;
            string selected = ConfiguredLanguage;
            if (selected == "auto") selected = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
            fallback = Load("en-US");
            current = selected == "en-US" ? fallback : Load(selected);
            CurrentLanguage = selected;
        }

        public static string Text(string key)
        {
            string value;
            if (current.TryGetValue(key, out value)) return value;
            if (fallback.TryGetValue(key, out value)) return value;
            return key;
        }

        public static string Format(string key, params object[] arguments)
        {
            return String.Format(CultureInfo.CurrentCulture, Text(key), arguments);
        }

        private static Dictionary<string, string> Load(string language)
        {
            string path = Path.Combine(AppPaths.LocaleDirectory, language + ".json");
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> values = JsonStore.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
            return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }
}
