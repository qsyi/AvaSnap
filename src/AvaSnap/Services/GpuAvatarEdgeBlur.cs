using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for ImageAdjustment.BlurPng (the avatar cutout's
/// silhouette feathering) -- see GPU_MIGRATION_PLAN.md's "残作業6".
///
/// The CPU version (BlurEdgePremultiplied) computes an exact Euclidean
/// distance transform (Felzenszwalb &amp; Huttenlocher) to find each pixel's
/// signed distance to the foreground/background boundary. That algorithm's
/// 1D pass keeps small per-column/per-row scratch arrays sized to the
/// column/row length, which doesn't map onto a GPU thread (HLSL locals need
/// a compile-time-known size). Rather than force that exact algorithm onto
/// the GPU, this uses the standard GPU-native technique for the same
/// problem: the Jump Flooding Algorithm (JFA). Every pixel starts by
/// recording itself as a "seed" if it sits on the foreground/background
/// boundary (differs from an in-bounds 4-neighbor), then O(log(max(width,
/// height))) parallel propagation passes let every pixel discover its
/// nearest seed by repeatedly comparing against neighbors at halving
/// offsets. It's an approximation (occasionally a pixel or two off from the
/// exact nearest boundary point), not a concern given this project's
/// "CPU一致は求めない" policy -- and it's a textbook-correct way to get a
/// distance field on a GPU, not a shortcut.
///
/// Same overall shape as BlurEdgePremultiplied otherwise: premultiplied
/// color+alpha gets a box blur (to reconstruct a clean fill color for the
/// feather band), and the final alpha comes from an eased falloff over the
/// boundary distance, not from the box blur directly.</summary>
public static class GpuAvatarEdgeBlur
{
    // Mirrors ImageAdjustment.EdgeBlurForegroundAlphaThreshold (see its own
    // doc comment for why this is close to zero, not the naive 50%
    // midpoint) -- kept as its own copy rather than referencing that
    // constant directly since ComputeSharp shader bodies can't reference
    // external constants, only constructor-passed values (same reason the
    // other Gpu*.cs files compute from ImageAdjustment's consts in host
    // code and pass the RESULT in, not the constant itself).
    private const float ForegroundAlphaThreshold = ImageAdjustment.EdgeBlurForegroundAlphaThreshold / 255f;

    /// <summary>Returns false (leaving <paramref name="pixels"/> untouched)
    /// if no DX12-capable GPU/driver is available, so the caller falls back
    /// to its own CPU BlurEdgePremultiplied instead.</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double edgeBlurRadius)
    {
        if (edgeBlurRadius <= 0) return true;
        int radius = (int)Math.Round(edgeBlurRadius);
        if (radius <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> source = GpuTexturePool.Rent(device, "EdgeBlur.Source", width, height);
            source.CopyFrom(pixelSpan);

            ReadWriteTexture2D<Bgra32, float4> premulA = GpuTexturePool.Rent(device, "EdgeBlur.PremulA", width, height);
            ReadWriteTexture2D<Bgra32, float4> premulB = GpuTexturePool.Rent(device, "EdgeBlur.PremulB", width, height);
            device.For(width, height, new PremultiplyShader(source, premulA));
            device.For(width, height, new PremulBoxBlurPassShader(premulA, premulB, width, height, radius, horizontal: true));
            device.For(width, height, new PremulBoxBlurPassShader(premulB, premulA, width, height, radius, horizontal: false));
            // premulA now holds the blurred premultiplied color+alpha.

            int count = width * height;
            using ReadWriteBuffer<float> seedX0 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedY0 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedX1 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedY1 = device.AllocateReadWriteBuffer<float>(count);

            device.For(width, height, new EdgeBlurInitSeedShader(source, seedX0, seedY0, width, height, ForegroundAlphaThreshold));

            ReadWriteBuffer<float> curX = seedX0, curY = seedY0, nextX = seedX1, nextY = seedY1;
            int maxDim = Math.Max(width, height);
            int step = 1;
            while (step * 2 < maxDim) step *= 2;
            for (int s = step; s >= 1; s /= 2)
            {
                device.For(width, height, new EdgeBlurJfaStepShader(curX, curY, nextX, nextY, width, height, s));
                (curX, nextX) = (nextX, curX);
                (curY, nextY) = (nextY, curY);
            }
            // One extra step=1 pass ("JFA+1"), a well-known refinement that
            // catches the occasional off-by-one error the base algorithm
            // leaves behind.
            device.For(width, height, new EdgeBlurJfaStepShader(curX, curY, nextX, nextY, width, height, 1));
            (curX, nextX) = (nextX, curX);
            (curY, nextY) = (nextY, curY);

            device.For(width, height, new EdgeBlurComposeShader(source, premulA, curX, curY, width, radius, ForegroundAlphaThreshold));

            source.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Premultiplies color by alpha (so a later box blur of this
/// texture never lets fully-transparent pixels' garbage RGB leak into
/// nearby opaque pixels) while carrying alpha through unchanged as the 4th
/// channel, ready for <see cref="PremulBoxBlurPassShader"/> to blur all
/// four channels together in one pass.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PremultiplyShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination) : IComputeShader
{
    public void Execute()
    {
        float4 px = source[ThreadIds.XY];
        destination[ThreadIds.XY] = new float4(px.R * px.A, px.G * px.A, px.B * px.A, px.A);
    }
}

/// <summary>Separable box blur over ALL FOUR channels (including alpha,
/// unlike GpuCompositePipeline's BoxBlurPassShader which passes alpha
/// through untouched) -- BlurEdgePremultiplied needs a blurred alpha too,
/// as the divisor that un-premultiplies the blurred color back out.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PremulBoxBlurPassShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width, int height, int radius, bool horizontal) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float sumB = 0f, sumG = 0f, sumR = 0f, sumA = 0f;
        int windowSize = radius * 2 + 1;

        if (horizontal)
        {
            for (int k = -radius; k <= radius; k++)
            {
                int xx = Hlsl.Clamp(x + k, 0, width - 1);
                float4 px = source[new int2(xx, y)];
                sumB += px.B; sumG += px.G; sumR += px.R; sumA += px.A;
            }
        }
        else
        {
            for (int k = -radius; k <= radius; k++)
            {
                int yy = Hlsl.Clamp(y + k, 0, height - 1);
                float4 px = source[new int2(x, yy)];
                sumB += px.B; sumG += px.G; sumR += px.R; sumA += px.A;
            }
        }

        destination[new int2(x, y)] = new float4(sumR / windowSize, sumG / windowSize, sumB / windowSize, sumA / windowSize);
    }
}

/// <summary>Seeds the Jump Flooding Algorithm: a pixel becomes its own seed
/// (records its own coordinates) if it sits on the foreground/background
/// boundary -- differs from any in-bounds 4-neighbor's foreground/background
/// class -- else it's marked invalid (-1,-1) for the propagation passes to
/// fill in.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurInitSeedShader(
    IReadWriteNormalizedTexture2D<float4> source,
    ReadWriteBuffer<float> seedX, ReadWriteBuffer<float> seedY,
    int width, int height, float foregroundAlphaThreshold) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;
        bool isFg = source[pos].A >= foregroundAlphaThreshold;

        bool leftDiff = pos.X > 0 && (source[new int2(pos.X - 1, pos.Y)].A >= foregroundAlphaThreshold) != isFg;
        bool rightDiff = pos.X < width - 1 && (source[new int2(pos.X + 1, pos.Y)].A >= foregroundAlphaThreshold) != isFg;
        bool upDiff = pos.Y > 0 && (source[new int2(pos.X, pos.Y - 1)].A >= foregroundAlphaThreshold) != isFg;
        bool downDiff = pos.Y < height - 1 && (source[new int2(pos.X, pos.Y + 1)].A >= foregroundAlphaThreshold) != isFg;
        bool isBoundary = leftDiff || rightDiff || upDiff || downDiff;

        if (isBoundary)
        {
            seedX[idx] = (float)pos.X;
            seedY[idx] = (float)pos.Y;
        }
        else
        {
            seedX[idx] = -1f;
            seedY[idx] = -1f;
        }
    }
}

