using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Services;

/// <summary>
/// Pixel-level adjustments applied to a copy of a bitmap, never the original --
/// always reprocessed from the pristine source so repeated tweaks never
/// compound/degrade quality. Shared between the align-mode PNG preview and the
/// composite-mode photo preview: both need brightness/contrast/saturation,
/// only the PNG additionally needs the alpha-only edge blur. Also handles
/// rendering the adjusted PNG into pixels and compositing it onto a photo.
/// </summary>
public static class ImageAdjustment
{
    /// <summary>The source image pre-converted to a raw BGRA32 pixel buffer,
    /// prepared once per image load and reused for every subsequent adjustment
    /// tweak -- redoing the FormatConvertedBitmap + WriteableBitmap allocation
    /// on every slider tick was needless work on top of the actual per-pixel
    /// processing.</summary>
    public sealed record PixelBuffer(byte[] Pixels, int Width, int Height, int Stride);

    /// <summary>Every per-pixel color adjustment bundled together, since the
    /// avatar-image look and the background-photo look both use the exact
    /// same set (just with independent values). All the -100..100 fields are
    /// 0 = unchanged; Hue is degrees, 0 = unchanged.</summary>
    public readonly record struct ColorAdjustments(
        double Brightness, double Contrast, double Saturation,
        double Vibrance, double Temperature, double Tint, double Hue,
        double Highlights = 0, double Shadows = 0, double Whites = 0, double Blacks = 0,
        double ColorTintStrength = 0, byte ColorTintR = 255, byte ColorTintG = 255, byte ColorTintB = 255)
    {
        public bool IsIdentity =>
            Brightness == 0 && Contrast == 0 && Saturation == 0 &&
            Vibrance == 0 && Temperature == 0 && Tint == 0 && Hue == 0 &&
            Highlights == 0 && Shadows == 0 && Whites == 0 && Blacks == 0 &&
            ColorTintStrength == 0;
    }

