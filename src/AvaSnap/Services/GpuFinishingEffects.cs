using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>The "finishing" effects that run AFTER the avatar/drop-shadow
/// compositing step in CompositeOverlayOntoPhoto -- softness, sharpness,
/// clarity, fade, glow, light leak, chromatic aberration, color bleed, and
/// vignette -- as one GPU pipeline, same one-upload/one-download idea as
/// GpuCompositePipeline (see its own doc comment) but for this second half
/// of the effect chain. Kept as a SEPARATE GPU round trip from
/// GpuCompositePipeline rather than merged into one, because drop-shadow
/// compositing and the avatar alpha-blend in between still run on the CPU
/// (they involve a second, differently-sized/offset buffer -- the avatar
/// PNG -- not just the single photo buffer these two pipelines share) and
/// haven't been ported yet. Tone gradient, scanlines, and film grain also
/// still run on the CPU after this returns -- tone gradient needs an
/// aggregate color-extraction pass over the whole image first, and film
/// grain needs its own precomputed noise field uploaded as a texture,
/// neither of which this straightforward per-pixel/box-blur pipeline
/// covers yet. Those three CPU steps sit INTERLEAVED between these GPU-
/// covered effects in the original order (tone gradient between light leak
/// and chromatic aberration; scanlines between color bleed and vignette),
/// so calling everything covered here in one giant pass would silently
/// reorder the pipeline -- see TryRunPreToneGradient/TryRunPreScanlines/
/// TryRunVignette, the three grouped entry points CompositeOverlayOntoPhoto
/// actually calls, each bracketing exactly the effects that sit between two
/// of those CPU steps (or before the first/after the last). Each is still
/// its own single upload/download, just for a smaller slice of the chain
/// than one call covering everything would be.
///
/// Formula constants (fade floor cap, glow blur radius cap, etc.) are read
/// directly off ImageAdjustment's own internal consts (MaxFadeFloor,
/// MaxGlowRadius, ...) rather than duplicated here, so there's one source
/// of truth for both the CPU and GPU paths. Texture allocations are cached
/// across calls by GpuTexturePool, not freshly allocated every render.</summary>
public static class GpuFinishingEffects
{
    /// <summary>Softness, sharpness, clarity, fade, glow, and light leak --
    /// everything between the avatar/drop-shadow compositing step and tone
    /// gradient in the original CPU order.</summary>
    public static bool TryRunPreToneGradient(byte[] pixels, int stride, int width, int height,
        double softnessAmount, double sharpnessAmount, double finishDetailScale,
        double clarityAmount, double clarityScale,
        double fadeAmount,
        double glowAmount, double glowScale,
        double lightLeakAmount, double lightLeakAngle, double lightLeakDistance,
        byte lightLeakColorB, byte lightLeakColorG, byte lightLeakColorR) =>
        TryRun(pixels, stride, width, height,
            softnessAmount, sharpnessAmount, finishDetailScale,
            clarityAmount, clarityScale,
            fadeAmount,
            glowAmount, glowScale,
            lightLeakAmount, lightLeakAngle, lightLeakDistance, lightLeakColorB, lightLeakColorG, lightLeakColorR,
            chromaticAberrationAmount: 0, colorBleedAmount: 0, vhsScale: 1.0,
            vignetteAmount: 0);

    /// <summary>Chromatic aberration and color bleed -- everything between
    /// tone gradient and scanlines in the original CPU order.</summary>
    public static bool TryRunPreScanlines(byte[] pixels, int stride, int width, int height,
        double chromaticAberrationAmount, double colorBleedAmount, double vhsScale) =>
        TryRun(pixels, stride, width, height,
            softnessAmount: 0, sharpnessAmount: 0, finishDetailScale: 1.0,
            clarityAmount: 0, clarityScale: 1.0,
            fadeAmount: 0,
            glowAmount: 0, glowScale: 1.0,
            lightLeakAmount: 0, lightLeakAngle: 0, lightLeakDistance: 0, lightLeakColorB: 0, lightLeakColorG: 0, lightLeakColorR: 0,
            chromaticAberrationAmount, colorBleedAmount, vhsScale,
            vignetteAmount: 0);

