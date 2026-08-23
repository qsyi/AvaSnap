using System.Windows;

namespace AvaSnap.Services;

/// <summary>Swaps the app's color palette (Themes/LightTheme.xaml or
/// Themes/DarkTheme.xaml) into Application.Resources at runtime. Every
/// brush these dictionaries define is consumed via DynamicResource (not
/// StaticResource) in ControlPanelWindow.xaml, so a swap here is picked up
/// immediately by whatever's already on screen -- no window reload
/// needed.</summary>
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
