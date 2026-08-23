using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>GPU offload for ImageAdjustment.AdjustColors via ComputeSharp/
/// DX12 -- see TryAdjustColors' own doc comment for why this specific
/// effect was picked as the first one to move off the CPU. Kept in its own
/// file/class (not inside ImageAdjustment.cs itself) so the CPU
/// implementation there stays untouched and easy to fall back to.</summary>
public static class GpuColorAdjustments
{
    /// <summary>Runs the exact same math as ImageAdjustment's private
    /// AdjustColors, but as a single DX12 compute-shader dispatch instead
    /// of a CPU Parallel.For -- AdjustColors is called UNCONDITIONALLY on
    /// every single composite render (not gated behind an amount>0 check
    /// like the other finishing effects below it), so it's on the hot path
    /// for every tick of every color/placement slider drag regardless of
    /// which other effects are even enabled, making it the highest-value
    /// first target for GPU offload. It's also a pure per-pixel transform
    /// with no neighbor/multi-pass dependency, so a straight 1:1 port to a
    /// compute shader is both natural and easy to verify against the CPU
    /// path (see the GpuVerify scratch harness used to confirm this).
    /// Returns false (leaving <paramref name="pixels"/> untouched) if no
    /// DX12-capable GPU/driver is available, so callers can transparently
    /// fall back to the CPU implementation instead of crashing on older
    /// hardware.</summary>
    public static bool TryAdjustColors(byte[] pixels, int stride, int width, int height, ImageAdjustment.ColorAdjustments adj)
    {
        if (adj.IsIdentity) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "ColorAdjustments", width, height);
            texture.CopyFrom(pixelSpan);

