using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>CompositeOverlayOntoPhoto のアバター アルファブレンド(位置合わせ・
/// リサイズ済みアバター PNG を写真へオフセット付きで重ねる)の GPU 版。写真
/// テクスチャは毎回アップロードするが、オーバーレイテクスチャは GpuDropShadow と
/// 同じ "Overlay" key で RentUploaded する ── 1回の CompositeOverlayOntoPhoto 内で
/// 両者に同じ overlayPixels 参照が渡るので、先に走る方(DropShadow)がアップロードし、
/// こちらはスキップされる。</summary>
public static class GpuAvatarBlend
{
    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="photoPixels"/> は不変)。
    /// 呼び出し側は CPU の Parallel.For ブレンドへフォールバックする。</summary>
    public static bool TryBlend(
        byte[] photoPixels, int photoStride, int photoWidth, int photoHeight,
        byte[] overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        int overlayLeft, int overlayTop)
    {
        if (overlayWidth <= 0 || overlayHeight <= 0) return true;
        if (photoStride != photoWidth * 4 || photoPixels.Length < photoStride * photoHeight) return false;
        if (overlayStride != overlayWidth * 4 || overlayPixels.Length < overlayStride * overlayHeight) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> photoSpan = MemoryMarshal.Cast<byte, Bgra32>(photoPixels.AsSpan(0, photoStride * photoHeight));

            ReadWriteTexture2D<Bgra32, float4> photoTexture = GpuTexturePool.Rent(device, "Blend.Photo", photoWidth, photoHeight);
            photoTexture.CopyFrom(photoSpan);

            if (!BlendIntoTexture(photoTexture, device, photoWidth, photoHeight,
                overlayPixels, overlayStride, overlayWidth, overlayHeight, overlayLeft, overlayTop))
            {
                return false;
            }

            photoTexture.CopyTo(photoSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><see cref="TryBlend"/> と同じ処理を、既に GPU 上にある
    /// <paramref name="photoTexture"/> へ直接書く(写真のアップロード/ダウンロード無し)。
    /// GpuCompositeChain から使う。</summary>
    internal static bool BlendIntoTexture(
        ReadWriteTexture2D<Bgra32, float4> photoTexture, GraphicsDevice device, int photoWidth, int photoHeight,
        byte[] overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        int overlayLeft, int overlayTop)
    {
        if (overlayWidth <= 0 || overlayHeight <= 0) return true;
        if (overlayStride != overlayWidth * 4 || overlayPixels.Length < overlayStride * overlayHeight) return false;

        ReadWriteTexture2D<Bgra32, float4> overlayTexture = GpuTexturePool.RentUploaded(device, "Overlay", overlayPixels, overlayStride, overlayWidth, overlayHeight);

        // ディスパッチはオーバーレイの寸法(通常は写真より小)。アバターが覆う
        // ピクセルだけ触れば良いので、写真全体を回す必要はない。
        device.For(overlayWidth, overlayHeight, new AlphaBlendShader(photoTexture, overlayTexture, photoWidth, photoHeight, overlayLeft, overlayTop));

        return true;
    }
}

/// <summary>CompositeOverlayOntoPhoto のアルファブレンドの再現。アルファ非0の
/// オーバーレイ各画素を写真の (overlayLeft+x, overlayTop+y) へブレンドし、写真の
/// 範囲外は捨てる。ディスパッチはオーバーレイ寸法で、書き込み位置はスレッドの
/// ThreadIds とはオフセットぶんずれるが、各スレッドが別々のオーバーレイ画素
/// = 別々の写真画素を持つので、近傍参照シェーダ(ぼかし等)のような競合は無い。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AlphaBlendShader(
    IReadWriteNormalizedTexture2D<float4> photo,
    IReadWriteNormalizedTexture2D<float4> overlay,
    int photoWidth, int photoHeight,
    int overlayLeft, int overlayTop) : IComputeShader
{
    public void Execute()
    {
        int ox = ThreadIds.X, oy = ThreadIds.Y;
        int px = overlayLeft + ox;
        int py = overlayTop + oy;
        if (px < 0 || px >= photoWidth || py < 0 || py >= photoHeight)
        {
            return;
        }

        float4 ov = overlay[new int2(ox, oy)];
        if (ov.A <= 0f)
        {
            return;
        }

        float4 ph = photo[new int2(px, py)];
        float alpha = ov.A;
        float r = ov.R * alpha + ph.R * (1f - alpha);
        float g = ov.G * alpha + ph.G * (1f - alpha);
        float b = ov.B * alpha + ph.B * (1f - alpha);
        photo[new int2(px, py)] = new float4(r, g, b, ph.A);
    }
}
