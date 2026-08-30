using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>アバター/ドロップシャドウ合成の後に走る「仕上げ」エフェクト
/// (ソフト・シャープ・クラリティ・フェード・グロー・ライトリーク・色収差・
/// カラーブリード・ビネット)を1つの GPU パイプラインで。GpuCompositePipeline と
/// 同じ 1アップロード/1ダウンロードの発想の、連鎖後半ぶん。
///
/// CPU に残るトーングラデ・走査線・グレインは、元の順序でここのエフェクトの
/// 「間」に挟まる(トーングラデはライトリークと色収差の間、走査線はカラーブリードと
/// ビネットの間)。全部を1回のパスにまとめると順序が崩れるので、
/// TryRunPreToneGradient / TryRunPreScanlines / TryRunVignette の3入口に分け、
/// それぞれが CPU ステップに挟まれた区間だけを担当する。
///
/// フェード床上限・グローぼかし半径上限などの定数は ImageAdjustment の内部 const を
/// 直接読む(CPU/GPU で単一の真実)。テクスチャ確保は GpuTexturePool がキャッシュする。</summary>
public static class GpuFinishingEffects
{
    /// <summary>ソフト・シャープ・クラリティ・フェード・グロー・ライトリーク
    /// (元の CPU 順で、アバター/ドロップシャドウ合成とトーングラデの間)。</summary>
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

    /// <summary>色収差・カラーブリード(元の CPU 順で、トーングラデと走査線の間)。</summary>
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

    /// <summary>ビネットのみ(元の CPU 順で、走査線とグレインの間)。</summary>
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

    /// <summary>private な byte[] 版 TryRun と同じエフェクト群を、既に GPU 上にある
    /// <paramref name="mainTexture"/> へ直接かける(アップロード/ダウンロード無し)。
    /// 色収差/カラーブリードは `current` を別テクスチャへ ping-pong させることがあるが、
    /// 戻る前に <paramref name="mainTexture"/> へコピーし直すので、呼び出し側は常に
    /// <paramref name="mainTexture"/> が最新と信じてよい。GpuCompositeChain から使う。</summary>
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

        // `current` = 今どの物理テクスチャが最新か。開始時は mainTexture。色収差/
        // カラーブリードは近傍を読むので in-place できず scratch へ ping-pong し
        // `current` を張り替える。他のエフェクトは自分の画素しか触らないので in-place。
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
            // 色収差/カラーブリードが結果を scratch に残したので mainTexture へ戻す
            // (GPU 同士の安いコピー)。次ステージが mainTexture を最新前提で使えるように。
            current.CopyTo(mainTexture);
        }
        return true;
    }

    /// <summary>(a, b, c) のうち `current` でない2つを返す。3つのうち常に1つだけが
    /// `current` なので一意。スクラッチ用テクスチャを取るのに使う。</summary>
    private static (ReadWriteTexture2D<Bgra32, float4> first, ReadWriteTexture2D<Bgra32, float4> second) OtherTwo(
        ReadWriteTexture2D<Bgra32, float4> current,
        ReadWriteTexture2D<Bgra32, float4> a, ReadWriteTexture2D<Bgra32, float4> b, ReadWriteTexture2D<Bgra32, float4> c)
    {
        if (ReferenceEquals(current, a)) return (b, c);
        if (ReferenceEquals(current, b)) return (a, c);
        return (a, b);
    }

}

// ---- 以下のシェーダは、それぞれ ImageAdjustment の同目的 CPU メソッドの再現。
//      内部計算は 0..255 float 空間(CPU の byte 空間の式をそのまま)、テクスチャ
//      入出力の境界でだけ 0..1 と変換する。 ----

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

/// <summary>ソフト/シャープ/クラリティ共通。いずれも「自身のぼかしへ寄せる/離す」。
/// <paramref name="strength"/> 正でぼかしへ寄せる(ソフト)、負で離す(シャープ/
/// クラリティ)。<paramref name="useMidtoneWeight"/> は中間調のベル型輝度重みで
/// さらにスケール(クラリティのみ)。</summary>
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

/// <summary><paramref name="source"/> を x 方向にずらして読み(赤は一方、青は他方、
/// 緑はそのまま)、<paramref name="destination"/> へ書く。近傍を読むので in-place 不可、
/// 別テクスチャが必要。</summary>
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

/// <summary>カラーブリード前半(後半は YCbCrUnpackShader)。各画素を Y'CbCr にして
/// 同じテクスチャへ (R=Y, G=Cb, B=Cr) で詰め直す(外に出さない内部詰め方)。既存の
/// BoxBlurPassShader でぼかせるように。Y は道連れで捨て、ぼかした Cb/Cr だけ使う。
/// 自分の画素しか触らないので in-place で安全。</summary>
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

/// <summary>カラーブリード後半。<paramref name="current"/> の R に残る未ぼかしの Y と、
/// <paramref name="blurred"/> の G/B にある横ぼかし済み Cb/Cr から RGB を復元し、
/// <paramref name="current"/> へ書き戻す。各テクスチャから自分の画素しか読まないので
/// in-place で安全。</summary>
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