            device.For(width, height, BuildShader(texture, adj));
            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            // No DX12-capable adapter, driver rejected the shader, etc. --
            // whatever the reason, the caller falls back to the CPU path.
            return false;
        }
    }

    /// <summary>Builds an AdjustColorsShader instance targeting an already-
    /// allocated texture -- factored out of TryAdjustColors so
    /// GpuCompositePipeline can chain this same shader onto a texture it's
    /// already uploaded for another effect, instead of each effect paying
    /// for its own separate upload/download round trip.</summary>
    public static AdjustColorsShader BuildShader(IReadWriteNormalizedTexture2D<float4> texture, ImageAdjustment.ColorAdjustments adj)
    {
        double satFactor = 1 + adj.Saturation / 100.0;
        double contrastFactor = 1 + adj.Contrast / 100.0;
        double brightnessOffset = adj.Brightness / 100.0 * 255.0;
        double tempShift = adj.Temperature / 100.0 * 40.0;
        double tintShift = adj.Tint / 100.0 * 40.0;
        double vibranceAmt = adj.Vibrance / 100.0 * 0.65;
        double highlightsAmt = adj.Highlights / 100.0 * 130.0;
        double shadowsAmt = adj.Shadows / 100.0 * 130.0;
        double whitesAmt = adj.Whites / 100.0 * 150.0;
        double blacksAmt = adj.Blacks / 100.0 * 150.0;
        double colorTintT = adj.ColorTintStrength / 100.0;

        float hm00 = 1, hm01 = 0, hm02 = 0, hm10 = 0, hm11 = 1, hm12 = 0, hm20 = 0, hm21 = 0, hm22 = 1;
        bool useHue = adj.Hue != 0;
        if (useHue)
        {
            double rad = adj.Hue * Math.PI / 180.0;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
            hm00 = (float)(0.213 + cosA * 0.787 - sinA * 0.213);
            hm01 = (float)(0.715 - cosA * 0.715 - sinA * 0.715);
            hm02 = (float)(0.072 - cosA * 0.072 + sinA * 0.928);
            hm10 = (float)(0.213 - cosA * 0.213 + sinA * 0.143);
            hm11 = (float)(0.715 + cosA * 0.285 + sinA * 0.140);
            hm12 = (float)(0.072 - cosA * 0.072 - sinA * 0.283);
            hm20 = (float)(0.213 - cosA * 0.213 - sinA * 0.787);
            hm21 = (float)(0.715 - cosA * 0.715 + sinA * 0.715);
            hm22 = (float)(0.072 + cosA * 0.928 + sinA * 0.072);
        }

        return new AdjustColorsShader(
            texture,
            adj.Temperature != 0, (float)tempShift,
            adj.Tint != 0, (float)tintShift,
            useHue, hm00, hm01, hm02, hm10, hm11, hm12, hm20, hm21, hm22,
            adj.Vibrance != 0, (float)vibranceAmt,
            adj.Saturation != 0, (float)satFactor,
            adj.Contrast != 0, (float)contrastFactor,
            adj.Brightness != 0, (float)brightnessOffset,
            adj.Highlights != 0 || adj.Shadows != 0 || adj.Whites != 0 || adj.Blacks != 0,
            adj.Highlights != 0, (float)highlightsAmt,
            adj.Shadows != 0, (float)shadowsAmt,
            adj.Whites != 0, (float)whitesAmt,
            adj.Blacks != 0, (float)blacksAmt,
            adj.ColorTintStrength != 0, (float)colorTintT, adj.ColorTintR, adj.ColorTintG, adj.ColorTintB);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AdjustColorsShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    bool hasTemperature, float tempShift,
    bool hasTint, float tintShift,
    bool useHue, float hm00, float hm01, float hm02, float hm10, float hm11, float hm12, float hm20, float hm21, float hm22,
    bool hasVibrance, float vibranceAmt,
    bool hasSaturation, float satFactor,
    bool hasContrast, float contrastFactor,
    bool hasBrightness, float brightnessOffset,
    bool useToneRegions,
    bool hasHighlights, float highlightsAmt,
    bool hasShadows, float shadowsAmt,
    bool hasWhites, float whitesAmt,
    bool hasBlacks, float blacksAmt,
    bool useColorTint, float colorTintT, float colorTintR, float colorTintG, float colorTintB) : IComputeShader
{
    public void Execute()
    {
        float4 px = texture[ThreadIds.XY];
        float r = px.R * 255f, g = px.G * 255f, b = px.B * 255f;

        if (hasTemperature) { r += tempShift; b -= tempShift; }
        if (hasTint) { g += tintShift; }

        if (useHue)
        {
            float nr = r * hm00 + g * hm01 + b * hm02;
            float ng = r * hm10 + g * hm11 + b * hm12;
            float nb = r * hm20 + g * hm21 + b * hm22;
            r = nr; g = ng; b = nb;
        }

        if (hasVibrance)
        {
            RgbToHsl(r, g, b, out float h, out float s, out float l);
            float hueDist = Hlsl.Abs(h - 30f);
            hueDist = Hlsl.Min(hueDist, 360f - hueDist);
            float skinProtect = 0.5f + 0.5f * Hlsl.Saturate(hueDist / 45f);
            float boost = (1f - s) * vibranceAmt * skinProtect;
            float newS = Hlsl.Saturate(s + boost);
            HslToRgb(h, newS, l, out r, out g, out b);
        }

        if (hasSaturation)
        {
            float gray = 0.299f * r + 0.587f * g + 0.114f * b;
            r = gray + (r - gray) * satFactor;
            g = gray + (g - gray) * satFactor;
            b = gray + (b - gray) * satFactor;
        }

        if (hasContrast)
        {
            r = (r - 128f) * contrastFactor + 128f;
            g = (g - 128f) * contrastFactor + 128f;
            b = (b - 128f) * contrastFactor + 128f;
        }

        if (hasBrightness) { r += brightnessOffset; g += brightnessOffset; b += brightnessOffset; }

        if (useToneRegions)
        {
            float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
            float offset = 0f;
            if (hasHighlights) offset += highlightsAmt * Smoothstep(lum01, 0.25f, 1f);
            if (hasShadows) offset += shadowsAmt * (1f - Smoothstep(lum01, 0f, 0.75f));
            if (hasWhites) { float w = Smoothstep(lum01, 0.5f, 1f); offset += whitesAmt * w * w; }
            if (hasBlacks) { float w = 1f - Smoothstep(lum01, 0f, 0.5f); offset += blacksAmt * w * w; }
            r += offset; g += offset; b += offset;
        }

        if (useColorTint)
        {
            float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
            float targetR = colorTintR * lum01;
            float targetG = colorTintG * lum01;
            float targetB = colorTintB * lum01;
            r += (targetR - r) * colorTintT;
            g += (targetG - g) * colorTintT;
            b += (targetB - b) * colorTintT;
        }

        px.R = Hlsl.Saturate(r / 255f);
        px.G = Hlsl.Saturate(g / 255f);
        px.B = Hlsl.Saturate(b / 255f);
        texture[ThreadIds.XY] = px;
    }

    private static float Smoothstep(float x, float edge0, float edge1)
    {
        float t = Hlsl.Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float rn = r / 255f, gn = g / 255f, bn = b / 255f;
        float max = Hlsl.Max(rn, Hlsl.Max(gn, bn));
        float min = Hlsl.Min(rn, Hlsl.Min(gn, bn));
        l = (max + min) / 2f;
        float delta = max - min;
        if (delta < 1e-6f)
        {
            h = 0f;
            s = 0f;
            return;
        }
        s = l < 0.5f ? delta / (max + min) : delta / (2f - max - min);
        if (max == rn) h = 60f * (((gn - bn) / delta) % 6f);
        else if (max == gn) h = 60f * (((bn - rn) / delta) + 2f);
        else h = 60f * (((rn - gn) / delta) + 4f);
        if (h < 0f) h += 360f;
    }

    private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s <= 0f)
        {
            r = g = b = l * 255f;
            return;
        }
        float c = (1f - Hlsl.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Hlsl.Abs((h / 60f) % 2f - 1f));
        float m = l - c / 2f;
        float rn, gn, bn;
        if (h < 60f) { rn = c; gn = x; bn = 0f; }
        else if (h < 120f) { rn = x; gn = c; bn = 0f; }
        else if (h < 180f) { rn = 0f; gn = c; bn = x; }
        else if (h < 240f) { rn = 0f; gn = x; bn = c; }
        else if (h < 300f) { rn = x; gn = 0f; bn = c; }
        else { rn = c; gn = 0f; bn = x; }
        r = (rn + m) * 255f;
        g = (gn + m) * 255f;
        b = (bn + m) * 255f;
    }
}
