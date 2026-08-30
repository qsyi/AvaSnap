using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU テクスチャをレンダー間でキャッシュする(各 Gpu* が毎回確保/破棄
/// するのを避ける。確保はデータ転送とは別にドライバのオーバーヘッドがある)。
/// 呼び出し側は <paramref name="key"/> 文字列(例 "Composite.A")で識別する。
/// アバター画像経路(PNG サイズ)と写真経路(通常もっと大)が同じスロットを
/// サイズ違いで取り合ってキャッシュを潰し合わないよう、key ごとに独立サイズの
/// スロットを持つ。
///
/// 借りたテクスチャはプロセス終了まで(または同 key へサイズ違い要求が来るまで)
/// 生きる。写真アンロード時の明示解放は無い ── 1 key につき常に 1 テクスチャなので、
/// 最悪でも key ごとに 1 枚ぶんの GPU メモリを終了まで保持するだけ(青天井のリークにはならない)。</summary>
public static class GpuTexturePool
{
    private sealed record Slot(int Width, int Height, ReadWriteTexture2D<Bgra32, float4> Texture, object? UploadedFrom = null);

    private static readonly Dictionary<string, Slot> Slots = new();

    /// <summary><paramref name="key"/> のキャッシュが要求サイズと同じならそれを返し、
    /// 違えば破棄して確保し直す。中身は前回の使用結果(初回は不定)なので、現在の
    /// ピクセルを反映させたい呼び出し側は借りたあと自分で CopyFrom すること。</summary>
    public static ReadWriteTexture2D<Bgra32, float4> Rent(GraphicsDevice device, string key, int width, int height)
    {
        if (Slots.TryGetValue(key, out var existing))
        {
            if (existing.Width == width && existing.Height == height)
            {
                return existing.Texture;
            }
            existing.Texture.Dispose();
        }

        var texture = device.AllocateReadWriteTexture2D<Bgra32, float4>(width, height);
        Slots[key] = new Slot(width, height, texture);
        return texture;
    }

    /// <summary><see cref="Rent"/> に加えて <paramref name="pixels"/> をテクスチャへ
    /// アップロードする。ただし同じ配列参照(内容一致ではなく参照一致)が既にこの key・
    /// このサイズでアップロード済みならスキップする。丸ごと差し替えしかしない
    /// (in-place で書き換えない)バッファ用 ── 例 _photoPixelBuffer.Pixels は
    /// TryLoadPhotoPixels でしか置き換わらないので「同じ参照 = 再アップロード不要」。
    /// 調整量だけ変えるスライダードラッグは毎回同じ配列で呼ぶので、CPU→GPU 転送は
    /// 写真読み込みごとに1回で済む。</summary>
    public static ReadWriteTexture2D<Bgra32, float4> RentUploaded(GraphicsDevice device, string key, byte[] pixels, int stride, int width, int height)
    {
        if (Slots.TryGetValue(key, out var existing) && existing.Width == width && existing.Height == height)
        {
            if (ReferenceEquals(existing.UploadedFrom, pixels))
            {
                return existing.Texture;
            }
            existing.Texture.CopyFrom(MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height)));
            Slots[key] = existing with { UploadedFrom = pixels };
            return existing.Texture;
        }

        if (existing is not null)
        {
            existing.Texture.Dispose();
        }

        var texture = device.AllocateReadWriteTexture2D<Bgra32, float4>(width, height);
        texture.CopyFrom(MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height)));
        Slots[key] = new Slot(width, height, texture, pixels);
        return texture;
    }
}
