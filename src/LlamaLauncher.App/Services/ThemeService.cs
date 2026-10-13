using System.Windows;

namespace LlamaLauncher.App.Services;

/// <summary>主题切换：替换 App 资源字典中的主题字典。</summary>
public sealed class ThemeService : IThemeService
{
    private const string DarkDictionary = "Themes/Theme.Dark.xaml";
    private const string LightDictionary = "Themes/Theme.Light.xaml";

    public string CurrentTheme { get; private set; } = "dark";

    public void Apply(string theme)
    {
        var normalized = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            CurrentTheme = normalized;
            return;
        }

        var targetSource = new Uri(normalized == "light" ? LightDictionary : DarkDictionary, UriKind.Relative);
        var dictionaries = application.Resources.MergedDictionaries;

        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var source = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary { Source = targetSource });
        CurrentTheme = normalized;
    }
}