/// <summary>One Jump Flooding propagation pass: each pixel looks at its own
/// current nearest-seed guess plus its 8 neighbors offset by <paramref
/// name="stepSize"/> pixels, and keeps whichever candidate seed is
/// actually closest. Run with halving step sizes (see GpuAvatarEdgeBlur.
/// TryApply) until every pixel has propagated its seed from as far as
/// max(width, height) away.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurJfaStepShader(
    ReadWriteBuffer<float> seedXIn, ReadWriteBuffer<float> seedYIn,
    ReadWriteBuffer<float> seedXOut, ReadWriteBuffer<float> seedYOut,
    int width, int height, int stepSize) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float bestX = seedXIn[idx];
        float bestY = seedYIn[idx];
        float bestDist = bestX >= 0f ? Distance2(pos, bestX, bestY) : 1e18f;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = pos.X + dx * stepSize;
                int ny = pos.Y + dy * stepSize;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;

                int nidx = ny * width + nx;
                float candX = seedXIn[nidx];
                if (candX < 0f) continue;
                float candY = seedYIn[nidx];
                float d = Distance2(pos, candX, candY);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestX = candX;
                    bestY = candY;
                }
            }
        }

        seedXOut[idx] = bestX;
        seedYOut[idx] = bestY;
    }

    private static float Distance2(int2 pos, float sx, float sy)
    {
        float dx = pos.X - sx;
        float dy = pos.Y - sy;
        return dx * dx + dy * dy;
    }
}

