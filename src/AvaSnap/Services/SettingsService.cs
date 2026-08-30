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

/// <summary>オーバーレイの前回のサイズ/回転/不透明度/画像パスを起動間で保存する。
/// 位置(X/Y)は保存しない ── 起動ごとに VRChat ウィンドウへ再センタリングする
/// (前回セッションの画面座標は意味を持たないため)。クリックスルー状態も保存しない
/// (Shift 押しっぱなしのホットキー状態であってモードではない)。</summary>
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
            // ベストエフォート。前回レイアウトを失っても致命的ではない
        }
    }

    /// <summary>LastUpdateCheckUtc だけの read-modify-write。UpdateService の
    /// バックグラウンドチェックから、終了時にしか走らない full Save() とは独立に呼ぶ。
    /// 両者は同時に走らないので書き込み競合は無い。</summary>
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
            // ベストエフォート
        }
    }
}
