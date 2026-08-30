using System.Windows;

namespace AvaSnap.Services;

/// <summary>アプリの配色(Themes/LightTheme.xaml か DarkTheme.xaml)を実行時に
/// Application.Resources へ差し替える。これらのブラシは全て DynamicResource で
/// 参照されているので、差し替えは表示中の画面に即反映される(再読み込み不要)。</summary>
public static class ThemeService
{
    public static bool IsDarkMode { get; private set; }

    private static ResourceDictionary? _current;

    public static void Apply(bool dark)
    {
        IsDarkMode = dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}Theme.xaml", UriKind.Relative),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }
        merged.Add(dict);
        _current = dict;
    }
}
