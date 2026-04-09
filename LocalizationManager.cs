using System.Windows;

namespace ImageBrowser
{
    public static class LocalizationManager
    {
        public static string CurrentLanguage { get; private set; } = "zh-CN";

        public static void Apply(string languageCode)
        {
            CurrentLanguage = languageCode;
            var uri = new Uri(
                $"pack://application:,,,/Resources/Strings.{languageCode}.xaml",
                UriKind.Absolute);
            var dict = new ResourceDictionary { Source = uri };

            // 替换已有语言字典
            var existing = Application.Current.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("Strings.") == true);
            if (existing != null)
                Application.Current.Resources.MergedDictionaries.Remove(existing);

            Application.Current.Resources.MergedDictionaries.Add(dict);
        }

        /// <summary>从资源字典获取本地化字符串，找不到则返回 key。</summary>
        public static string Get(string key) =>
            Application.Current.TryFindResource(key) as string ?? key;
    }
}
