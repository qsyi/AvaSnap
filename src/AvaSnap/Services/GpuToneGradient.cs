using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for CompositeOverlayOntoPhoto's ExtractToneGradientColors
/// + ApplyToneGradient pair -- see GPU_MIGRATION_PLAN.md's "残作業2" item
/// 5, the trickiest of the five since it needs an aggregate (weighted
/// average) over the WHOLE image before the per-pixel gradient can even be
/// computed, not just a per-pixel transform like the others.
///
/// Uses a one-thread-per-ROW reduction rather than a full GPU tree
/// reduction (thread-group shared memory, multiple passes, etc.): each of
/// `height` GPU threads sums its own row's weighted bright/dark
/// contributions into an 8-float slot of a small ReadWriteBuffer, which is
/// then downloaded to the CPU (height*8 floats -- trivial next to the
/// photo's own width*height*4 bytes) for the final summation across rows.
/// Real GPU parallelism across rows, without the complexity of a proper
/// tree reduction -- a deliberate, documented simplification, not an
/// oversight; revisit if profiling ever shows the per-row sequential loop
/// (each thread still walks the full row width alone) is the bottleneck.</summary>
public static class GpuToneGradient
{
    /// <summary>Returns false (leaving <paramref name="pixels"/> untouched)
    /// if no DX12-capable GPU/driver is available. lightR/G/B and darkR/G/B
    /// are the gradient's two endpoint colors -- no longer auto-computed on
    /// every call (see TryDetectColors for that, now a separate one-shot
    /// operation the UI's 自動判定 button triggers explicitly).</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount, double rotationDegrees,
        byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB)
    {
        if (amount <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "ToneGradient", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount, rotationDegrees, lightR, lightG, lightB, darkR, darkG, darkB);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same tone-gradient pass as <see cref="TryApply"/>, but
    /// operating directly on an already GPU-resident <paramref name="texture"/>
    /// -- no upload/download of the photo itself. Used by GpuCompositeChain
    /// -- see its own doc comment.</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount, double rotationDegrees,
        byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB)
    {
        if (amount <= 0) return true;

        double rad = rotationDegrees * Math.PI / 180.0;
        float dirX = (float)-Math.Sin(rad), dirY = (float)Math.Cos(rad);
        float cx = (float)((width - 1) / 2.0), cy = (float)((height - 1) / 2.0);
        float maxExtent = (float)((Math.Abs(dirX) * width + Math.Abs(dirY) * height) / 2.0);
        if (maxExtent < 1e-6f) maxExtent = 1f;
        float strength = (float)(amount / 100.0);

        device.For(width, height, new ToneGradientApplyShader(texture, dirX, dirY, cx, cy, maxExtent, strength,
            lightB, lightG, lightR, darkB, darkG, darkR));

        return true;
    }

    /// <summary>The one-shot whole-image weighted-average extraction that
    /// used to run on every ApplyToTexture call -- now only invoked
    /// explicitly by the 自動判定 (auto-detect) button, with the result
    /// stored as the user's own editable 明色/暗色 fields from then on
    /// rather than recomputed every render. Same ToneGradientRowSumShader
    /// reduction as before (see its own doc comment for the weighting
    /// rationale), just no longer chained straight into an apply pass.
    /// Returns false (leaving the out params at their white/black defaults)
    /// if no GPU is available.</summary>
    public static bool TryDetectColors(byte[] pixels, int stride, int width, int height,
        out byte lightR, out byte lightG, out byte lightB, out byte darkR, out byte darkG, out byte darkB)
    {
        lightR = lightG = lightB = 255;
        darkR = darkG = darkB = 0;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "ToneGradientDetect", width, height);
            texture.CopyFrom(pixelSpan);

            using ReadWriteBuffer<float> rowSums = device.AllocateReadWriteBuffer<float>(height * 8);
            device.For(height, new ToneGradientRowSumShader(texture, rowSums, width));

            var rowSumsArray = new float[height * 8];
            rowSums.CopyTo(rowSumsArray);

            double brightW = 0, brightB = 0, brightG = 0, brightR = 0;
            double darkW = 0, darkBAcc = 0, darkGAcc = 0, darkRAcc = 0;
            for (int y = 0; y < height; y++)
            {
                int b = y * 8;
                brightW += rowSumsArray[b]; brightB += rowSumsArray[b + 1]; brightG += rowSumsArray[b + 2]; brightR += rowSumsArray[b + 3];
                darkW += rowSumsArray[b + 4]; darkBAcc += rowSumsArray[b + 5]; darkGAcc += rowSumsArray[b + 6]; darkRAcc += rowSumsArray[b + 7];
            }

            lightR = (byte)Math.Clamp(brightW > 1e-6 ? brightR / brightW : 255, 0, 255);
            lightG = (byte)Math.Clamp(brightW > 1e-6 ? brightG / brightW : 255, 0, 255);
            lightB = (byte)Math.Clamp(brightW > 1e-6 ? brightB / brightW : 255, 0, 255);
            darkR = (byte)Math.Clamp(darkW > 1e-6 ? darkRAcc / darkW : 0, 0, 255);
            darkG = (byte)Math.Clamp(darkW > 1e-6 ? darkGAcc / darkW : 0, 0, 255);
            darkB = (byte)Math.Clamp(darkW > 1e-6 ? darkBAcc / darkW : 0, 0, 255);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>One thread per photo row -- see GpuToneGradient's own doc
