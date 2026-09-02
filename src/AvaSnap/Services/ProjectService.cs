using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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

    /// <summary>Projects フォルダ内の .avasnap を新しい順に最大 <paramref name="max"/> 件。
    /// まず安価な更新時刻で並べて上位だけを開く(全件フルパースしない)。1つのファイルが
    /// 一時的に読めなくても(保存直後のロック・AV スキャン等)そのファイルは最小情報の
    /// まま残す ── 一覧が丸ごと消えないように。</summary>
    public static IReadOnlyList<ProjectInfo> ListProjects(int max = 10)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(ProjectsDir)) return Array.Empty<ProjectInfo>();
            files = Directory.GetFiles(ProjectsDir, "*" + Extension);
        }
        catch (IOException) { return Array.Empty<ProjectInfo>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<ProjectInfo>(); }

        var recent = files
            .Select(p => (Path: p, Mtime: SafeMtime(p)))
            .OrderByDescending(t => t.Mtime)
            .Take(Math.Max(0, max))
            .ToList();

        var list = new List<ProjectInfo>(recent.Count);
        foreach (var (p, mtime) in recent)
        {
            DateTime saved = mtime;
            byte[]? preview = null;
            if (TryReadAllText(p) is { } text)
            {
                try
                {
                    if (JsonSerializer.Deserialize<ProjectSummaryDto>(text, Options) is { } sum)
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
            }
            list.Add(new ProjectInfo(p, Path.GetFileNameWithoutExtension(p), saved, preview));
        }
        return list.OrderByDescending(i => i.ModifiedUtc).ToList();
    }

    private static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary><paramref name="days"/> 日以上更新のない .avasnap をゴミ箱へ移す(元に戻せる)。
    /// 起動時に1回だけ呼ぶ想定。中身は開かず更新時刻だけで判定する。</summary>
    public static void PruneOlderThan(int days)
    {
        try
        {
            if (!Directory.Exists(ProjectsDir)) return;
            DateTime cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var p in Directory.GetFiles(ProjectsDir, "*" + Extension))
            {
                if (SafeMtime(p) < cutoff)
                {
                    try { RecycleFile(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>書き込み中断(クラッシュ等)で取り残された <c>.avasnap.tmp</c> を削除する。
    /// 起動時に1回。フォルダを .avasnap だけに保つため。</summary>
    public static void SweepOrphanTempFiles()
    {
        try
        {
            if (!Directory.Exists(ProjectsDir)) return;
            foreach (var p in Directory.GetFiles(ProjectsDir, "*" + Extension + ".tmp"))
            {
                try { File.Delete(p); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>共有違反(保存直後の一時ロック・AV スキャン等)で失敗しても数回だけ
    /// 短く待って読み直す。ファイルが無ければ <c>null</c>。</summary>
    private static string? TryReadAllText(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(30); }
            catch (UnauthorizedAccessException) { Thread.Sleep(30); }
        }
        return null;
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
        var tmp = path + ".tmp";
        try
        {
            dto.SavedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Options));
            // 書き込み途中の破損を避けて一旦 .tmp。差し替えは共有違反で失敗しうるので
            // 数回だけ待って再試行(MoveFileEx は失敗しても元ファイルを壊さない)。
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(40); }
            }
        }
        catch
        {
            // ベストエフォート。保存に失敗しても編集は続行できる。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } // .avasnap 以外を残さない
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

    /// <summary>プロジェクトファイルをゴミ箱へ送る(完全削除ではないので元に戻せる)。
    /// 取り残しの <c>.tmp</c> があれば一緒に片付ける。</summary>
    public static void Delete(string path)
    {
        try
        {
            string tmp = path + ".tmp";
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            if (File.Exists(path)) RecycleFile(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private static void RecycleFile(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // pFrom はダブル null 終端
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        SHFileOperation(ref op);
    }
}