    public static PixelBuffer PrepareBuffer(BitmapSource source)
    {
        var converted = source.Format != PixelFormats.Bgra32
            ? new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0)
            : source;
        int width = converted.PixelWidth, height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return new PixelBuffer(pixels, width, height, stride);
    }

    /// <summary>A cheap nearest-neighbor-downscaled copy, capped to
    /// <paramref name="maxDimension"/> on its longer side. Used to keep
    /// ComputeDominantClusters' k-means sampling cheap (see
    /// ClusterSampleMaxDimension) -- not a live-preview mechanism. Returns
    /// the original unchanged if it's already small enough.</summary>
    public static PixelBuffer Downscale(PixelBuffer source, int maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension) return source;

        double scale = maxDimension / (double)Math.Max(source.Width, source.Height);
        int newWidth = Math.Max(1, (int)(source.Width * scale));
        int newHeight = Math.Max(1, (int)(source.Height * scale));
        int newStride = newWidth * 4;
        var newPixels = new byte[newStride * newHeight];

        Parallel.For(0, newHeight, y =>
        {
            int srcY = Math.Min((int)(y / scale), source.Height - 1);
            int srcRowBase = srcY * source.Stride;
            int dstRowBase = y * newStride;
            for (int x = 0; x < newWidth; x++)
            {
                int srcX = Math.Min((int)(x / scale), source.Width - 1);
                int srcIndex = srcRowBase + srcX * 4;
                int dstIndex = dstRowBase + x * 4;
                newPixels[dstIndex] = source.Pixels[srcIndex];
                newPixels[dstIndex + 1] = source.Pixels[srcIndex + 1];
                newPixels[dstIndex + 2] = source.Pixels[srcIndex + 2];
                newPixels[dstIndex + 3] = source.Pixels[srcIndex + 3];
            }
        });

        return new PixelBuffer(newPixels, newWidth, newHeight, newStride);
    }

    /// <summary>Edge-blur stage only (the expensive part): softens the
    /// cutout's silhouette. Split out from color adjustment so a caller that
    /// caches the result only needs to redo this when the blur radius itself
    /// changes, not on every brightness/contrast/saturation tweak -- and, for
    /// the live overlay, not on every tick of an in-progress slider drag
    /// either (see OverlayWindow's edge-blur-dragging suppression).</summary>
    public static PixelBuffer BlurPng(PixelBuffer original, double edgeBlurRadius)
    {
        if (edgeBlurRadius <= 0) return original;
        var pixels = (byte[])original.Pixels.Clone();
        GpuAvatarEdgeBlur.TryApply(pixels, original.Stride, original.Width, original.Height, edgeBlurRadius);
        return original with { Pixels = pixels };
    }

    /// <summary>Color stage only: color adjustments on top of an already
    /// (optionally) blurred buffer -- cheap enough to re-run on every slider
    /// tick without re-blurring. Called on every single tick of an avatar-
    /// image color-slider drag (see OverlayWindow.ApplyImageAdjustments).</summary>
    public static WriteableBitmap ApplyColor(PixelBuffer buffer, ColorAdjustments adjustments)
    {
        var pixels = (byte[])buffer.Pixels.Clone();
        GpuColorAdjustments.TryAdjustColors(pixels, buffer.Stride, buffer.Width, buffer.Height, adjustments);

        var bitmap = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, buffer.Width, buffer.Height), pixels, buffer.Stride, 0);
        return bitmap;
    }

    /// <summary>The same color-adjustment work ApplyColor does,
    /// but returns the raw <see cref="PixelBuffer"/> instead of wrapping it
    /// in a WriteableBitmap. Used by the "match look" buttons' background-
    /// thread computation (see ComputeLookStats/ComputeDominantClusters),
    /// which only ever need the pixel data itself -- a WriteableBitmap is a
    /// DispatcherObject with thread affinity to whoever creates it, so
    /// building one on a background thread (via Task.Run) is exactly the
    /// kind of thing that either throws or silently misbehaves later.</summary>
    public static PixelBuffer ApplyColorToPixelBuffer(PixelBuffer buffer, ColorAdjustments adjustments, double photoBlurAmount = 0)
    {
        var pixels = (byte[])buffer.Pixels.Clone();
        AdjustColors(pixels, buffer.Stride, buffer.Width, buffer.Height, adjustments);
        if (photoBlurAmount > 0) ApplyPhotoBlur(pixels, buffer.Stride, buffer.Width, buffer.Height, photoBlurAmount, 1.0);
        return buffer with { Pixels = pixels };
    }

    // ---- "Match look" statistics: lets one layer's look be nudged toward
    //      the other's (see SolveMatchAdjustments) by comparing aggregate
    //      color statistics rather than any per-pixel/neural process. ----

    /// <summary>Aggregate color statistics for one layer's pixels (the
    /// opaque ones only, for the avatar cutout -- see
    /// <paramref name="maskByAlpha"/> on <see cref="ComputeLookStats"/>),
    /// used by <see cref="SolveMatchAdjustments"/> to derive slider values
    /// that push one layer's look toward the other's. Raw accumulator sums
    /// are kept (not pre-divided) so per-region means/weights can be derived
    /// lazily below without re-scanning pixels.</summary>
    public readonly record struct LookStats(
        double MeanLuma, double VarLuma, double PixelCount,
        double MeanSaturation,
        double HueSinSum, double HueCosSum, double HueWeightSum,
        double MeanRMinusB, double MeanGOffset,
        double HighlightSumE, double HighlightSumE2, double HighlightSumELuma,
        double ShadowSumE, double ShadowSumE2, double ShadowSumELuma,
        double WhiteSumE, double WhiteSumE2, double WhiteSumELuma,
        double BlackSumE, double BlackSumE2, double BlackSumELuma)
    {
        public double StdLuma => Math.Sqrt(Math.Max(VarLuma, 0));

        /// <summary>Saturation-weighted circular mean hue, in degrees --
        /// only meaningful when <see cref="HueWeightSum"/> isn't negligible
        /// (a near-gray image has no reliable hue to match against).</summary>
        public double MeanHueDegrees
        {
            get
            {
                double deg = Math.Atan2(HueSinSum, HueCosSum) * 180.0 / Math.PI;
                return deg < 0 ? deg + 360 : deg;
            }
        }

        public double HighlightMean => HighlightSumE > 1e-6 ? HighlightSumELuma / HighlightSumE : MeanLuma;
        public double HighlightAvgWeight => HighlightSumE > 1e-6 ? HighlightSumE2 / HighlightSumE : 0;
        public double ShadowMean => ShadowSumE > 1e-6 ? ShadowSumELuma / ShadowSumE : MeanLuma;
        public double ShadowAvgWeight => ShadowSumE > 1e-6 ? ShadowSumE2 / ShadowSumE : 0;
        public double WhiteMean => WhiteSumE > 1e-6 ? WhiteSumELuma / WhiteSumE : MeanLuma;
        public double WhiteAvgWeight => WhiteSumE > 1e-6 ? WhiteSumE2 / WhiteSumE : 0;
        public double BlackMean => BlackSumE > 1e-6 ? BlackSumELuma / BlackSumE : MeanLuma;
        public double BlackAvgWeight => BlackSumE > 1e-6 ? BlackSumE2 / BlackSumE : 0;
    }

    private sealed class LookStatsAccumulator
    {
        public double Count, SumLuma, SumLumaSq, SumSaturation;
        public double SumHueSin, SumHueCos, SumHueWeight;
        public double SumRMinusB, SumGOffset;
        public double HSumE, HSumE2, HSumELuma;
        public double SSumE, SSumE2, SSumELuma;
        public double WSumE, WSumE2, WSumELuma;
        public double BSumE, BSumE2, BSumELuma;
    }

    /// <summary>Scans <paramref name="buffer"/> once, computing everything
    /// <see cref="SolveMatchAdjustments"/> needs. <paramref name="maskByAlpha"/>
    /// skips transparent/near-transparent pixels (alpha &lt; 128, the same
    /// foreground threshold <see cref="BlurEdgePremultiplied"/> uses) -- the
    /// avatar cutout's transparent surroundings would otherwise badly skew
    /// its own statistics. A one-shot pass (called once per Match button
    /// click, not per render), so the usual per-pixel-write Parallel.For
    /// pattern used elsewhere in this file doesn't apply -- this instead
    /// uses the standard local-accumulator-then-merge reduction form, since
    /// every row contributes to the SAME running totals rather than writing
    /// its own independent output pixels.</summary>
    public static LookStats ComputeLookStats(PixelBuffer buffer, bool maskByAlpha)
    {
        int width = buffer.Width, height = buffer.Height, stride = buffer.Stride;
        var pixels = buffer.Pixels;
        var total = new LookStatsAccumulator();
        var sync = new object();

        Parallel.For(0, height,
            () => new LookStatsAccumulator(),
            (y, loopState, local) =>
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int i = rowOffset + x * 4;
                    if (maskByAlpha && pixels[i + 3] < 128) continue;

                    double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                    RgbToHsl(r, g, b, out double h, out double s, out _);

                    local.Count++;
                    local.SumLuma += luma;
                    local.SumLumaSq += luma * luma;
                    local.SumSaturation += s;
                    local.SumHueWeight += s;
                    double rad = h * Math.PI / 180.0;
                    local.SumHueSin += Math.Sin(rad) * s;
                    local.SumHueCos += Math.Cos(rad) * s;
                    local.SumRMinusB += r - b;
                    local.SumGOffset += g - (r + b) / 2.0;

                    // Same tonal-region weighting AdjustColors itself uses,
                    // so the regions being matched here are the same ones
                    // Highlights/Shadows/Whites/Blacks actually shift.
                    double lum01 = Math.Clamp(luma / 255.0, 0, 1);
                    double hw = Smoothstep(lum01, 0.25, 1.0);
                    double sw = 1.0 - Smoothstep(lum01, 0.0, 0.75);
                    double wwBase = Smoothstep(lum01, 0.5, 1.0);
                    double ww = wwBase * wwBase;
                    double bwBase = 1.0 - Smoothstep(lum01, 0.0, 0.5);
                    double bw = bwBase * bwBase;

                    local.HSumE += hw; local.HSumE2 += hw * hw; local.HSumELuma += hw * luma;
                    local.SSumE += sw; local.SSumE2 += sw * sw; local.SSumELuma += sw * luma;
                    local.WSumE += ww; local.WSumE2 += ww * ww; local.WSumELuma += ww * luma;
                    local.BSumE += bw; local.BSumE2 += bw * bw; local.BSumELuma += bw * luma;
                }
                return local;
            },
            local =>
            {
                lock (sync)
                {
                    total.Count += local.Count;
                    total.SumLuma += local.SumLuma;
                    total.SumLumaSq += local.SumLumaSq;
                    total.SumSaturation += local.SumSaturation;
                    total.SumHueWeight += local.SumHueWeight;
                    total.SumHueSin += local.SumHueSin;
                    total.SumHueCos += local.SumHueCos;
                    total.SumRMinusB += local.SumRMinusB;
                    total.SumGOffset += local.SumGOffset;
                    total.HSumE += local.HSumE; total.HSumE2 += local.HSumE2; total.HSumELuma += local.HSumELuma;
                    total.SSumE += local.SSumE; total.SSumE2 += local.SSumE2; total.SSumELuma += local.SSumELuma;
                    total.WSumE += local.WSumE; total.WSumE2 += local.WSumE2; total.WSumELuma += local.WSumELuma;
                    total.BSumE += local.BSumE; total.BSumE2 += local.BSumE2; total.BSumELuma += local.BSumELuma;
                }
            });

        double count = Math.Max(total.Count, 1);
        double meanLuma = total.SumLuma / count;
        double varLuma = Math.Max(total.SumLumaSq / count - meanLuma * meanLuma, 0);

        return new LookStats(
            meanLuma, varLuma, count,
            total.SumSaturation / count,
            total.SumHueSin, total.SumHueCos, total.SumHueWeight,
            total.SumRMinusB / count, total.SumGOffset / count,
            total.HSumE, total.HSumE2, total.HSumELuma,
            total.SSumE, total.SSumE2, total.SSumELuma,
            total.WSumE, total.WSumE2, total.WSumELuma,
            total.BSumE, total.BSumE2, total.BSumELuma);
    }

    /// <summary>Derives slider values that push <paramref name="source"/>'s
    /// (raw/unadjusted) look as close as this app's specific pipeline can
    /// manage toward <paramref name="target"/>'s (the OTHER layer's current,
    /// already-adjusted look). Brightness+Contrast are an exact affine mean/
    /// standard-deviation match -- the same core idea as Reinhard, Adhikhmin,
    /// Gooch &amp; Shirley's 2001 "Color Transfer between Images" (matching
    /// per-channel mean and std-dev between a source and target image, there
    /// applied directly to Lab's decorrelated axes; here applied to luminance
    /// and mapped onto this app's own Contrast-then-Brightness pipeline
    /// instead of a raw per-pixel affine transform, since that's the pair of
    /// sliders that actually exist to scale/shift it). Saturation is a ratio
    /// match of mean HSL saturation -- Vibrance is deliberately left at 0,
    /// since its skin-tone-damped, diminishing-returns curve isn't a clean
    /// linear knob to solve for, and Saturation alone already spans the same
    /// range. Temperature/Tint approximate a gray-world white-balance match
    /// (mean R-B / mean G-vs-(R+B)/2 channel-balance difference). Hue is the
    /// saturation-weighted circular mean hue-angle difference. Highlights/
    /// Shadows/Whites/Blacks each close the remaining gap in their own tonal
    /// region (see SolveToneRegion) -- solved independently per region
    /// (ignoring the small overlap between neighboring regions' weighting),
    /// after accounting for what the Brightness/Contrast solved above would
    /// already do to that region's mean on its own.</summary>
    public static ColorAdjustments SolveMatchAdjustments(LookStats source, LookStats target)
    {
        double sourceStd = Math.Max(source.StdLuma, 1e-3);
        double contrastFactor = Math.Clamp(target.StdLuma / sourceStd, 0.1, 4.0);
        double contrast = Math.Clamp((contrastFactor - 1.0) * 100.0, -100, 100);
        // Re-derive the factor from the CLAMPED slider value, so the
        // brightness solve (and the tone-region ones below) match what will
        // actually be applied, not the pre-clamp ideal.
        contrastFactor = 1 + contrast / 100.0;
        double meanAfterContrast = 128 + (source.MeanLuma - 128) * contrastFactor;
        double brightnessOffset255 = target.MeanLuma - meanAfterContrast;
        double brightness = Math.Clamp(brightnessOffset255 / 255.0 * 100.0, -100, 100);

        double satFactor = Math.Clamp(target.MeanSaturation / Math.Max(source.MeanSaturation, 0.02), 0.1, 4.0);
        double saturation = Math.Clamp((satFactor - 1.0) * 100.0, -100, 100);

        double tempShift = (target.MeanRMinusB - source.MeanRMinusB) / 2.0;
        double temperature = Math.Clamp(tempShift / 40.0 * 100.0, -100, 100);
        double tintShift = target.MeanGOffset - source.MeanGOffset;
        double tint = Math.Clamp(tintShift / 40.0 * 100.0, -100, 100);

        double hue = 0;
        if (source.HueWeightSum > 1e-6 && target.HueWeightSum > 1e-6)
        {
            double diff = (target.MeanHueDegrees - source.MeanHueDegrees) % 360.0;
            if (diff > 180) diff -= 360;
            if (diff < -180) diff += 360;
            hue = Math.Clamp(diff, -180, 180);
        }

        double highlights = SolveToneRegion(source.HighlightMean, source.HighlightAvgWeight, target.HighlightMean, contrastFactor, brightnessOffset255, 130.0);
        double shadows = SolveToneRegion(source.ShadowMean, source.ShadowAvgWeight, target.ShadowMean, contrastFactor, brightnessOffset255, 130.0);
        double whites = SolveToneRegion(source.WhiteMean, source.WhiteAvgWeight, target.WhiteMean, contrastFactor, brightnessOffset255, 150.0);
        double blacks = SolveToneRegion(source.BlackMean, source.BlackAvgWeight, target.BlackMean, contrastFactor, brightnessOffset255, 150.0);

        return new ColorAdjustments(
            brightness, contrast, saturation,
            0, temperature, tint, hue,
            highlights, shadows, whites, blacks);
    }

    /// <summary>One tonal region's share of the Highlights/Shadows/Whites/
    /// Blacks solve: <paramref name="sourceRegionMean"/> is first carried
    /// through the SAME Contrast+Brightness transform already solved above
    /// (an exact linear operation, so it applies to a region's mean exactly
    /// as well as to any single pixel), then the remaining gap to
    /// <paramref name="targetRegionMean"/> is closed by the region's own
    /// slider, scaled by <paramref name="sourceAvgWeight"/> (how strongly
    /// this region's own weighting concentrates on itself -- see
    /// LookStats.HighlightAvgWeight and friends) and by
    /// <paramref name="maxAmt"/> (130 for Highlights/Shadows, 150 for
    /// Whites/Blacks, matching AdjustColors' own constants).</summary>
    private static double SolveToneRegion(double sourceRegionMean, double sourceAvgWeight, double targetRegionMean, double contrastFactor, double brightnessOffset255, double maxAmt)
    {
        if (sourceAvgWeight < 1e-4) return 0;
        double intermediateMean = 128 + (sourceRegionMean - 128) * contrastFactor + brightnessOffset255;
        double amt = (targetRegionMean - intermediateMean) / sourceAvgWeight;
        return Math.Clamp(amt / maxAmt * 100.0, -100, 100);
    }

    // ---- Cluster-based "match look": an upgrade to SolveMatchAdjustments
    //      above -- instead of matching just two global moments (mean, std)
    //      of the whole image, this reduces each layer down to its k=4
    //      dominant colors (via k-means++ in the perceptually-uniform Lab
    //      color space), pairs each source cluster with its nearest target
    //      cluster, then fits the same slider parameters via a WEIGHTED
    //      LEAST-SQUARES regression over those k paired anchor points
    //      instead of just 2 moments. More robust to multi-modal color
    //      distributions (e.g. an avatar with distinct skin/hair/clothing
    //      colors matched against a photo with distinct sky/ground colors)
    //      than a single global average, which would otherwise blend
    //      everything into one muddy, unrepresentative number. ----

    private static double SrgbToLinear(double c8)
    {
        double c = c8 / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private const double LabEpsilon = 0.008856; // (6/29)^3
    private const double LabKappaDenom = 0.128418; // 3*(6/29)^2

    private static double LabF(double t) => t > LabEpsilon ? Math.Cbrt(t) : t / LabKappaDenom + 4.0 / 29.0;

    /// <summary>sRGB (0..255) to CIE L*a*b* (D65 white point) -- used only
    /// as a perceptually-uniform distance metric for clustering/pairing
    /// dominant colors below, not for rendering.</summary>
    private static void RgbToLab(double r, double g, double b, out double l, out double a, out double bb)
    {
        double rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
        double x = (rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375) / 0.95047;
        double y = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
        double z = (rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041) / 1.08883;

        double fx = LabF(x), fy = LabF(y), fz = LabF(z);
        l = 116.0 * fy - 16.0;
        a = 500.0 * (fx - fy);
        bb = 200.0 * (fy - fz);
    }

    private static double LabDistSq(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        double dl = l1 - l2, da = a1 - a2, db = b1 - b2;
        return dl * dl + da * da + db * db;
    }

    /// <summary>One dominant color cluster (see <see cref="ComputeDominantClusters"/>):
    /// its Lab centroid (for distance/pairing) plus the same per-cluster
    /// aggregate stats <see cref="LookStats"/> tracks globally (mean luma,
    /// saturation, hue, white-balance channel deltas), already divided by
    /// this cluster's own pixel weight except where noted.</summary>
    public readonly record struct LookCluster(
        double Weight,
        double LabL, double LabA, double LabB,
        double MeanLuma, double MeanSaturation,
        double HueSinSum, double HueCosSum, double HueWeightSum,
        double MeanRMinusB, double MeanGOffset)
    {
        public double MeanHueDegrees
        {
            get
            {
                double deg = Math.Atan2(HueSinSum, HueCosSum) * 180.0 / Math.PI;
                return deg < 0 ? deg + 360 : deg;
            }
        }
    }

    /// <summary>Longest side a buffer is downscaled to before clustering --
    /// k-means only needs enough samples to find representative dominant
    /// colors, not every pixel of a multi-megapixel photo, and running it
    /// against millions of points would make every Match button click
    /// noticeably slow for no real gain in the resulting clusters.</summary>
    private const int ClusterSampleMaxDimension = 200;

    /// <summary>Reduces <paramref name="buffer"/> to its <paramref name="k"/>
    /// dominant colors via k-means++ in Lab space (deterministic seed, so
    /// repeated clicks on the same image give the same result). Returns
    /// fewer than <paramref name="k"/> entries if the buffer has fewer
    /// distinct samples than that (a tiny or heavily-masked avatar) or if a
    /// cluster ends up empty after the final assignment pass.</summary>
    public static LookCluster[] ComputeDominantClusters(PixelBuffer buffer, bool maskByAlpha, int k)
    {
        var sample = Downscale(buffer, ClusterSampleMaxDimension);
        int width = sample.Width, height = sample.Height, stride = sample.Stride;
        var pixels = sample.Pixels;

        var labL = new List<double>();
        var labA = new List<double>();
        var labB = new List<double>();
        var luma = new List<double>();
        var sat = new List<double>();
        var hueSin = new List<double>();
        var hueCos = new List<double>();
        var hueW = new List<double>();
        var rMinusB = new List<double>();
        var gOffset = new List<double>();

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x * 4;
                if (maskByAlpha && pixels[i + 3] < 128) continue;

                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                RgbToLab(r, g, b, out double l, out double a, out double bLab);
                labL.Add(l); labA.Add(a); labB.Add(bLab);

                double lm = 0.299 * r + 0.587 * g + 0.114 * b;
                RgbToHsl(r, g, b, out double h, out double s, out _);
                luma.Add(lm);
                sat.Add(s);
                double rad = h * Math.PI / 180.0;
                hueSin.Add(Math.Sin(rad) * s);
                hueCos.Add(Math.Cos(rad) * s);
                hueW.Add(s);
                rMinusB.Add(r - b);
                gOffset.Add(g - (r + b) / 2.0);
            }
        }

        int n = labL.Count;
        if (n == 0) return Array.Empty<LookCluster>();
        k = Math.Min(k, n);

        // k-means++ initialization: first centroid uniform-random, each
        // following one picked with probability proportional to its squared
        // distance to the nearest centroid chosen so far -- spreads the
        // initial centroids across the color distribution instead of
        // risking several landing in the same cluster by chance.
        var rng = new Random(20260101);
        var centroidL = new double[k];
        var centroidA = new double[k];
        var centroidB = new double[k];
        int first = rng.Next(n);
        centroidL[0] = labL[first]; centroidA[0] = labA[first]; centroidB[0] = labB[first];

        var minDistSq = new double[n];
        for (int i = 0; i < n; i++) minDistSq[i] = LabDistSq(labL[i], labA[i], labB[i], centroidL[0], centroidA[0], centroidB[0]);

        for (int c = 1; c < k; c++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += minDistSq[i];
            int chosenIndex = n - 1;
            if (sum > 1e-9)
            {
                double pick = rng.NextDouble() * sum;
                double running = 0;
                for (int i = 0; i < n; i++)
                {
                    running += minDistSq[i];
                    if (running >= pick) { chosenIndex = i; break; }
                }
            }
            centroidL[c] = labL[chosenIndex]; centroidA[c] = labA[chosenIndex]; centroidB[c] = labB[chosenIndex];
            for (int i = 0; i < n; i++)
            {
                double d = LabDistSq(labL[i], labA[i], labB[i], centroidL[c], centroidA[c], centroidB[c]);
                if (d < minDistSq[i]) minDistSq[i] = d;
            }
        }

        // Lloyd's algorithm: assign-then-recompute, until assignments stop
        // changing (usually converges in well under 12 passes at k=4).
        var assignment = new int[n];
        const int maxIterations = 12;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int best = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double d = LabDistSq(labL[i], labA[i], labB[i], centroidL[c], centroidA[c], centroidB[c]);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
                if (assignment[i] != best) { assignment[i] = best; changed = true; }
            }

            var sumL = new double[k]; var sumA = new double[k]; var sumBc = new double[k]; var count = new double[k];
            for (int i = 0; i < n; i++)
            {
                int c = assignment[i];
                sumL[c] += labL[i]; sumA[c] += labA[i]; sumBc[c] += labB[i]; count[c]++;
            }
            for (int c = 0; c < k; c++)
            {
                // An empty cluster keeps its previous centroid rather than
                // becoming NaN -- it'll simply stay unused (see the final
                // weight-zero filter below) unless a later iteration's
                // reassignment gives it members again.
                if (count[c] < 1) continue;
                centroidL[c] = sumL[c] / count[c];
                centroidA[c] = sumA[c] / count[c];
                centroidB[c] = sumBc[c] / count[c];
            }
            if (!changed) break;
        }

        var clusterWeight = new double[k];
        var clusterSumLuma = new double[k];
        var clusterSumSat = new double[k];
        var clusterSumHueSin = new double[k];
        var clusterSumHueCos = new double[k];
        var clusterSumHueW = new double[k];
        var clusterSumRMinusB = new double[k];
        var clusterSumGOffset = new double[k];

        for (int i = 0; i < n; i++)
        {
            int c = assignment[i];
            clusterWeight[c]++;
            clusterSumLuma[c] += luma[i];
            clusterSumSat[c] += sat[i];
            clusterSumHueSin[c] += hueSin[i];
            clusterSumHueCos[c] += hueCos[i];
            clusterSumHueW[c] += hueW[i];
            clusterSumRMinusB[c] += rMinusB[i];
            clusterSumGOffset[c] += gOffset[i];
        }

        var result = new List<LookCluster>(k);
        for (int c = 0; c < k; c++)
        {
            if (clusterWeight[c] < 1) continue;
            result.Add(new LookCluster(
                clusterWeight[c],
                centroidL[c], centroidA[c], centroidB[c],
                clusterSumLuma[c] / clusterWeight[c],
                clusterSumSat[c] / clusterWeight[c],
                clusterSumHueSin[c], clusterSumHueCos[c], clusterSumHueW[c],
                clusterSumRMinusB[c] / clusterWeight[c],
                clusterSumGOffset[c] / clusterWeight[c]));
        }
        return result.ToArray();
    }

    /// <summary>Lab-distance falloff scale for cluster-pairing confidence
    /// (see SolveMatchAdjustmentsClustered): a paired-but-still-far-apart
    /// cluster (the "nearest" match wasn't actually close -- e.g. an
    /// avatar's skin-tone cluster with nothing similar anywhere in the
    /// photo) contributes less to the fit than a genuinely close pair,
    /// rather than being trusted just as much as an exact match would be.</summary>
    private const double MatchPairConfidenceScale = 40.0;

    // Per-slider damping applied to the fully-solved ("ideal") match value
    // before it's ever shown to the user, per explicit request: Contrast/
    // Temperature/Tint/Saturation are all cut back hard (these swing the
    // hardest off a small stats mismatch), and everything else (Brightness/
    // Hue/tone-regions) is applied at closer to full strength.
    private const double MatchContrastStrength = 0.3;
    private const double MatchColorBalanceStrength = 0.2; // temperature, tint, saturation
    private const double MatchMinorStrength = 0.3; // brightness/hue/tone-regions

    /// <summary>The cluster-based counterpart to
    /// <see cref="SolveMatchAdjustments"/>: each <paramref name="sourceClusters"/>
    /// entry is paired with its nearest <paramref name="targetClusters"/>
    /// entry by Lab distance (many source clusters may pair with the same
    /// target cluster -- that's fine, and expected whenever the target has
    /// fewer distinct color regions than the source), then Brightness/
    /// Contrast/Saturation/Hue are fit via a weighted least-squares
    /// regression over those paired anchor points (weighted by each source
    /// cluster's own pixel count AND by how close its pairing actually is --
    /// see <see cref="MatchPairConfidenceScale"/>) instead of just the two
    /// global moments <see cref="SolveMatchAdjustments"/> uses. Temperature/
    /// Tint deliberately do NOT use the cluster pairing at all -- white
    /// balance/color cast is a whole-scene lighting property, not something
    /// that should vary by which color region happened to pair with which,
    /// so they're solved from the plain global <paramref name="sourceRegionStats"/>/
    /// <paramref name="targetRegionStats"/> instead (same formula
    /// <see cref="SolveMatchAdjustments"/> uses). Highlights/Shadows/Whites/
    /// Blacks are likewise unchanged from that method -- a luminance-band,
    /// not color-cluster, concept. Falls back to
    /// <see cref="SolveMatchAdjustments"/> entirely if either side has no
    /// clusters at all (an empty/fully-transparent buffer). Every returned
    /// field is finally scaled down by its own damping constant above --
    /// the fully-solved value is treated as an upper bound, not the
    /// delivered result.</summary>
    public static ColorAdjustments SolveMatchAdjustmentsClustered(
        LookCluster[] sourceClusters, LookCluster[] targetClusters,
        LookStats sourceRegionStats, LookStats targetRegionStats)
    {
        ColorAdjustments raw;
        if (sourceClusters.Length == 0 || targetClusters.Length == 0)
        {
            raw = SolveMatchAdjustments(sourceRegionStats, targetRegionStats);
        }
        else
        {
            var pairedTarget = new LookCluster[sourceClusters.Length];
            var pairConfidence = new double[sourceClusters.Length];
            for (int i = 0; i < sourceClusters.Length; i++)
            {
                var s = sourceClusters[i];
                int best = 0;
                double bestDist = double.MaxValue;
                for (int j = 0; j < targetClusters.Length; j++)
                {
                    var t = targetClusters[j];
                    double d = LabDistSq(s.LabL, s.LabA, s.LabB, t.LabL, t.LabA, t.LabB);
                    if (d < bestDist) { bestDist = d; best = j; }
                }
                pairedTarget[i] = targetClusters[best];
                pairConfidence[i] = 1.0 / (1.0 + bestDist / (MatchPairConfidenceScale * MatchPairConfidenceScale));
            }

            // ---- Brightness + Contrast: weighted least-squares line
            //      through the paired (sourceLuma-128, targetLuma-128)
            //      points -- the k-anchor-point generalization of
            //      SolveMatchAdjustments' 2-moment (mean/std) match. ----
            double sumW = 0, sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
            for (int i = 0; i < sourceClusters.Length; i++)
            {
                double w = sourceClusters[i].Weight * pairConfidence[i];
                double x = sourceClusters[i].MeanLuma - 128.0;
                double y = pairedTarget[i].MeanLuma - 128.0;
                sumW += w; sumX += w * x; sumY += w * y; sumXX += w * x * x; sumXY += w * x * y;
            }
            double meanX = sumX / sumW, meanY = sumY / sumW;
            double varX = sumXX / sumW - meanX * meanX;
            double covXY = sumXY / sumW - meanX * meanY;
            double contrastFactor = Math.Clamp(varX > 1e-6 ? covXY / varX : 1.0, 0.1, 4.0);
            double contrast = Math.Clamp((contrastFactor - 1.0) * 100.0, -100, 100);
            contrastFactor = 1 + contrast / 100.0;
            double brightnessOffset255 = meanY - contrastFactor * meanX;
            double brightness = Math.Clamp(brightnessOffset255 / 255.0 * 100.0, -100, 100);

            // ---- Saturation: weighted regression THROUGH THE ORIGIN (the
            //      Saturation slider has no additive term, only a
            //      multiplicative one), same weighting as above. ----
            double satNum = 0, satDen = 0;
            for (int i = 0; i < sourceClusters.Length; i++)
            {
                double w = sourceClusters[i].Weight * pairConfidence[i];
                double sx = sourceClusters[i].MeanSaturation;
                double sy = pairedTarget[i].MeanSaturation;
                satNum += w * sx * sy;
                satDen += w * sx * sx;
            }
            double satFactor = Math.Clamp(satDen > 1e-6 ? satNum / satDen : 1.0, 0.1, 4.0);
            double saturation = Math.Clamp((satFactor - 1.0) * 100.0, -100, 100);

            // ---- Temperature/Tint: plain global mean difference (NOT the
            //      cluster pairing above) -- see the method doc comment for
            //      why. ----
            double tempShift = (targetRegionStats.MeanRMinusB - sourceRegionStats.MeanRMinusB) / 2.0;
            double temperature = Math.Clamp(tempShift / 40.0 * 100.0, -100, 100);
            double tintShift = targetRegionStats.MeanGOffset - sourceRegionStats.MeanGOffset;
            double tint = Math.Clamp(tintShift / 40.0 * 100.0, -100, 100);

            // ---- Hue: circular weighted mean of each pair's hue
            //      difference, weighted by pairing confidence AND by
            //      whichever of the pair is LESS saturation-confident
            //      (HueWeightSum) -- a near-gray cluster on either side
            //      makes that pair's hue difference meaningless. ----
            double hueSinSum = 0, hueCosSum = 0, hueWSum = 0;
            for (int i = 0; i < sourceClusters.Length; i++)
            {
                double confidence = Math.Min(sourceClusters[i].HueWeightSum, pairedTarget[i].HueWeightSum) * pairConfidence[i];
                if (confidence < 1e-6) continue;
                double diffRad = (pairedTarget[i].MeanHueDegrees - sourceClusters[i].MeanHueDegrees) * Math.PI / 180.0;
                hueSinSum += Math.Sin(diffRad) * confidence;
                hueCosSum += Math.Cos(diffRad) * confidence;
                hueWSum += confidence;
            }
            double hue = hueWSum > 1e-6 ? Math.Clamp(Math.Atan2(hueSinSum, hueCosSum) * 180.0 / Math.PI, -180, 180) : 0;

            double highlights = SolveToneRegion(sourceRegionStats.HighlightMean, sourceRegionStats.HighlightAvgWeight, targetRegionStats.HighlightMean, contrastFactor, brightnessOffset255, 130.0);
            double shadows = SolveToneRegion(sourceRegionStats.ShadowMean, sourceRegionStats.ShadowAvgWeight, targetRegionStats.ShadowMean, contrastFactor, brightnessOffset255, 130.0);
            double whites = SolveToneRegion(sourceRegionStats.WhiteMean, sourceRegionStats.WhiteAvgWeight, targetRegionStats.WhiteMean, contrastFactor, brightnessOffset255, 150.0);
            double blacks = SolveToneRegion(sourceRegionStats.BlackMean, sourceRegionStats.BlackAvgWeight, targetRegionStats.BlackMean, contrastFactor, brightnessOffset255, 150.0);

            raw = new ColorAdjustments(
                brightness, contrast, saturation,
                0, temperature, tint, hue,
                highlights, shadows, whites, blacks);
        }

        return new ColorAdjustments(
            raw.Brightness * MatchMinorStrength,
            raw.Contrast * MatchContrastStrength,
            raw.Saturation * MatchColorBalanceStrength,
            0,
            raw.Temperature * MatchColorBalanceStrength,
            raw.Tint * MatchColorBalanceStrength,
            raw.Hue * MatchMinorStrength,
            raw.Highlights * MatchMinorStrength,
            raw.Shadows * MatchMinorStrength,
            raw.Whites * MatchMinorStrength,
            raw.Blacks * MatchMinorStrength);
    }

    /// <summary>Renders the (already look-adjusted) overlay PNG at the given
    /// on-screen size/rotation/opacity into actual pixels, matching exactly
    /// what the live overlay window shows -- used to composite it onto a
    /// still photo, where there's no Canvas + RenderTransform to rely on.
    /// Rendered into a canvas padded to the rotated bounding box (not just
    /// width x height) so a rotated corner doesn't get clipped off, the same
    /// way it would visibly overflow the (unclipped) live overlay's box. The
    /// returned offsets are how far the padded canvas's top-left sits from the
    /// unrotated placement rect's top-left -- callers need this to position
    /// the result correctly on the photo.</summary>
    public static (WriteableBitmap Bitmap, double OffsetX, double OffsetY) RenderOverlayForComposite(
        BitmapSource pngSource, double width, double height, double rotationDegrees, double opacity)
    {
        double rad = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double boundWidth = width * cos + height * sin;
        double boundHeight = width * sin + height * cos;

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(boundWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(boundHeight));
        double offsetX = (boundWidth - width) / 2;
        double offsetY = (boundHeight - height) / 2;

        var image = new Image
        {
            Source = pngSource,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Opacity = Math.Clamp(opacity, 0, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(rotationDegrees),
        };
        Canvas.SetLeft(image, offsetX);
        Canvas.SetTop(image, offsetY);
        var container = new Canvas { Width = boundWidth, Height = boundHeight };
        container.Children.Add(image);

        container.Measure(new Size(boundWidth, boundHeight));
        container.Arrange(new Rect(0, 0, boundWidth, boundHeight));
        container.UpdateLayout();

        var rendered = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(container);
        var bitmap = new WriteableBitmap(rendered);
        // Frozen so the caller can hand it (and anything derived from its
        // pixels) off to a background thread for the rest of the composite
        // pipeline -- this method itself still needs the UI thread (it
        // renders an actual WPF visual tree), but nothing downstream does.
        bitmap.Freeze();
        return (bitmap, offsetX, offsetY);
    }

    /// <summary>Applies photo-level color adjustments to the photo, then
    /// alpha-blends the already-rendered overlay (see
    /// <see cref="RenderOverlayForComposite"/>) on top at the given position,
    /// then finally film grain and vignette (see <see cref="ApplyFilmGrain"/>/
    /// <see cref="ApplyVignette"/>) over the whole composited result -- these
    /// two are the only adjustments that apply once to the finished photo
    /// rather than per-layer. <paramref name="overlayLeft"/>/
    /// <paramref name="overlayTop"/> are the padded overlay bitmap's top-left
    /// in photo pixel coordinates (i.e. already offset by the render's
    /// OffsetX/OffsetY). <paramref name="overlayPixels"/> is optional: when
    /// null (no avatar image loaded), the blend step is skipped entirely and
    /// every finishing effect still applies to the photo alone, so retouching
    /// and saving a photo with no avatar works the same as with one. Takes
    /// the overlay's raw BGRA32 pixels directly rather than a BitmapSource --
    /// the one WPF-visual-tree-dependent step (RenderOverlayForComposite)
    /// happens entirely in the caller, so this method touches nothing but
    /// plain byte[]/PixelBuffer/primitive inputs and can safely run on any
    /// thread, including a background one during a live slider drag.</summary>
    public static WriteableBitmap CompositeOverlayOntoPhoto(
        PixelBuffer photo, ColorAdjustments photoAdjustments,
        byte[]? overlayPixels = null, int overlayStride = 0, int overlayWidth = 0, int overlayHeight = 0,
        double overlayLeft = 0, double overlayTop = 0,
        double grainAmount = 0, double vignetteAmount = 0, double photoBlurAmount = 0, double photoBlurScale = 1.0,
        double softnessAmount = 0, double sharpnessAmount = 0, double finishDetailScale = 1.0,
        double fadeAmount = 0, double glowAmount = 0, double glowScale = 1.0,
        double chromaticAberrationAmount = 0, double colorBleedAmount = 0, double scanlineAmount = 0,
        double vhsScale = 1.0, double clarityAmount = 0, double clarityScale = 1.0, double lightLeakAmount = 0,
        double lightLeakAngle = 225, double lightLeakDistance = 1.0,
        byte lightLeakColorB = 60, byte lightLeakColorG = 160, byte lightLeakColorR = 255,
        double toneGradientAmount = 0, double toneGradientRotation = 0,
        byte toneGradientLightR = 255, byte toneGradientLightG = 255, byte toneGradientLightB = 255,
        byte toneGradientDarkR = 0, byte toneGradientDarkG = 0, byte toneGradientDarkB = 0,
        double dropShadowAmount = 0, double dropShadowDirection = 0, double dropShadowDistance = 0, double dropShadowBlur = 0,
        byte dropShadowColorB = 0, byte dropShadowColorG = 0, byte dropShadowColorR = 0, double dropShadowScale = 1.0,
        bool dropShadowTone = false, double dropShadowDotSize = 8, DropShadowBlendMode dropShadowBlendMode = DropShadowBlendMode.Multiply)
    {
        // The entire effect chain below (color adjust, photo blur, drop
        // shadow, avatar blend, softness/sharpness/clarity/fade/glow/light
        // leak, tone gradient, chromatic aberration/color bleed, scanlines,
        // vignette, film grain) runs as ONE GPU round trip via
        // GpuCompositeChain -- see its own doc comment for why chaining
        // through GPU-resident textures instead of each stage doing its own
        // upload/download matters (measured: roughly half the total time
        // at typical VRChat screenshot resolutions was pure transfer
        // overhead before this). photo.Pixels is passed as the SOURCE (not
        // cloned here) so the upload can be skipped on renders where only
        // the adjustment amounts changed, not the photo itself -- see
        // GpuTexturePool.RentUploaded. `pixels` itself starts uninitialized
        // (no clone needed) since the GPU path fully overwrites it via its
        // own download.
        var pixels = new byte[photo.Pixels.Length];
        int photoBlurRadius = photoBlurAmount > 0
            ? (int)Math.Round(Math.Clamp(photoBlurAmount, 0, 100) / 100.0 * MaxPhotoBlurRadius * photoBlurScale)
            : 0;
        GpuCompositeChain.TryRun(
            photo.Pixels, pixels, photo.Stride, photo.Width, photo.Height, photoAdjustments, photoBlurRadius,
            overlayPixels, overlayStride, overlayWidth, overlayHeight, overlayLeft, overlayTop,
            dropShadowAmount, dropShadowDirection, dropShadowDistance, dropShadowBlur,
            dropShadowColorB, dropShadowColorG, dropShadowColorR, dropShadowScale,
            dropShadowTone, dropShadowDotSize, (int)dropShadowBlendMode,
            softnessAmount, sharpnessAmount, finishDetailScale,
            clarityAmount, clarityScale,
            fadeAmount,
            glowAmount, glowScale,
            lightLeakAmount, lightLeakAngle, lightLeakDistance, lightLeakColorB, lightLeakColorG, lightLeakColorR,
            toneGradientAmount, toneGradientRotation,
            toneGradientLightR, toneGradientLightG, toneGradientLightB,
            toneGradientDarkR, toneGradientDarkG, toneGradientDarkB,
            chromaticAberrationAmount, colorBleedAmount, vhsScale,
            scanlineAmount,
            vignetteAmount,
            grainAmount);

        var result = new WriteableBitmap(photo.Width, photo.Height, 96, 96, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, photo.Width, photo.Height), pixels, photo.Stride, 0);
        // Frozen so this is safe to construct on (and hand back from) a
        // background thread -- see this method's own doc comment.
        result.Freeze();
        return result;
    }

    /// <summary>Crops a finished composite down to a target aspect ratio as
    /// the very last step (after every color/finishing effect already baked
    /// in), centered by default and shiftable via <paramref name="offsetXPercent"/>/
    /// <paramref name="offsetYPercent"/> (0..100, where the crop window sits
    /// along whichever axis has slack -- 50 = centered). Whichever dimension
    /// the target ratio is narrower than the source keeps its full extent;
    /// the other gets trimmed. <paramref name="aspectRatio"/> null (or &lt;=0)
    /// means 自由 (free): no ratio to lock to, so <paramref name="widthPercent"/>/
    /// <paramref name="heightPercent"/> each shrink their own axis
    /// independently against the FULL source dimensions instead of both
    /// deriving from one ratio-fit box.</summary>
    public static WriteableBitmap CropToAspect(WriteableBitmap source, double? aspectRatio, double offsetXPercent, double offsetYPercent, double widthPercent = 100, double heightPercent = 100)
    {
        int srcWidth = source.PixelWidth, srcHeight = source.PixelHeight;
        if (srcWidth <= 0 || srcHeight <= 0) return source;

        int maxCropWidth, maxCropHeight;
        double heightZoomPercent;
        if (aspectRatio is { } ratio && ratio > 0)
        {
            double srcRatio = (double)srcWidth / srcHeight;
            if (ratio > srcRatio)
            {
                maxCropWidth = srcWidth;
                maxCropHeight = Math.Max(1, (int)Math.Round(srcWidth / ratio));
            }
            else
            {
                maxCropHeight = srcHeight;
                maxCropWidth = Math.Max(1, (int)Math.Round(srcHeight * ratio));
            }
            maxCropWidth = Math.Min(maxCropWidth, srcWidth);
            maxCropHeight = Math.Min(maxCropHeight, srcHeight);
            // Fixed-ratio mode: the SAME zoom factor scales both axes (each
            // already ratio-fit above), so the ratio stays locked at any
            // zoom level -- widthPercent alone drives both.
            heightZoomPercent = widthPercent;
        }
        else
        {
            // 自由 (free): each axis's own 100% is the full source extent,
            // and the two knobs are independent -- no ratio to preserve.
            maxCropWidth = srcWidth;
            maxCropHeight = srcHeight;
            heightZoomPercent = heightPercent;
        }

        // widthPercent/heightPercent shrink the crop box below its 100%
        // size (the ratio-maximal box in fixed mode, the full photo in 自由
        // mode) -- a zoom-in-place knob layered on top of the aspect-ratio
        // pick.
        double widthZoom = Math.Clamp(widthPercent, 1, 100) / 100.0;
        double heightZoom = Math.Clamp(heightZoomPercent, 1, 100) / 100.0;
        int cropWidth = Math.Max(1, (int)Math.Round(maxCropWidth * widthZoom));
        int cropHeight = Math.Max(1, (int)Math.Round(maxCropHeight * heightZoom));
        if (cropWidth == srcWidth && cropHeight == srcHeight) return source;

        int maxLeft = srcWidth - cropWidth;
        int maxTop = srcHeight - cropHeight;
        int left = (int)Math.Round(maxLeft * Math.Clamp(offsetXPercent, 0, 100) / 100.0);
        int top = (int)Math.Round(maxTop * Math.Clamp(offsetYPercent, 0, 100) / 100.0);

        var format = source.Format;
        int bytesPerPixel = (format.BitsPerPixel + 7) / 8;
        int cropStride = cropWidth * bytesPerPixel;
        var buffer = new byte[cropStride * cropHeight];
        source.CopyPixels(new Int32Rect(left, top, cropWidth, cropHeight), buffer, cropStride, 0);

        var result = new WriteableBitmap(cropWidth, cropHeight, source.DpiX, source.DpiY, format, null);
        result.WritePixels(new Int32Rect(0, 0, cropWidth, cropHeight), buffer, cropStride, 0);
        // Frozen for the same cross-thread-safety reason as
        // CompositeOverlayOntoPhoto's own result -- this is the last step
        // in the same pipeline.
        result.Freeze();
        return result;
    }

    /// <summary>Splits two same-size composites into one image for a before/
    /// after comparison slider: columns left of <paramref name="splitFraction"/>
    /// (0..1, fraction of the width) come from <paramref name="before"/>,
    /// columns at/after that come from <paramref name="after"/>. A plain
    /// per-row byte copy -- no pixel math at all -- so it's cheap enough to
    /// redo on every tick of the slider dragging it.</summary>
    public static WriteableBitmap MergeBeforeAfter(BitmapSource before, BitmapSource after, double splitFraction)
    {
        int width = after.PixelWidth, height = after.PixelHeight;
        int stride = width * 4;

        var beforePixels = new byte[stride * height];
        var afterPixels = new byte[stride * height];
        CopyAsBgra32(before, beforePixels, stride);
        CopyAsBgra32(after, afterPixels, stride);

        int splitByte = Math.Clamp((int)Math.Round(width * splitFraction), 0, width) * 4;
        var merged = new byte[stride * height];
        Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            Array.Copy(beforePixels, rowOffset, merged, rowOffset, splitByte);
            Array.Copy(afterPixels, rowOffset + splitByte, merged, rowOffset + splitByte, stride - splitByte);
        });

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), merged, stride, 0);
        return bitmap;
    }

    private static void CopyAsBgra32(BitmapSource source, byte[] buffer, int stride)
    {
        BitmapSource bgra32 = source.Format != PixelFormats.Bgra32
            ? new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0)
            : source;
        bgra32.CopyPixels(buffer, stride, 0);
    }

    /// <summary>The alpha level (0..255) at which a pixel counts as "solid"
    /// for edge-blur boundary detection -- deliberately close to zero, not
    /// the naive 50% midpoint. A midpoint threshold means any genuinely
    /// semi-transparent DESIGN element (a translucent visor, glass, a
    /// skirt drawn at partial opacity) that happens to sit below 50% gets
    /// classified as "background," creating an artificial boundary at ITS
    /// own edges too -- if that element is narrower than roughly 2x the
    /// blur radius, its entire area then falls within "close to a
    /// boundary" and gets feathered/blurred throughout, not just the
    /// cutout's true outer silhouette. Treating only near-fully-transparent
    /// pixels as "outside" means semi-transparent interior details are
    /// classified as solid and left alone unless they're actually near the
    /// real silhouette edge, while true silhouette boundaries (which fade
    /// to fully 0 alpha, not just to some translucent value) are still
    /// found correctly. Shared with GpuAvatarEdgeBlur's own copy of this
    /// same threshold (as a normalized float) -- keep both in sync.</summary>
    internal const int EdgeBlurForegroundAlphaThreshold = 10;


    /// <summary>Applies white balance (temperature/tint), then hue rotation,
    /// then vibrance, saturation, contrast, and brightness, to the RGB
    /// channels of every pixel (alpha untouched) -- roughly the same pipeline
    /// order a photo editor uses (correct the color cast first, then
    /// grade/tone on top of that).</summary>
    private static void AdjustColors(byte[] pixels, int stride, int width, int height, ColorAdjustments adj)
    {
        if (adj.IsIdentity) return;

        double satFactor = 1 + adj.Saturation / 100.0;
        double contrastFactor = 1 + adj.Contrast / 100.0;
        double brightnessOffset = adj.Brightness / 100.0 * 255.0;
        double tempShift = adj.Temperature / 100.0 * 40.0;
        double tintShift = adj.Tint / 100.0 * 40.0;
        // Scaled down from a straight 0..1 range: at vibranceAmt=1, boost =
        // (1-s) alone would push EVERY pixel to fully saturated (s=1) since
        // newS = s + (1-s) = 1 regardless of starting s -- far stronger than
        // Saturation=100 (which only doubles each pixel's existing distance
        // from gray, never forcing it to the max). 0.65 (raised from an
        // earlier 0.5 -- see skinProtect below for the other half of this
        // same rebalance) keeps Vibrance at its max closer to comparably
        // strong to Saturation at its max instead of acting like an on/off
        // "full saturation" switch.
        double vibranceAmt = adj.Vibrance / 100.0 * 0.65;

        // Highlights/Shadows/Whites/Blacks: each is an additive shift scaled
        // by a smooth luminance-based weight (0..1), not a hard tonal split --
        // Shadows/Highlights weight the lower/upper half of the range with a
        // broad, gentle ramp (matching Lightroom's own broad "midtone-ish"
        // reach), while Whites/Blacks square that same ramp to concentrate
        // their effect more narrowly at the very extremes (the clipping-point
        // behavior their names imply). Magnitudes are hand-tuned to feel
        // comparable in strength to Brightness at their own max, not derived
        // from anything physical.
        bool useToneRegions = adj.Highlights != 0 || adj.Shadows != 0 || adj.Whites != 0 || adj.Blacks != 0;
        double highlightsAmt = adj.Highlights / 100.0 * 130.0;
        double shadowsAmt = adj.Shadows / 100.0 * 130.0;
        double whitesAmt = adj.Whites / 100.0 * 150.0;
        double blacksAmt = adj.Blacks / 100.0 * 150.0;

        // ティント: a luminance-preserving color wash toward ColorTintR/G/B,
        // not a flat lerp toward a fixed RGB triple -- the target color is
        // itself rescaled by each pixel's own luminance first (targetR =
        // ColorTintR * lum01, etc.), so shadows tint toward a dark version of
        // the chosen color and highlights toward a light version of it,
        // preserving the image's tonal structure/detail even at
        // ColorTintStrength=100 (where a flat lerp would flatten everything
        // to one solid color) -- the same idea as classic sepia toning,
        // generalized to any chosen color.
        bool useColorTint = adj.ColorTintStrength != 0;
        double colorTintT = adj.ColorTintStrength / 100.0;

        // Hue rotation matrix (rotates color around the gray/luminance axis),
        // precomputed once outside the pixel loop -- the standard coefficients
        // behind CSS's hue-rotate() filter / SVG feColorMatrix type="hueRotate".
        bool useHue = adj.Hue != 0;
        double hm00 = 1, hm01 = 0, hm02 = 0, hm10 = 0, hm11 = 1, hm12 = 0, hm20 = 0, hm21 = 0, hm22 = 1;
        if (useHue)
        {
            double rad = adj.Hue * Math.PI / 180.0;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
            hm00 = 0.213 + cosA * 0.787 - sinA * 0.213;
            hm01 = 0.715 - cosA * 0.715 - sinA * 0.715;
            hm02 = 0.072 - cosA * 0.072 + sinA * 0.928;
            hm10 = 0.213 - cosA * 0.213 + sinA * 0.143;
            hm11 = 0.715 + cosA * 0.285 + sinA * 0.140;
            hm12 = 0.072 - cosA * 0.072 - sinA * 0.283;
            hm20 = 0.213 - cosA * 0.213 - sinA * 0.787;
            hm21 = 0.715 - cosA * 0.715 + sinA * 0.715;
            hm22 = 0.072 + cosA * 0.928 + sinA * 0.072;
        }

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x * 4;
                double b = pixels[i];
                double g = pixels[i + 1];
                double r = pixels[i + 2];

                if (adj.Temperature != 0)
                {
                    r += tempShift;
                    b -= tempShift;
                }
                if (adj.Tint != 0)
                {
                    g += tintShift;
                }
                if (useHue)
                {
                    double nr = r * hm00 + g * hm01 + b * hm02;
                    double ng = r * hm10 + g * hm11 + b * hm12;
                    double nb = r * hm20 + g * hm21 + b * hm22;
                    r = nr; g = ng; b = nb;
                }
                if (adj.Vibrance != 0)
                {
                    // True HSL saturation adjustment (not a channel-push
                    // approximation): boosts saturation MORE on already-dull
                    // pixels and LESS on already-vivid ones -- Adobe's own
                    // description of Vibrance -- AND additionally damps the
                    // effect further near skin-tone hues specifically, since
                    // skin usually isn't very saturated to begin with and
                    // would otherwise get the biggest boost of anything in a
                    // portrait, oversaturating faces first. The floor here
                    // (0.5, raised from an earlier 0.25) keeps that damping
                    // from going so far that skin-toned pixels barely move
                    // at all at Vibrance=100 while Saturation=100 visibly
                    // grades the same pixels hard -- most of what this app's
                    // avatar images actually ARE is skin-toned, so that gap
                    // was the main reason the two sliders felt wildly
                    // different in strength instead of just "a bit gentler".
                    RgbToHsl(r, g, b, out double h, out double s, out double l);
                    double hueDist = Math.Abs(h - SkinHueDegrees);
                    hueDist = Math.Min(hueDist, 360.0 - hueDist);
                    double skinProtect = 0.5 + 0.5 * Math.Clamp(hueDist / 45.0, 0, 1);
                    double boost = (1.0 - s) * vibranceAmt * skinProtect;
                    double newS = Math.Clamp(s + boost, 0, 1);
                    HslToRgb(h, newS, l, out r, out g, out b);
                }
                if (adj.Saturation != 0)
                {
                    double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = gray + (r - gray) * satFactor;
                    g = gray + (g - gray) * satFactor;
                    b = gray + (b - gray) * satFactor;
                }
                if (adj.Contrast != 0)
                {
                    r = (r - 128) * contrastFactor + 128;
                    g = (g - 128) * contrastFactor + 128;
                    b = (b - 128) * contrastFactor + 128;
                }
                if (adj.Brightness != 0)
                {
                    r += brightnessOffset;
                    g += brightnessOffset;
                    b += brightnessOffset;
                }
                if (useToneRegions)
                {
                    double lum01 = Math.Clamp((0.299 * r + 0.587 * g + 0.114 * b) / 255.0, 0, 1);
                    double offset = 0;
                    if (adj.Highlights != 0) offset += highlightsAmt * Smoothstep(lum01, 0.25, 1.0);
                    if (adj.Shadows != 0) offset += shadowsAmt * (1.0 - Smoothstep(lum01, 0.0, 0.75));
                    if (adj.Whites != 0)
                    {
                        double w = Smoothstep(lum01, 0.5, 1.0);
                        offset += whitesAmt * w * w;
                    }
                    if (adj.Blacks != 0)
                    {
                        double w = 1.0 - Smoothstep(lum01, 0.0, 0.5);
                        offset += blacksAmt * w * w;
                    }
                    r += offset;
                    g += offset;
                    b += offset;
                }
                if (useColorTint)
                {
                    double lum01 = Math.Clamp((0.299 * r + 0.587 * g + 0.114 * b) / 255.0, 0, 1);
                    double targetR = adj.ColorTintR * lum01;
                    double targetG = adj.ColorTintG * lum01;
                    double targetB = adj.ColorTintB * lum01;
                    r += (targetR - r) * colorTintT;
                    g += (targetG - g) * colorTintT;
                    b += (targetB - b) * colorTintT;
                }

                pixels[i] = (byte)Math.Clamp(b, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(r, 0, 255);
            }
        });
    }

    /// <summary>Approximate hue (degrees) of typical skin tones on the color
    /// wheel -- orange-ish -- used to damp Vibrance's boost near that hue.</summary>
    private const double SkinHueDegrees = 30.0;

    /// <summary>Standard smoothstep: 0 at/below <paramref name="edge0"/>, 1
    /// at/above <paramref name="edge1"/>, eased in between. Used to build the
    /// Highlights/Shadows/Whites/Blacks luminance masks above.</summary>
    private static double Smoothstep(double x, double edge0, double edge1)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn));
        double min = Math.Min(rn, Math.Min(gn, bn));
        l = (max + min) / 2.0;
        double delta = max - min;
        if (delta < 1e-9)
        {
            h = 0;
            s = 0;
            return;
        }
        s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
        if (max == rn) h = 60.0 * (((gn - bn) / delta) % 6.0);
        else if (max == gn) h = 60.0 * (((bn - rn) / delta) + 2.0);
        else h = 60.0 * (((rn - gn) / delta) + 4.0);
        if (h < 0) h += 360.0;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 0)
        {
            r = g = b = l * 255.0;
            return;
        }
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2.0;
        double rn, gn, bn;
        if (h < 60) { rn = c; gn = x; bn = 0; }
        else if (h < 120) { rn = x; gn = c; bn = 0; }
        else if (h < 180) { rn = 0; gn = c; bn = x; }
        else if (h < 240) { rn = 0; gn = x; bn = c; }
        else if (h < 300) { rn = x; gn = 0; bn = c; }
        else { rn = c; gn = 0; bn = x; }
        r = (rn + m) * 255.0;
        g = (gn + m) * 255.0;
        b = (bn + m) * 255.0;
    }

    // ---- Finishing effects: applied once to the final composite result only
    //      (not per-layer), so a user compositing several photos with the
    //      same avatar cutout gets one consistent "shot on film" look on the
    //      output rather than compounding grain/vignette from each layer. ----

    internal const int GrainSeed = 20260101;

    /// <summary>Cache for <see cref="GenerateArNoise"/>, keyed by the buffer
    /// size it was built for: the noise field is fully determined by
    /// width/height/seed (all fixed per photo), so re-deriving it on every
    /// single render -- including every tick of a grain-slider or placement
    /// drag -- was pure waste. Capped defensively rather than sized
    /// precisely.</summary>
    private static readonly Dictionary<(int Width, int Height, int Seed), double[]> GrainNoiseCache = new();

    /// <summary>Builds and caches the film grain noise field for a given
    /// buffer size ahead of time, so the first render at that size doesn't
    /// pay for it. Called once per photo load (for both its full-res and
    /// drag-preview sizes) -- see TryLoadPhotoPixels.</summary>
    public static void PrecomputeFilmGrainNoise(int width, int height) => GetArNoise(width, height, GrainSeed);

    internal static double[] GetArNoise(int width, int height, int seed)
    {
        var key = (width, height, seed);
        if (GrainNoiseCache.TryGetValue(key, out var cached)) return cached;
        var noise = GenerateArNoise(width, height, seed);
        if (GrainNoiseCache.Count >= 4) GrainNoiseCache.Clear();
        GrainNoiseCache[key] = noise;
        return noise;
    }

    /// <summary>Autoregressive noise field: each sample is a hashed random
    /// impulse pulled toward its already-generated left/upper neighbors, the
    /// same idea AV1's film grain synthesis uses an AR model for -- it makes
    /// the noise clump into small organic blobs instead of looking like
    /// independent per-pixel static. Raster scan with a genuine dependency on
    /// the previous column/row, so unlike the rest of this file it can't be
    /// parallelized across rows (same reasoning as ChamferDistance above).</summary>
    private static double[] GenerateArNoise(int width, int height, int seed)
    {
        const double AlphaLeft = 0.22;
        const double AlphaUp = 0.22;
        const double AlphaImpulse = 1.0 - AlphaLeft - AlphaUp;

        var noise = new double[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double impulse = HashNoise(x, y, seed);
                double left = x > 0 ? noise[row + x - 1] : 0.0;
                double up = y > 0 ? noise[row - width + x] : 0.0;
                noise[row + x] = impulse * AlphaImpulse + left * AlphaLeft + up * AlphaUp;
            }
        }
        return noise;
    }

    /// <summary>Cheap deterministic pseudo-noise from integer coordinates --
    /// stands in for a Random instance where per-pixel-parallel code can't
    /// safely share one. Returns a value in -1..1.</summary>
    internal static double HashNoise(int x, int y, int seed)
    {
        unchecked
        {
            int h = (int)(x * 374761393L + y * 668265263L + seed * 2246822519L);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0xFFFFFF * 2.0 - 1.0;
        }
    }

    /// <summary>Radially darkens toward the corners. <paramref name="amount"/>
    /// is 0..100, 0 = off.</summary>
    public static void ApplyVignette(byte[] pixels, int stride, int width, int height, double amount)
    {
        if (amount <= 0) return;
        double strength = amount / 100.0;
        double centerX = width / 2.0, centerY = height / 2.0;
        double maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);
        if (maxDist <= 0) return;

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            double dy = y - centerY;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x * 4;
                double dx = x - centerX;
                double dist = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / maxDist, 0, 1);
                double falloff = 1.0 - strength * (dist * dist);
                pixels[i] = (byte)Math.Clamp(pixels[i] * falloff, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] * falloff, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] * falloff, 0, 255);
            }
        });
    }

    internal const int MaxPhotoBlurRadius = 40;

    /// <summary>Separable two-pass box blur (horizontal then vertical) over a
    /// single int channel -- ApplyPhotoBlur's own blur primitive (see
    /// ApplyColorToPixelBuffer, its only remaining caller now that the GPU
    /// path handles this in the hot compositing loop).</summary>
    private static int[] BoxBlur2D(int[] src, int width, int height, int radius)
    {
        var horizontal = new int[width * height];
        BoxBlur1D(src, horizontal, width, height, radius, horizontalPass: true);
        var vertical = new int[width * height];
        BoxBlur1D(horizontal, vertical, width, height, radius, horizontalPass: false);
        return vertical;
    }

    /// <summary>Each row (horizontal pass) / column (vertical pass) is an
    /// independent sliding-window sum, so this parallelizes cleanly across rows
    /// or columns with no shared mutable state between iterations.</summary>
    private static void BoxBlur1D(int[] src, int[] dst, int width, int height, int radius, bool horizontalPass)
    {
        int windowSize = radius * 2 + 1;
        if (horizontalPass)
        {
            Parallel.For(0, height, y =>
            {
                int rowStart = y * width;
                int sum = 0;
                for (int x = -radius; x <= radius; x++)
                {
                    sum += src[rowStart + Math.Clamp(x, 0, width - 1)];
                }
                for (int x = 0; x < width; x++)
                {
                    dst[rowStart + x] = sum / windowSize;
                    int addIndex = Math.Clamp(x + radius + 1, 0, width - 1);
                    int removeIndex = Math.Clamp(x - radius, 0, width - 1);
                    sum += src[rowStart + addIndex] - src[rowStart + removeIndex];
                }
            });
        }
        else
        {
            Parallel.For(0, width, x =>
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++)
                {
                    sum += src[Math.Clamp(y, 0, height - 1) * width + x];
                }
                for (int y = 0; y < height; y++)
                {
                    dst[y * width + x] = sum / windowSize;
                    int addIndex = Math.Clamp(y + radius + 1, 0, height - 1);
                    int removeIndex = Math.Clamp(y - radius, 0, height - 1);
                    sum += src[addIndex * width + x] - src[removeIndex * width + x];
                }
            });
        }
    }

    /// <summary>Blurs the WHOLE photo, not just an edge -- a simple depth-of-
    /// field-style soft-background effect. Applied to the photo before the
    /// avatar overlay is composited on top (see
    /// <see cref="CompositeOverlayOntoPhoto"/>), so the avatar itself stays
    /// sharp while the background behind it softens. A plain uniform box
    /// blur (see <see cref="BoxBlur2D"/>).
    /// <paramref name="amount"/> is 0..100, 0 = off. <paramref name="scale"/>
    /// lets a caller working on a downscaled preview buffer (see
    /// RenderCompositePreview's drag-time small buffer) shrink the pixel
    /// radius proportionally, so the live preview doesn't look more blurred
    /// than the eventual full-resolution result.</summary>
    /// <summary>Same blur CompositeOverlayOntoPhoto's GPU chain applies to the
    /// background, exposed for callers (decal behind-avatar compositing) that
    /// need the background already blurred BEFORE something is pasted onto
    /// it, so that something doesn't get blurred along with it.</summary>
    internal static void ApplyPhotoBlurInPlace(PixelBuffer buffer, double amount, double scale) =>
        ApplyPhotoBlur(buffer.Pixels, buffer.Stride, buffer.Width, buffer.Height, amount, scale);

    private static void ApplyPhotoBlur(byte[] pixels, int stride, int width, int height, double amount, double scale)
    {
        int radius = (int)Math.Round(Math.Clamp(amount, 0, 100) / 100.0 * MaxPhotoBlurRadius * scale);
        if (radius <= 0) return;

        int count = width * height;
        var b = new int[count];
        var g = new int[count];
        var r = new int[count];

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            int rowIndex = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x * 4;
                int idx = rowIndex + x;
                b[idx] = pixels[i];
                g[idx] = pixels[i + 1];
                r[idx] = pixels[i + 2];
            }
        });

        var blurredB = BoxBlur2D(b, width, height, radius);
        var blurredG = BoxBlur2D(g, width, height, radius);
        var blurredR = BoxBlur2D(r, width, height, radius);

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            int rowIndex = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x * 4;
                int idx = rowIndex + x;
                pixels[i] = (byte)Math.Clamp(blurredB[idx], 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(blurredG[idx], 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(blurredR[idx], 0, 255);
            }
        });
    }

    // Shared by both ApplySoftness and ApplySharpness -- they're the same
    // local-contrast operation (pixel vs. a small-radius blur of itself)
    // pushed in opposite directions, so they need to reference the same
    // blur to actually read as each other's inverse rather than just two
    // unrelated effects that happen to share a name.
    internal const int FinishDetailRadius = 2;

    internal const double MaxFadeFloor = 60.0;
    internal const double MaxFadeDesaturate = 0.2;

    internal const int MaxGlowRadius = 30;
    internal const double GlowThreshold = 0.6;

    internal const int MaxAberrationOffset = 8;

    internal const int MaxColorBleedRadius = 12;

    internal const int VhsGlitchSeed = 20260101;
    internal const int MaxGlitchBands = 4;
    internal const int MaxGlitchShift = 24;

    internal const int MaxClarityRadius = 30;

    /// <summary>How the drop shadow's color combines with whatever's already
    /// on the photo -- Multiply darkens/tints the existing pixel (the
    /// traditional "shadow" look), Normal paints a flat alpha-blended blob
    /// of the shadow color (ignores what's underneath), Additive brightens
    /// by adding the shadow color's light (a "glow" rather than a shadow,
    /// but offered as a creative option alongside the other two). Values
    /// match GpuDropShadow's own int encoding passed into
    /// DropShadowBlendShader -- HLSL shader fields can't be C# enums, so the
    /// GPU side receives <c>(int)</c> this instead.</summary>
    public enum DropShadowBlendMode { Multiply = 0, Normal = 1, Additive = 2 }

}