/// comment for why this isn't a full tree reduction. Mirrors
/// ExtractToneGradientColors' own per-pixel weighting exactly (smoothstep
/// bright/dark weights, dark pixels additionally boosted by their own HSL
/// saturation), just accumulating into 8 floats per row instead of 8
/// running totals shared across every row the way the CPU version's
/// Parallel.For thread-local accumulators do.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneGradientRowSumShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    ReadWriteBuffer<float> rowSums,
    int width) : IComputeShader
{
    public void Execute()
    {
        int y = ThreadIds.X;
        float bwSum = 0f, bwB = 0f, bwG = 0f, bwR = 0f;
        float dwSum = 0f, dwB = 0f, dwG = 0f, dwR = 0f;

        for (int x = 0; x < width; x++)
        {
            float4 px = texture[new int2(x, y)];
            float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
            float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
            float bw = Smoothstep(lum01, 0.6f, 1f);
            float dw = 1f - Smoothstep(lum01, 0f, 0.4f);
            if (dw > 0f)
            {
                float sat = Saturation(r, g, b);
                dw *= 1f + sat * 3f;
            }

            bwSum += bw; bwB += bw * b; bwG += bw * g; bwR += bw * r;
            dwSum += dw; dwB += dw * b; dwG += dw * g; dwR += dw * r;
        }

        int baseIndex = y * 8;
        rowSums[baseIndex] = bwSum;
        rowSums[baseIndex + 1] = bwB;
        rowSums[baseIndex + 2] = bwG;
        rowSums[baseIndex + 3] = bwR;
        rowSums[baseIndex + 4] = dwSum;
        rowSums[baseIndex + 5] = dwB;
        rowSums[baseIndex + 6] = dwG;
        rowSums[baseIndex + 7] = dwR;
    }

    private static float Smoothstep(float x, float edge0, float edge1)
    {
        float t = Hlsl.Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float Saturation(float r, float g, float b)
    {
        float rn = r / 255f, gn = g / 255f, bn = b / 255f;
        float max = Hlsl.Max(rn, Hlsl.Max(gn, bn));
        float min = Hlsl.Min(rn, Hlsl.Min(gn, bn));
        float delta = max - min;
        if (delta < 1e-6f)
        {
            return 0f;
        }
        float l = (max + min) / 2f;
        return l < 0.5f ? delta / (max + min) : delta / (2f - max - min);
    }
}

/// <summary>Mirrors ApplyToneGradient's own per-pixel screen-blend exactly,
/// given the already-extracted bright/dark colors as plain scalars. The dot
/// (dirX,dirY's own direction) points toward BRIGHT, not dark -- see
/// ApplyToneGradient's own doc comment for why.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneGradientApplyShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    float dirX, float dirY, float cx, float cy, float maxExtent,
    float strength,
    float brightB, float brightG, float brightR,
    float darkB, float darkG, float darkR) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float dx = pos.X - cx, dy = pos.Y - cy;
        float proj = dx * dirX + dy * dirY;
        float t = Hlsl.Saturate((proj + maxExtent) / (2f * maxExtent));
        t = Hlsl.Pow(t, 0.5f); // ToneGradientDarkBias

        // t near 1 is the dot's own direction -- bright/dark swapped from
        // the naive bright-at-t=0 mapping so the dot points at bright.
        float gB = (darkB + (brightB - darkB) * t) * strength;
        float gG = (darkG + (brightG - darkG) * t) * strength;
        float gR = (darkR + (brightR - darkR) * t) * strength;

        float4 px = texture[pos];
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        b = 255f - (255f - b) * (255f - gB) / 255f;
        g = 255f - (255f - g) * (255f - gG) / 255f;
        r = 255f - (255f - r) * (255f - gR) / 255f;
        texture[pos] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), px.A);
    }
}
