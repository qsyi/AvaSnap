using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for CompositeOverlayOntoPhoto's ApplyDropShadow --
/// see GPU_MIGRATION_PLAN.md's "残作業2" item 2. Mirrors the CPU pipeline
/// stage for stage: extract the overlay's alpha channel, optionally box-
/// blur it, optionally re-quantize it into a halftone dot pattern, then
/// blend it onto the photo at an offset (see
/// ImageAdjustment.DropShadowBlendMode -- multiply/normal/additive). The
/// alpha-blur step
/// reuses GpuCompositePipeline's own BoxBlurPassShader by packing the
/// single alpha value into all of R/G/B (that shader blurs R/G/B alike,
/// clamp-to-edge) rather than writing a dedicated single-channel blur
/// shader -- CPU parity isn't a goal here (see GPU_MIGRATION_PLAN.md), so
/// the edge behavior not matching BoxBlurAlpha's own shrinking-average
/// edges exactly is an accepted, purely cosmetic difference right at the
/// silhouette's own border.
///
/// Uses the SAME "Overlay" GpuTexturePool key as GpuAvatarBlend: within one
/// CompositeOverlayOntoPhoto call, ApplyDropShadow and the avatar blend
/// both receive the identical overlayPixels array reference, so whichever
/// runs first (DropShadow, per the CPU call order) uploads it and the
/// other's RentUploaded call for the same key/reference is a no-op.</summary>
public static class GpuDropShadow
{
    /// <summary>Returns false (leaving <paramref name="photoPixels"/>
    /// untouched) if no DX12-capable GPU/driver is available, so the caller
    /// falls back to its own CPU ApplyDropShadow instead.</summary>
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

    /// <summary>Same drop-shadow pass as <see cref="TryApply"/>, but drawing
    /// directly onto an already GPU-resident <paramref name="photoTexture"/>
    /// -- no upload/download of the photo itself (the overlay's own alpha
    /// still gets uploaded/rented as usual, since it's a small, separate
    /// buffer). Used by GpuCompositeChain -- see its own doc comment.</summary>
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

/// <summary>Copies <paramref name="overlay"/>'s alpha channel into all of
/// R/G/B of <paramref name="destination"/>, so the existing (RGB-blurring)
/// BoxBlurPassShader can blur it as if it were a grayscale image, without
/// needing a dedicated single-channel blur shader.</summary>
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

/// <summary>Mirrors ApplyHalftoneDots exactly (see its own doc comment in
/// ImageAdjustment.cs for the shape rationale) -- a per-pixel, no-neighbor-
/// dependency formula, so it's safe to run in place. Reads/writes the alpha
/// value packed into R/G/B by ExtractAlphaShader (or left there by the
/// preceding blur pass).</summary>
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

/// <summary>Mirrors ApplyDropShadow's own final blend loop: combines the
/// photo pixel with the shadow color (colorB/colorG/colorR) per
/// <paramref name="blendMode"/> (0=Multiply, 1=Normal, 2=Additive -- matches
/// ImageAdjustment.DropShadowBlendMode's own int values; HLSL shader fields
/// can't be C# enums, so the caller passes the int cast), weighted by the
/// (blurred/halftoned) alpha at <paramref name="alphaTexture"/>'s own
/// position, writing into the photo at an offset position. Dispatched over
/// the overlay's dimensions -- same no-cross-thread-hazard reasoning as
/// AlphaBlendShader, since each thread owns a distinct alpha-texture pixel
/// and therefore a distinct photo pixel.</summary>
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
        if (blendMode == 2) // Additive
        {
            b += colorB * a;
            g += colorG * a;
            r += colorR * a;
        }
        else if (blendMode == 1) // Normal
        {
            b = b * (1f - a) + colorB * a;
            g = g * (1f - a) + colorG * a;
            r = r * (1f - a) + colorR * a;
        }
        else // Multiply
        {
            b = b * (1f - a) + b * colorB / 255f * a;
            g = g * (1f - a) + g * colorG / 255f * a;
            r = r * (1f - a) + r * colorR / 255f * a;
        }
        photo[new int2(px, py)] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), ph.A);
    }
}