/// <summary>Final composition: turns each pixel's boundary distance (from
/// the completed JFA seed search) into the same eased alpha falloff
/// BlurEdgePremultiplied uses, and reconstructs the feather band's color
/// from the blurred premultiplied texture -- mirrors that method's own
/// final loop exactly (see its doc comment in ImageAdjustment.cs), just
/// with the distance coming from JFA instead of an exact EDT. Writes back
/// into <paramref name="source"/> in place: safe because this shader only
/// ever reads its OWN pixel from every input, never a neighbor's.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurComposeShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> blurredPremul,
    ReadWriteBuffer<float> seedX, ReadWriteBuffer<float> seedY,
    int width, int radius, float foregroundAlphaThreshold) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float sx = seedX[idx];
        float dist;
        if (sx < 0f)
        {
            dist = 1e9f;
        }
        else
        {
            float dx = pos.X - sx;
            float dy = pos.Y - seedY[idx];
            dist = Hlsl.Sqrt(dx * dx + dy * dy);
        }

        bool isFg = source[pos].A >= foregroundAlphaThreshold;
        float signedDist = isFg ? dist : -dist;

        float t = Hlsl.Saturate((signedDist + radius) / (2f * radius));
        float eased = t * t * (3f - 2f * t);

        if (eased >= 0.999f)
        {
            // Fully opaque -- no boundary within the radius, leave this
            // pixel's color exactly as it was.
            return;
        }

        if (eased <= 0.001f)
        {
            source[pos] = new float4(0f, 0f, 0f, 0f);
            return;
        }

        float4 blurred = blurredPremul[pos];
        float aBox = Hlsl.Max(blurred.A, 1f / 255f);
        float outR = Hlsl.Saturate(blurred.R / aBox);
        float outG = Hlsl.Saturate(blurred.G / aBox);
        float outB = Hlsl.Saturate(blurred.B / aBox);
        source[pos] = new float4(outR, outG, outB, eased);
    }
}
