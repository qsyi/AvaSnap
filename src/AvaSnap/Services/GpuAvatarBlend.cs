using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for CompositeOverlayOntoPhoto's avatar alpha-blend
/// loop (the aligned/resized avatar PNG composited onto the photo at an
/// offset). The simplest of the remaining CPU steps -- no neighbor
/// dependency, no aggregate statistics, no external noise data -- so it's
/// the natural first piece of GPU_MIGRATION_PLAN.md's "残作業2" list. The
/// photo texture is always freshly uploaded (CompositeOverlayOntoPhoto's
/// own working buffer is a fresh byte[] every call, see its own comment on
/// why), but the overlay texture is rented via GpuTexturePool.RentUploaded
/// under the SAME "Overlay" key GpuDropShadow uses -- within one
/// CompositeOverlayOntoPhoto call both receive the identical overlayPixels
/// array reference, so whichever runs first (DropShadow, per the CPU call
/// order) uploads it and this call's own upload is skipped.</summary>
public static class GpuAvatarBlend
{
    /// <summary>Returns false (leaving <paramref name="photoPixels"/>
    /// untouched) if no DX12-capable GPU/driver is available, so the caller
    /// falls back to its own CPU Parallel.For blend loop instead.</summary>
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

    /// <summary>Same alpha-blend pass as <see cref="TryBlend"/>, but writing
    /// directly onto an already GPU-resident <paramref name="photoTexture"/>
    /// -- no upload/download of the photo itself. Used by
    /// GpuCompositeChain -- see its own doc comment.</summary>
    internal static bool BlendIntoTexture(
        ReadWriteTexture2D<Bgra32, float4> photoTexture, GraphicsDevice device, int photoWidth, int photoHeight,
        byte[] overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        int overlayLeft, int overlayTop)
    {
        if (overlayWidth <= 0 || overlayHeight <= 0) return true;
        if (overlayStride != overlayWidth * 4 || overlayPixels.Length < overlayStride * overlayHeight) return false;

        ReadWriteTexture2D<Bgra32, float4> overlayTexture = GpuTexturePool.RentUploaded(device, "Overlay", overlayPixels, overlayStride, overlayWidth, overlayHeight);

        // Dispatched over the OVERLAY's own dimensions (usually much
        // smaller than the photo), matching the CPU loop's own
        // Parallel.For(0, overlayHeight, ...) -- no need to touch every
        // photo pixel, only the ones the avatar actually covers.
        device.For(overlayWidth, overlayHeight, new AlphaBlendShader(photoTexture, overlayTexture, photoWidth, photoHeight, overlayLeft, overlayTop));

        return true;
    }
}

/// <summary>Mirrors CompositeOverlayOntoPhoto's own alpha-blend loop: for
/// each overlay pixel with non-zero alpha, blends it onto the photo pixel
/// at (overlayLeft + x, overlayTop + y), skipping anything that lands
/// outside the photo's own bounds. Dispatched over the overlay's
/// dimensions, not the photo's -- <paramref name="photo"/> is read AND
/// written at a DIFFERENT position than the thread's own ThreadIds (offset
/// by overlayLeft/overlayTop), but since every thread here owns a distinct
/// overlay pixel and therefore a distinct photo pixel (the overlay never
/// blends onto itself), there's no cross-thread read/write hazard the way
/// a neighbor-sampling shader (blur, chromatic aberration) would have.</summary>
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
