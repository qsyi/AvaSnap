using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>ApplyDropShadow の GPU 版。CPU 版と同じ段取り: オーバーレイのアルファを
/// 取り出す → 任意でボックスブラー → 任意でハーフトーンのドット化 → オフセット付きで
/// 写真へブレンド(DropShadowBlendMode: multiply/normal/additive)。アルファブラーは
/// 専用シェーダを書かず、アルファ値を R/G/B 全部に詰めて BoxBlurPassShader を流用する
/// (シルエット縁の挙動が BoxBlurAlpha と厳密一致しないが、見た目だけの差で許容)。
///
/// GpuAvatarBlend と同じ "Overlay" key を使う ── 1回の CompositeOverlayOntoPhoto 内で
/// 両者に同じ overlayPixels 参照が渡るので、先に走る方がアップロードし、もう一方の
/// RentUploaded は no-op。</summary>
public static class GpuDropShadow
{
    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="photoPixels"/> は不変)。
    /// 呼び出し側は CPU の ApplyDropShadow へフォールバックする。</summary>
    public static bool TryApply(
        byte[] photoPixels, int photoStride, int photoWidth, int photoHeight,
        byte[] overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        int overlayLeft, int overlayTop,
        double amount, double directionDegrees, double distance, double blurRadius,
        byte colorB, byte colorG, byte colorR, double scale,
        bool tone, double dotSize, int blendMode)
    {
        if (amount <= 0 || overlayWidth <= 0 || overlayHeight <= 0) return true;
        if (photoStride != photoWidth * 4 || photoPixels.Length < photoStride * photoHeight) return false;
        if (overlayStride != overlayWidth * 4 || overlayPixels.Length < overlayStride * overlayHeight) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> photoSpan = MemoryMarshal.Cast<byte, Bgra32>(photoPixels.AsSpan(0, photoStride * photoHeight));

            ReadWriteTexture2D<Bgra32, float4> photoTexture = GpuTexturePool.Rent(device, "Blend.Photo", photoWidth, photoHeight);
            photoTexture.CopyFrom(photoSpan);

            if (!ApplyToTexture(photoTexture, device, photoWidth, photoHeight,
                overlayPixels, overlayStride, overlayWidth, overlayHeight, overlayLeft, overlayTop,
                amount, directionDegrees, distance, blurRadius, colorB, colorG, colorR, scale, tone, dotSize, blendMode))
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

    /// <summary><see cref="TryApply"/> と同じ処理を、既に GPU 上にある
    /// <paramref name="photoTexture"/> へ直接描く(写真のアップロード/ダウンロード無し。
    /// オーバーレイのアルファは小さい別バッファなので従来どおり借りる)。
    /// GpuCompositeChain から使う。</summary>
    internal static bool ApplyToTexture(
        ReadWriteTexture2D<Bgra32, float4> photoTexture, GraphicsDevice device, int photoWidth, int photoHeight,
        byte[] overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        int overlayLeft, int overlayTop,
        double amount, double directionDegrees, double distance, double blurRadius,
        byte colorB, byte colorG, byte colorR, double scale,
        bool tone, double dotSize, int blendMode)
    {
        if (amount <= 0 || overlayWidth <= 0 || overlayHeight <= 0) return true;
        if (overlayStride != overlayWidth * 4 || overlayPixels.Length < overlayStride * overlayHeight) return false;

        double rad = directionDegrees * Math.PI / 180.0;
        double scaledDistance = distance * scale;
        double scaledBlur = blurRadius * scale;
        int shadowLeft = overlayLeft + (int)Math.Round(-Math.Sin(rad) * scaledDistance);
        int shadowTop = overlayTop + (int)Math.Round(Math.Cos(rad) * scaledDistance);
        float strength = (float)(amount / 100.0);

        ReadWriteTexture2D<Bgra32, float4> overlayTexture = GpuTexturePool.RentUploaded(device, "Overlay", overlayPixels, overlayStride, overlayWidth, overlayHeight);

        ReadWriteTexture2D<Bgra32, float4> alphaA = GpuTexturePool.Rent(device, "DropShadow.AlphaA", overlayWidth, overlayHeight);
        device.For(overlayWidth, overlayHeight, new ExtractAlphaShader(overlayTexture, alphaA));

        if (scaledBlur > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(scaledBlur));
            ReadWriteTexture2D<Bgra32, float4> alphaB = GpuTexturePool.Rent(device, "DropShadow.AlphaB", overlayWidth, overlayHeight);
            device.For(overlayWidth, overlayHeight, new BoxBlurPassShader(alphaA, alphaB, overlayWidth, overlayHeight, radius, horizontal: true));
            device.For(overlayWidth, overlayHeight, new BoxBlurPassShader(alphaB, alphaA, overlayWidth, overlayHeight, radius, horizontal: false));
        }

        if (tone)
        {
            int cell = Math.Max(2, (int)Math.Round(Math.Max(2, dotSize * scale)));
            device.For(overlayWidth, overlayHeight, new HalftoneDotsShader(alphaA, cell));
        }

        device.For(overlayWidth, overlayHeight, new DropShadowBlendShader(
            photoTexture, alphaA, photoWidth, photoHeight, shadowLeft, shadowTop, strength, colorB, colorG, colorR, blendMode));

        return true;
    }
}

