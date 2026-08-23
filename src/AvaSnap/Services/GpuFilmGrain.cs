using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for CompositeOverlayOntoPhoto's ApplyFilmGrain --
/// see GPU_MIGRATION_PLAN.md's "残作業2" item 4. The noise FIELD itself
/// (ImageAdjustment.GenerateArNoise) is an autoregressive, raster-scan-
/// dependent computation that can't be parallelized (each sample depends on
/// its own left/upper neighbors already having been generated) -- but it's
/// only ever computed once per (width, height, seed) and cached
/// (ImageAdjustment.GrainNoiseCache/GetArNoise), so this doesn't try to
/// move THAT part to the GPU. What it does move is the per-pixel BLEND step
/// (luminance-weighted soft-light blend against the noise), which has no
/// such dependency. The noise field's own upload to the GPU is cached the
/// same way GetArNoise caches it on the CPU side: by reference, so a
/// render where the photo size hasn't changed (the common case, e.g.
/// during a color-slider drag) skips re-uploading it every time, same idea
/// as GpuTexturePool.RentUploaded but for a ReadWriteBuffer&lt;float&gt;
/// instead of a texture (GpuTexturePool itself only manages the
/// Bgra32/float4 texture type the rest of the pipeline uses).</summary>
public static class GpuFilmGrain
{
    private static double[]? _lastNoise;
    private static ReadWriteBuffer<float>? _noiseBuffer;

    /// <summary>Returns false (leaving <paramref name="pixels"/> untouched)
    /// if no DX12-capable GPU/driver is available, so the caller falls back
    /// to its own CPU ApplyFilmGrain instead.</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount)
    {
        if (amount <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "Grain", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same film-grain pass as <see cref="TryApply"/>, but
    /// operating directly on an already GPU-resident <paramref name="texture"/>
    /// -- no upload/download of its own (the noise buffer itself is still
    /// cached/uploaded the same way regardless of caller). Used by
    /// GpuCompositeChain -- see its own doc comment.</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount)
    {
        if (amount <= 0) return true;

        double[] noise = ImageAdjustment.GetArNoise(width, height, ImageAdjustment.GrainSeed);
        float strength = (float)(amount / 100.0 * 0.5);

        if (!ReferenceEquals(_lastNoise, noise) || _noiseBuffer is null || _noiseBuffer.Length != noise.Length)
        {
            var noiseFloats = new float[noise.Length];
            for (int i = 0; i < noise.Length; i++)
            {
                noiseFloats[i] = (float)noise[i];
            }
            _noiseBuffer?.Dispose();
            _noiseBuffer = device.AllocateReadWriteBuffer(noiseFloats);
            _lastNoise = noise;
        }

        device.For(width, height, new FilmGrainShader(texture, _noiseBuffer, width, strength));

        return true;
    }
}

/// <summary>Mirrors ApplyFilmGrain's own per-pixel blend exactly -- see its
/// doc comment in ImageAdjustment.cs for why luminance-weighted soft-light
/// instead of a flat additive blend.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct FilmGrainShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    ReadWriteBuffer<float> noise,
    int width, float strength) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float4 px = texture[pos];
        float b0 = px.B, g0 = px.G, r0 = px.R;

        float luminance = 0.299f * r0 + 0.587f * g0 + 0.114f * b0;
        float lumaFactor = LuminanceFactor(luminance);
        float blend = Hlsl.Saturate(0.5f + noise[idx] * strength * lumaFactor);

        float b = SoftLight(b0, blend);
        float g = SoftLight(g0, blend);
        float r = SoftLight(r0, blend);

        texture[pos] = new float4(Hlsl.Saturate(r), Hlsl.Saturate(g), Hlsl.Saturate(b), px.A);
    }

    private static float LuminanceFactor(float luminance)
    {
        const float fadeStart = 0.5f;
        const float floorFactor = 0.15f;
        float t = Hlsl.Saturate((luminance - fadeStart) / (1f - fadeStart));
        float eased = t * t * (3f - 2f * t);
        return floorFactor + (1f - floorFactor) * (1f - eased);
    }

    private static float SoftLight(float baseF, float blendF)
    {
        if (blendF < 0.5f)
        {
            return 2f * baseF * blendF + baseF * baseF * (1f - 2f * blendF);
        }
        return Hlsl.Sqrt(baseF) * (2f * blendF - 1f) + 2f * baseF * (1f - blendF);
    }
}