    /// <summary>Vignette alone -- everything between scanlines and film
    /// grain in the original CPU order.</summary>
    public static bool TryRunVignette(byte[] pixels, int stride, int width, int height, double vignetteAmount) =>
        TryRun(pixels, stride, width, height,
            softnessAmount: 0, sharpnessAmount: 0, finishDetailScale: 1.0,
            clarityAmount: 0, clarityScale: 1.0,
            fadeAmount: 0,
            glowAmount: 0, glowScale: 1.0,
            lightLeakAmount: 0, lightLeakAngle: 0, lightLeakDistance: 0, lightLeakColorB: 0, lightLeakColorG: 0, lightLeakColorR: 0,
            chromaticAberrationAmount: 0, colorBleedAmount: 0, vhsScale: 1.0,
            vignetteAmount);

    private static bool TryRun(byte[] pixels, int stride, int width, int height,
        double softnessAmount, double sharpnessAmount, double finishDetailScale,
        double clarityAmount, double clarityScale,
        double fadeAmount,
        double glowAmount, double glowScale,
        double lightLeakAmount, double lightLeakAngle, double lightLeakDistance,
        byte lightLeakColorB, byte lightLeakColorG, byte lightLeakColorR,
        double chromaticAberrationAmount, double colorBleedAmount, double vhsScale,
        double vignetteAmount)
    {
        bool any = softnessAmount > 0 || sharpnessAmount > 0 || clarityAmount > 0 || fadeAmount > 0
            || glowAmount > 0 || lightLeakAmount > 0 || chromaticAberrationAmount > 0
            || colorBleedAmount > 0 || vignetteAmount > 0;
        if (!any) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> texA = GpuTexturePool.Rent(device, "Finishing.A", width, height);
            texA.CopyFrom(pixelSpan);

            ApplyToTexture(texA, device, width, height,
                softnessAmount, sharpnessAmount, finishDetailScale,
                clarityAmount, clarityScale, fadeAmount, glowAmount, glowScale,
                lightLeakAmount, lightLeakAngle, lightLeakDistance, lightLeakColorB, lightLeakColorG, lightLeakColorR,
                chromaticAberrationAmount, colorBleedAmount, vhsScale, vignetteAmount);

            texA.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same effect group as the private byte[]-based TryRun
    /// overload (called by TryRunPreToneGradient/TryRunPreScanlines/
    /// TryRunVignette), but operating directly on an already GPU-resident
    /// <paramref name="mainTexture"/> -- no upload/download of its own.
    /// <paramref name="mainTexture"/> plays the role <c>texA</c> played
    /// before: one of the three ping-pong slots ChromaticAberration/
    /// ColorBleed may swap `current` away from, in which case the final
    /// result is copied back into it with a cheap GPU-to-GPU copy before
    /// returning, so callers can always rely on <paramref name="mainTexture"/>
    /// holding the up-to-date image afterward. Used by GpuCompositeChain --
    /// see its own doc comment.</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> mainTexture, GraphicsDevice device, int width, int height,
        double softnessAmount, double sharpnessAmount, double finishDetailScale,
        double clarityAmount, double clarityScale,
        double fadeAmount,
        double glowAmount, double glowScale,
        double lightLeakAmount, double lightLeakAngle, double lightLeakDistance,
        byte lightLeakColorB, byte lightLeakColorG, byte lightLeakColorR,
        double chromaticAberrationAmount, double colorBleedAmount, double vhsScale,
        double vignetteAmount)
    {
        bool any = softnessAmount > 0 || sharpnessAmount > 0 || clarityAmount > 0 || fadeAmount > 0
            || glowAmount > 0 || lightLeakAmount > 0 || chromaticAberrationAmount > 0
            || colorBleedAmount > 0 || vignetteAmount > 0;
        if (!any) return true;

        ReadWriteTexture2D<Bgra32, float4> texB = GpuTexturePool.Rent(device, "Finishing.B", width, height);
        ReadWriteTexture2D<Bgra32, float4> texC = GpuTexturePool.Rent(device, "Finishing.C", width, height);

        // `current` is whichever of the three physical textures holds the
        // up-to-date image right now -- mainTexture at the start, but
        // ChromaticAberration/ColorBleed below read from every OTHER
        // pixel's position too (not just their own), so they can't safely
        // read-and-write the same texture in one dispatch and instead
        // ping-pong onto a scratch texture, reassigning `current` to point
        // at it. Every other effect here only ever touches its own pixel,
        // so it writes back into `current` in place without needing to
        // ping-pong.
        ReadWriteTexture2D<Bgra32, float4> current = mainTexture;

        if (softnessAmount > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(ImageAdjustment.FinishDetailRadius * finishDetailScale));
            var (blurred, scratch) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new BoxBlurPassShader(current, scratch, width, height, radius, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, blurred, width, height, radius, horizontal: false));
            device.For(width, height, new BlurBlendShader(current, blurred, (float)(softnessAmount / 100.0), useMidtoneWeight: false));
        }

        if (sharpnessAmount > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(ImageAdjustment.FinishDetailRadius * finishDetailScale));
            var (blurred, scratch) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new BoxBlurPassShader(current, scratch, width, height, radius, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, blurred, width, height, radius, horizontal: false));
            device.For(width, height, new BlurBlendShader(current, blurred, (float)(-(sharpnessAmount / 100.0 * 1.5)), useMidtoneWeight: false));
        }

        if (clarityAmount > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(ImageAdjustment.MaxClarityRadius * clarityScale));
            var (blurred, scratch) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new BoxBlurPassShader(current, scratch, width, height, radius, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, blurred, width, height, radius, horizontal: false));
            device.For(width, height, new BlurBlendShader(current, blurred, (float)(-(clarityAmount / 100.0 * 1.2)), useMidtoneWeight: true));
        }

        if (fadeAmount > 0)
        {
            double t = fadeAmount / 100.0;
            device.For(width, height, new FadeShader(current, (float)(t * ImageAdjustment.MaxFadeFloor), (float)(1.0 - t * ImageAdjustment.MaxFadeDesaturate)));
        }

        if (glowAmount > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(glowAmount / 100.0 * ImageAdjustment.MaxGlowRadius * glowScale));
            var (bright, scratch) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new GlowExtractShader(current, bright, (float)ImageAdjustment.GlowThreshold));
            device.For(width, height, new BoxBlurPassShader(bright, scratch, width, height, radius, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, bright, width, height, radius, horizontal: false));
            device.For(width, height, new GlowBlendShader(current, bright));
        }

        if (lightLeakAmount > 0)
        {
            double strength = lightLeakAmount / 100.0;
            double maxDist = Math.Sqrt(width * (double)width + height * (double)height) * 0.6;
            double rad = lightLeakAngle * Math.PI / 180.0;
            double dirX = -Math.Sin(rad), dirY = Math.Cos(rad);
            double halfW = width / 2.0, halfH = height / 2.0;
            double tX = Math.Abs(dirX) > 1e-9 ? halfW / Math.Abs(dirX) : double.PositiveInfinity;
            double tY = Math.Abs(dirY) > 1e-9 ? halfH / Math.Abs(dirY) : double.PositiveInfinity;
            double t = Math.Min(tX, tY) * Math.Clamp(lightLeakDistance, 0, 1);
            float anchorX = (float)(halfW + dirX * t);
            float anchorY = (float)(halfH + dirY * t);
            if (maxDist > 0)
            {
                device.For(width, height, new LightLeakShader(current, anchorX, anchorY, (float)maxDist, (float)strength,
                    lightLeakColorB, lightLeakColorG, lightLeakColorR));
            }
        }

        if (chromaticAberrationAmount > 0)
        {
            int offset = Math.Max(1, (int)Math.Round(chromaticAberrationAmount / 100.0 * ImageAdjustment.MaxAberrationOffset * vhsScale));
            var (dest, _) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new ChromaticAberrationShader(current, dest, width, offset));
            current = dest;
        }

        if (colorBleedAmount > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(colorBleedAmount / 100.0 * ImageAdjustment.MaxColorBleedRadius * vhsScale));
            var (scratch, _) = OtherTwo(current, mainTexture, texB, texC);
            device.For(width, height, new RgbToYCbCrPackShader(current));
            device.For(width, height, new BoxBlurPassShader(current, scratch, width, height, radius, horizontal: true));
            device.For(width, height, new YCbCrUnpackShader(current, scratch));
        }

        if (vignetteAmount > 0)
        {
            double strength = vignetteAmount / 100.0;
            double centerX = width / 2.0, centerY = height / 2.0;
            double maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);
            if (maxDist > 0)
            {
                device.For(width, height, new VignetteShader(current, (float)centerX, (float)centerY, (float)maxDist, (float)strength));
            }
        }

        if (!ReferenceEquals(current, mainTexture))
        {
            // A cheap GPU-to-GPU copy, not a CPU round trip -- ChromaticAberration
            // or ColorBleed left the final result sitting on a scratch texture
            // instead of mainTexture, so callers (GpuCompositeChain chaining
            // straight into the next stage) can keep relying on mainTexture
            // always holding the up-to-date image.
            current.CopyTo(mainTexture);
        }
        return true;
    }

    /// <summary>Returns the two textures out of (a, b, c) that are NOT
    /// `current` -- always well-defined since exactly one of the three is
    /// ever `current` at a time. Used to grab scratch space without caring
    /// which physical texture happens to be free after an earlier
    /// ChromaticAberration/ColorBleed pass reassigned `current`.</summary>
    private static (ReadWriteTexture2D<Bgra32, float4> first, ReadWriteTexture2D<Bgra32, float4> second) OtherTwo(
        ReadWriteTexture2D<Bgra32, float4> current,
        ReadWriteTexture2D<Bgra32, float4> a, ReadWriteTexture2D<Bgra32, float4> b, ReadWriteTexture2D<Bgra32, float4> c)
    {
        if (ReferenceEquals(current, a)) return (b, c);
        if (ReferenceEquals(current, b)) return (a, c);
        return (a, b);
    }

}

