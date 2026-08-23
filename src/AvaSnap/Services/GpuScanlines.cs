using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for CompositeOverlayOntoPhoto's ApplyScanlines --
/// see GPU_MIGRATION_PLAN.md's "残作業2" item 3. Two passes, same as the
/// CPU version: darken every even row, then apply up to
/// ImageAdjustment.MaxGlitchBands horizontal-shift glitch bands. The band
/// parameters (which rows, how far to shift) are cheap to compute -- at
/// most 4 calls to ImageAdjustment.HashNoise -- so they're computed on the
/// CPU exactly like the CPU path does and passed to the shader as plain
/// scalars, rather than trying to run that tiny sequential computation on
/// the GPU itself.</summary>
public static class GpuScanlines
{
    /// <summary>Returns false (leaving <paramref name="pixels"/> untouched)
    /// if no DX12-capable GPU/driver is available, so the caller falls back
    /// to its own CPU ApplyScanlines instead.</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount, double scale = 1.0)
    {
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "Scanlines.A", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount, scale);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same scanlines pass as <see cref="TryApply"/>, but operating
    /// directly on an already GPU-resident <paramref name="texture"/> -- no
    /// upload/download of its own. Used by GpuCompositeChain -- see its own
    /// doc comment.</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount, double scale = 1.0)
    {
        double strength = amount / 100.0;
        float darkenFactor = (float)(1.0 - strength * 0.35);

        int bandCount = Math.Max(1, (int)Math.Round(strength * ImageAdjustment.MaxGlitchBands));
        Span<int> bandStart = stackalloc int[4];
        Span<int> bandEnd = stackalloc int[4];
        Span<int> bandShift = stackalloc int[4];
        for (int band = 0; band < 4; band++)
        {
            if (band >= bandCount)
            {
                bandStart[band] = 0;
                bandEnd[band] = 0;
                bandShift[band] = 0;
                continue;
            }

            int bandY = (int)((ImageAdjustment.HashNoise(band, 0, ImageAdjustment.VhsGlitchSeed) + 1) / 2 * height);
            int bandHeight = 2 + (int)((ImageAdjustment.HashNoise(band, 1, ImageAdjustment.VhsGlitchSeed) + 1) / 2 * 4);
            int shift = (int)Math.Round(ImageAdjustment.HashNoise(band, 2, ImageAdjustment.VhsGlitchSeed) * ImageAdjustment.MaxGlitchShift * strength * scale);

            bandStart[band] = bandY;
            bandEnd[band] = Math.Min(bandY + bandHeight, height);
            bandShift[band] = shift;
        }

        device.For(width, height, new EvenRowDarkenShader(texture, darkenFactor));

        // Snapshot AFTER darkening (matching the CPU order: `original =
        // pixels.Clone()` happens after the darken pass), then the
        // glitch-band shader reads from this snapshot and writes back into
        // `texture` -- a ping-pong (GPU-to-GPU, not a CPU round trip), since
        // shifted rows read NEIGHBORING x positions.
        ReadWriteTexture2D<Bgra32, float4> original = GpuTexturePool.Rent(device, "Scanlines.B", width, height);
        texture.CopyTo(original);

        device.For(width, height, new GlitchBandShader(original, texture, width,
            bandStart[0], bandEnd[0], bandShift[0],
            bandStart[1], bandEnd[1], bandShift[1],
            bandStart[2], bandEnd[2], bandShift[2],
            bandStart[3], bandEnd[3], bandShift[3]));

        return true;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EvenRowDarkenShader(
    IReadWriteNormalizedTexture2D<float4> texture, float darkenFactor) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        if (pos.Y % 2 != 0)
        {
            return;
        }

        float4 px = texture[pos];
        texture[pos] = new float4(px.R * darkenFactor, px.G * darkenFactor, px.B * darkenFactor, px.A);
    }
}

/// <summary>For each pixel, checks which (if any) of up to 4 glitch bands
/// its row falls into -- later bands (higher index) override earlier ones
/// on overlap, matching the CPU version's sequential for-loop where a
/// later band's own write simply overwrites an earlier one's for the same
/// row. A row matching no band (or a band with shift=0) reads from
/// <paramref name="original"/> at its own unshifted position, an exact
/// passthrough copy.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GlitchBandShader(
    IReadWriteNormalizedTexture2D<float4> original,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width,
    int band0Start, int band0End, int band0Shift,
    int band1Start, int band1End, int band1Shift,
    int band2Start, int band2End, int band2Shift,
    int band3Start, int band3End, int band3Shift) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int y = pos.Y;
        int shift = 0;
        if (y >= band0Start && y < band0End) shift = band0Shift;
        if (y >= band1Start && y < band1End) shift = band1Shift;
        if (y >= band2Start && y < band2End) shift = band2Shift;
        if (y >= band3Start && y < band3End) shift = band3Shift;

        int srcX = Hlsl.Clamp(pos.X - shift, 0, width - 1);
        destination[pos] = original[new int2(srcX, y)];
    }
}
