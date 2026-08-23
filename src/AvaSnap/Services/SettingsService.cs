using System.IO;
using System.Text.Json;

namespace AvaSnap.Services;

public sealed class PersistedSettings
{
    public string? ImagePath { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double RotationDegrees { get; set; }
    public double Opacity { get; set; }
    public string? ScreenshotFolderPath { get; set; }
    public string? PhotoPath { get; set; }
    public List<string>? RecentAvatarPaths { get; set; }
    public bool IsDarkMode { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
}

/// <summary>Persists the overlay's last size/rotation/opacity/image path across
/// launches. Position (X/Y) is intentionally NOT persisted -- it's re-centered
/// on the VRChat window at every startup instead, since a screen coordinate
/// from a previous session is meaningless once the window has moved. Click-
/// through state is also not persisted -- it's a hold-to-toggle hotkey state
/// (Shift), not a mode the overlay should remember between launches.</summary>
public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static PersistedSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<PersistedSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(OverlayState state, string? screenshotFolderPath, string? photoPath, IReadOnlyList<string>? recentAvatarPaths = null, bool isDarkMode = false)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var data = new PersistedSettings
            {
                ImagePath = state.ImagePath,
                Width = state.Width,
                Height = state.Height,
                RotationDegrees = state.RotationDegrees,
                Opacity = state.Opacity,
                ScreenshotFolderPath = screenshotFolderPath,
                PhotoPath = photoPath,
                RecentAvatarPaths = recentAvatarPaths?.ToList(),
                IsDarkMode = isDarkMode,
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // best-effort; losing the last saved layout is not fatal
        }
    }

    /// <summary>Narrow read-modify-write of just LastUpdateCheckUtc --
    /// called from UpdateService's background check, independently of the
    /// full Save() above (which needs an OverlayState and only happens at
    /// exit). No concurrent-write race in practice: the two never run at
    /// the same time.</summary>
    public static void SaveLastUpdateCheck(DateTime utc)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var data = Load() ?? new PersistedSettings();
            data.LastUpdateCheckUtc = utc;
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // best-effort
        }
    }
}