// ---- Shaders below: each one mirrors a single ImageAdjustment CPU method
//      of the same purpose -- see GpuFinishingEffects.TryRun's call sites
//      for which CPU method each corresponds to. All operate in 0..255
//      float space internally (matching the CPU byte-space formulas
//      literally) even though the underlying texture storage is normalized
//      0..1, converting at the read/write boundary only. ----

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct FadeShader(
    IReadWriteNormalizedTexture2D<float4> texture, float floor, float satFactor) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 px = texture[pos];
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        float gray = 0.299f * r + 0.587f * g + 0.114f * b;
        r = gray + (r - gray) * satFactor;
        g = gray + (g - gray) * satFactor;
        b = gray + (b - gray) * satFactor;
        b = floor + b * (255f - floor) / 255f;
        g = floor + g * (255f - floor) / 255f;
        r = floor + r * (255f - floor) / 255f;
        texture[pos] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), px.A);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct VignetteShader(
    IReadWriteNormalizedTexture2D<float4> texture, float centerX, float centerY, float maxDist, float strength) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 px = texture[pos];
        float dx = pos.X - centerX, dy = pos.Y - centerY;
        float dist = Hlsl.Saturate(Hlsl.Sqrt(dx * dx + dy * dy) / maxDist);
        float falloff = 1f - strength * (dist * dist);
        texture[pos] = new float4(Hlsl.Saturate(px.R * falloff), Hlsl.Saturate(px.G * falloff), Hlsl.Saturate(px.B * falloff), px.A);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct LightLeakShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    float anchorX, float anchorY, float maxDist, float strength,
    float colorB, float colorG, float colorR) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 px = texture[pos];
        float dx = pos.X - anchorX, dy = pos.Y - anchorY;
        float cornerT = Hlsl.Saturate(1f - Hlsl.Sqrt(dx * dx + dy * dy) / maxDist);
        float leak = cornerT * cornerT * strength;
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        b = 255f - (255f - b) * (255f - colorB * leak) / 255f;
        g = 255f - (255f - g) * (255f - colorG * leak) / 255f;
        r = 255f - (255f - r) * (255f - colorR * leak) / 255f;
        texture[pos] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), px.A);
    }
}