/// <summary><paramref name="overlay"/> のアルファを <paramref name="destination"/> の
/// R/G/B 全部へコピーする。既存の BoxBlurPassShader(RGB ブラー)をグレースケール
/// 画像のように流用するため。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ExtractAlphaShader(
    IReadWriteNormalizedTexture2D<float4> overlay,
    IReadWriteNormalizedTexture2D<float4> destination) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float a = overlay[pos].A;
        destination[pos] = new float4(a, a, a, 1f);
    }
}

/// <summary>ApplyHalftoneDots の再現(形状の理由は ImageAdjustment.cs 側の doc)。
/// per-pixel・近傍非依存なので in-place で安全。ExtractAlphaShader が R/G/B に詰めた
/// アルファ値を読み書きする。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct HalftoneDotsShader(
    IReadWriteNormalizedTexture2D<float4> texture, int cell) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float half = cell / 2f;
        const float concavity = 2f / 3f;
        const float halftoneReferenceHalf = 14f;

        int cellX = (pos.X / cell) * cell;
        int cellY = (pos.Y / cell) * cell;
        float centerX = cellX + half;
        float centerY = cellY + half;
        float uTerm = Hlsl.Pow(Hlsl.Abs(pos.X + 0.5f - centerX) / half, concavity);
        float vTerm = Hlsl.Pow(Hlsl.Abs(pos.Y + 0.5f - centerY) / half, concavity);
        float edge = (1f - (uTerm + vTerm)) * halftoneReferenceHalf;
        float shape = Hlsl.Saturate(edge + 0.5f);

        float src = Hlsl.Saturate(texture[pos].R);
        float result = shape * src;
        texture[pos] = new float4(result, result, result, 1f);
    }
}

/// <summary>ApplyDropShadow の最終ブレンドの再現。写真画素を影色(colorB/G/R)と
/// <paramref name="blendMode"/>(0=Multiply / 1=Normal / 2=Additive。
/// DropShadowBlendMode の int 値。HLSL は enum 不可なので呼び出し側が int で渡す)で
/// 合成し、<paramref name="alphaTexture"/> の(ブラー/ハーフトーン済み)アルファで
/// 重み付けして写真のオフセット位置へ書く。ディスパッチはオーバーレイ寸法
/// (AlphaBlendShader と同じく競合なし)。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct DropShadowBlendShader(
    IReadWriteNormalizedTexture2D<float4> photo,
    IReadWriteNormalizedTexture2D<float4> alphaTexture,
    int photoWidth, int photoHeight,
    int shadowLeft, int shadowTop,
    float strength, float colorB, float colorG, float colorR, int blendMode) : IComputeShader
{
    public void Execute()
    {
        int ox = ThreadIds.X, oy = ThreadIds.Y;
        int px = shadowLeft + ox;
        int py = shadowTop + oy;
        if (px < 0 || px >= photoWidth || py < 0 || py >= photoHeight)
        {
            return;
        }

        float a = alphaTexture[new int2(ox, oy)].R * strength;
        if (a <= 0f)
        {
            return;
        }

        float4 ph = photo[new int2(px, py)];
        float b = ph.B * 255f, g = ph.G * 255f, r = ph.R * 255f;
        if (blendMode == 2) // 加算
        {
            b += colorB * a;
            g += colorG * a;
            r += colorR * a;
        }
        else if (blendMode == 1) // 通常
        {
            b = b * (1f - a) + colorB * a;
            g = g * (1f - a) + colorG * a;
            r = r * (1f - a) + colorR * a;
        }
        else // 乗算
        {
            b = b * (1f - a) + b * colorB / 255f * a;
            g = g * (1f - a) + g * colorG / 255f * a;
            r = r * (1f - a) + r * colorR / 255f * a;
        }
        photo[new int2(px, py)] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), ph.A);
    }
}
