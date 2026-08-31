using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvaSnap.Views;

namespace AvaSnap.Services;

/// <summary>レタッチモードの作業状態を1つの JSON プロジェクトファイルへ書き出す。
/// 画像は「パス参照」のみ(同梱しない)。パラメータ側は <see cref="CompositeSnapshot"/>
/// のサブレコードをそのまま使うので、ほとんどのフィールドは追加のマッピング不要。
/// 画像を含む <c>DecalEntrySnapshot.Pixels</c> / <c>Thumbnail</c> と写真バッファは
/// 保存せず、読み込み時にパス(枠線デカールはパラメータ)から作り直す。</summary>
public sealed class ProjectDto
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime SavedUtc { get; set; }

    // ---- 背景 ----
    public string? PhotoPath { get; set; }
    /// <summary>アプリ内 90° 回転ボタンを押した回数(mod 4)。読み込み時に同数だけ回す。</summary>
    public int PhotoRotationQuarters { get; set; }
    public bool SkipAvatar { get; set; }
    public CompositeBlankCanvas? BlankCanvas { get; set; }

    // ---- アバター ----
    public string? AvatarPath { get; set; }
    public AvatarLookDto? AvatarLook { get; set; }

    // ---- レタッチ本体(CompositeSnapshot のサブレコードをそのまま) ----
    public CompositePhotoLook? PhotoLook { get; set; }
    public CompositeFinish? Finish { get; set; }
    public CompositeDropShadow? DropShadow { get; set; }
    public CompositeCanvasCrop? CanvasCrop { get; set; }
    public CompositePlacement? Placement { get; set; }
    public CompositeMasks? Masks { get; set; }
    public List<DecalDto> Decals { get; set; } = new();

    // ---- 保存設定 ----
    public int SplitCount { get; set; } = 1;
    public int SplitGapPx { get; set; }

    /// <summary>ホーム一覧用の小さなプレビュー(JPEG を Base64 で埋め込み)。
    /// サイドカーファイルを作らず .avasnap 単体で持ち運べるようにするため。</summary>
    public string? PreviewJpegBase64 { get; set; }
}

/// <summary>一覧表示だけに必要な部分を取り出す軽量 DTO(全 DTO を読まずに済ませる)。</summary>
internal sealed class ProjectSummaryDto
{
    public DateTime SavedUtc { get; set; }
    public string? PreviewJpegBase64 { get; set; }
}

/// <summary>アバター画像のルック(<see cref="OverlayState"/> の該当フィールド)。
/// 位置/サイズは VRChat 相対で揮発的なのでプロジェクトには含めない(回転のみ)。</summary>
public sealed record AvatarLookDto(
    double EdgeBlurRadius,
    double Brightness, double Contrast, double Saturation, double Vibrance,
    double Temperature, double Tint, double Hue,
    double Highlights, double Shadows, double Whites, double Blacks,
    double ColorTintStrength, byte ColorTintR, byte ColorTintG, byte ColorTintB,
    double RotationDegrees);

/// <summary><see cref="DecalEntrySnapshot"/> の直列化用。画像デカールは
/// <see cref="SourcePath"/> から、枠線デカールはパラメータから作り直す。</summary>
public sealed record DecalDto(
    bool IsAvatarMarker,
    string? SourcePath,
    double X, double Y, double Width, double Height, double Rotation,
    bool IsFrame, byte ColorR, byte ColorG, byte ColorB, double StrokePercent,
    double Opacity);

/// <summary>一覧表示用のプロジェクトの要約(パス・名前・更新時刻・プレビュー JPEG バイト列)。</summary>
public sealed record ProjectInfo(string Path, string Name, DateTime ModifiedUtc, byte[]? PreviewJpeg);

public static class ProjectService
{
    public static readonly string ProjectsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "Projects");

    public const string Extension = ".avasnap";

    /// <summary>Projects フォルダ内の .avasnap を保存日時の新しい順に列挙する
    /// (プレビューは各ファイルから軽量パースで取り出す)。</summary>
    public static IReadOnlyList<ProjectInfo> ListProjects()
    {
        try
        {
            if (!Directory.Exists(ProjectsDir)) return Array.Empty<ProjectInfo>();
            var list = new List<ProjectInfo>();
            foreach (var p in Directory.EnumerateFiles(ProjectsDir, "*" + Extension))
            {
                DateTime saved = File.GetLastWriteTimeUtc(p);
                byte[]? preview = null;
                try
                {
                    var sum = JsonSerializer.Deserialize<ProjectSummaryDto>(File.ReadAllText(p), Options);
                    if (sum is not null)
                    {
                        if (sum.SavedUtc != default) saved = sum.SavedUtc;
                        if (!string.IsNullOrEmpty(sum.PreviewJpegBase64))
                        {
                            try { preview = Convert.FromBase64String(sum.PreviewJpegBase64); }
                            catch (FormatException) { }
                        }
                    }
                }
                catch (JsonException) { }
                catch (IOException) { }
                list.Add(new ProjectInfo(p, Path.GetFileNameWithoutExtension(p), saved, preview));
            }
            return list.OrderByDescending(i => i.ModifiedUtc).ToList();
        }
        catch (IOException) { return Array.Empty<ProjectInfo>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<ProjectInfo>(); }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new EquatableArrayJsonConverterFactory(),
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>まだ存在しない、日時ベースのプロジェクトファイルパスを返す(作成はしない)。</summary>
    public static string NewProjectPath()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(ProjectsDir, $"プロジェクト {stamp}{Extension}");
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(ProjectsDir, $"プロジェクト {stamp} ({n++}){Extension}");
        }
        return path;
    }

    public static void Save(ProjectDto dto, string path)
    {
        try
        {
            dto.SavedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Options));
            File.Move(tmp, path, overwrite: true); // 書き込み途中で壊れないよう一旦 .tmp
        }
        catch
        {
            // ベストエフォート。保存に失敗しても編集は続行できる。
        }
    }

    public static ProjectDto? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ProjectDto>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }
}
