using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AvaSnap.Services;

/// <summary>被写界深度(深度依存ぼかし)で使う Depth Anything V2 Small(fp16 ONNX、
/// Apache-2.0)のダウンロード/キャッシュ/検証。~47MB あるのでアプリには同梱せず、
/// 初回使用時に Hugging Face から取得して <see cref="ModelPath"/> に置く。
/// 破損/途中終了に備えて .part → SHA256 検証 → リネームの順。</summary>
public static class DepthModel
{
    public const string DownloadUrl =
        "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model_fp16.onnx";

    /// <summary>onnx-community/depth-anything-v2-small の onnx/model_fp16.onnx の
    /// Git LFS oid(2026-09 時点)。ダウンロード後に必ず突き合わせる。</summary>
    public const string Sha256Hex = "2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04";

    public const long ExpectedSize = 49642442;

    public static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "models");

    public static string ModelPath => Path.Combine(ModelDir, "depth-anything-v2-small-fp16.onnx");

    /// <summary>検証済みのモデルが手元にあるか(サイズだけの安価な判定。ハッシュ照合は
    /// ダウンロード直後に一度だけ行う)。</summary>
    public static bool IsAvailable()
    {
        try { return File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedSize; }
        catch (IOException) { return false; }
    }

    /// <summary>モデルを取得して <see cref="ModelPath"/> へ置く。既に有れば即 true。
    /// <paramref name="progress"/> は 0..1。SHA256 不一致なら false(部分ファイルは消す)。</summary>
    public static async Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (IsAvailable()) return true;

        Directory.CreateDirectory(ModelDir);
        string part = ModelPath + ".part";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? ExpectedSize;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(total > 0 ? Math.Clamp(done / (double)total, 0, 1) : 0);
                }
            }

            if (!HashMatches(part))
            {
                TryDelete(part);
                return false;
            }

            File.Move(part, ModelPath, overwrite: true);
            progress?.Report(1);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        {
            TryDelete(part);
            return false;
        }
    }

    private static bool HashMatches(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexStringLower(hash) == Sha256Hex;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