/// <summary>Shared by Softness/Sharpness/Clarity (see their own call sites
/// in TryRun): all three are "blend each pixel toward/away from a blur of
/// itself" -- positive <paramref name="strength"/> blends TOWARD the blur
/// (softness), negative blends AWAY from it (sharpness/clarity), and
/// <paramref name="useMidtoneWeight"/> additionally scales that by a bell-
/// curve luminance weight (clarity only).</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurBlendShader(
    IReadWriteNormalizedTexture2D<float4> current,
    IReadWriteNormalizedTexture2D<float4> blurred,
    float strength, bool useMidtoneWeight) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 orig = current[pos];
        float4 bl = blurred[pos];
        float r = orig.R * 255f, g = orig.G * 255f, b = orig.B * 255f;
        float br = bl.R * 255f, bg = bl.G * 255f, bb = bl.B * 255f;

        float weight = 1f;
        if (useMidtoneWeight)
        {
            float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
            weight = 1f - Hlsl.Abs(lum01 - 0.5f) * 2f;
        }

        float rr = r - (r - br) * strength * weight;
        float gg = g - (g - bg) * strength * weight;
        float bbb = b - (b - bb) * strength * weight;
        current[pos] = new float4(Hlsl.Saturate(rr / 255f), Hlsl.Saturate(gg / 255f), Hlsl.Saturate(bbb / 255f), orig.A);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GlowExtractShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    float threshold) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 px = source[pos];
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
        float t = Hlsl.Saturate((lum01 - threshold) / (1f - threshold));
        float weight = t * t * (3f - 2f * t);
        destination[pos] = new float4(Hlsl.Saturate(r * weight / 255f), Hlsl.Saturate(g * weight / 255f), Hlsl.Saturate(b * weight / 255f), 0f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GlowBlendShader(
    IReadWriteNormalizedTexture2D<float4> current,
    IReadWriteNormalizedTexture2D<float4> glow) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 orig = current[pos];
        float4 gl = glow[pos];
        float r = orig.R * 255f, g = orig.G * 255f, b = orig.B * 255f;
        float gr = gl.R * 255f, gg = gl.G * 255f, gb = gl.B * 255f;
        r = 255f - (255f - r) * (255f - gr) / 255f;
        g = 255f - (255f - g) * (255f - gg) / 255f;
        b = 255f - (255f - b) * (255f - gb) / 255f;
        current[pos] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), orig.A);
    }
}

/// <summary>Reads from <paramref name="source"/> at x-shifted positions
/// (red one way, blue the other, green untouched) and writes the result to
/// <paramref name="destination"/> -- must be a separate texture, not
/// in-place, since each thread reads OTHER pixels' positions that other
/// threads in the same dispatch may be concurrently overwriting.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ChromaticAberrationShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width, int offset) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int rSrcX = Hlsl.Clamp(pos.X - offset, 0, width - 1);
        int bSrcX = Hlsl.Clamp(pos.X + offset, 0, width - 1);
        float4 orig = source[pos];
        float bVal = source[new int2(bSrcX, pos.Y)].B;
        float rVal = source[new int2(rSrcX, pos.Y)].R;
        destination[pos] = new float4(rVal, orig.G, bVal, orig.A);
    }
}

/// <summary>First half of ColorBleed (see YCbCrUnpackShader for the
/// second): converts each pixel to Y'CbCr and packs it back into the same
/// texture as (R=Y, G=Cb, B=Cr) -- an arbitrary internal packing, never
/// exposed -- purely so the existing BoxBlurPassShader can blur it (Y comes
/// along for the ride and gets thrown away; only blurred Cb/Cr are used).
/// Safe in place: only reads/writes its own pixel.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RgbToYCbCrPackShader(IReadWriteNormalizedTexture2D<float4> texture) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 px = texture[pos];
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        float y = 0.299f * r + 0.587f * g + 0.114f * b;
        float cb = 128f - 0.168736f * r - 0.331264f * g + 0.5f * b;
        float cr = 128f + 0.5f * r - 0.418688f * g - 0.081312f * b;
        texture[pos] = new float4(Hlsl.Saturate(y / 255f), Hlsl.Saturate(cb / 255f), Hlsl.Saturate(cr / 255f), px.A);
    }
}

/// <summary>Second half of ColorBleed: reconstructs RGB from the original
/// (unblurred) Y still sitting in <paramref name="current"/>'s R channel
/// and the horizontally-blurred Cb/Cr sitting in <paramref name="blurred"/>'s
/// G/B channels (see RgbToYCbCrPackShader), writing the final RGB back into
/// <paramref name="current"/>. Safe in place: only reads its own pixel from
/// each of the two textures.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct YCbCrUnpackShader(
    IReadWriteNormalizedTexture2D<float4> current,
    IReadWriteNormalizedTexture2D<float4> blurred) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float4 orig = current[pos];
        float4 bl = blurred[pos];
        float y = orig.R * 255f;
        float cb = bl.G * 255f - 128f;
        float cr = bl.B * 255f - 128f;
        float bOut = y + 1.772f * cb;
        float gOut = y - 0.344136f * cb - 0.714136f * cr;
        float rOut = y + 1.402f * cr;
        current[pos] = new float4(Hlsl.Saturate(rOut / 255f), Hlsl.Saturate(gOut / 255f), Hlsl.Saturate(bOut / 255f), orig.A);
    }
}
