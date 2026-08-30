using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Services;

/// <summary>ビットマップのコピー(原本ではない)へのピクセル単位調整。常に pristine な
/// ソースから作り直すので、繰り返し調整しても劣化しない。位置合わせモードの PNG
/// プレビューと合成モードの写真プレビューで共有(PNG だけアルファのみのエッジぼかしが
/// 追加で要る)。調整済み PNG のピクセル化と写真への合成もここで扱う。</summary>
public static class ImageAdjustment
{
    /// <summary>ソース画像を BGRA32 生バッファへ変換したもの。画像読み込み時に1回作り、
    /// 以降の調整で使い回す(スライダーの tick ごとに変換し直すのは無駄)。</summary>
    public sealed record PixelBuffer(byte[] Pixels, int Width, int Height, int Stride);

    /// <summary>per-pixel の色調整をまとめたもの。アバター画像のルックと背景写真の
    /// ルックが同じセットを使う(値は独立)。-100..100 のフィールドは 0 = 変化なし、
    /// Hue は度で 0 = 変化なし。</summary>
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

    /// <summary>平坦・不透明な単色バッファ。「背景なしで作成」ボタンが代替「写真」として
    /// 使い、合成パイプライン(切り抜き・配置・デカール・仕上げ)を無改造で回す。</summary>
    public static PixelBuffer CreateSolidColor(int width, int height, byte r, byte g, byte b)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        Parallel.For(0, height, y =>
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        });
        return new PixelBuffer(pixels, width, height, stride);
    }

    /// <summary>2色の線形グラデーションバッファ。<see cref="CreateSolidColor"/> と
    /// 同じ用途(背景なしキャンバスの代替「写真」)だが、<paramref name="angleDegrees"/>
    /// に沿って color1 → color2(0 = 上→下、正 = 時計回り)。仕上げエフェクトの
    /// トーングラデーション(GpuToneGradient)とは別で、既存写真の上に重ねるのではなく
    /// キャンバス自体を塗る。</summary>
    public static PixelBuffer CreateLinearGradient(int width, int height,
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, double angleDegrees)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        double rad = angleDegrees * Math.PI / 180.0;
        double axisX = -Math.Sin(rad), axisY = Math.Cos(rad);
        double cx = width / 2.0, cy = height / 2.0;
        double halfExtent = Math.Abs(cx * axisX) + Math.Abs(cy * axisY);
        if (halfExtent <= 0) halfExtent = 1;

        Parallel.For(0, height, y =>
        {
            int rowStart = y * stride;
            double ry = y + 0.5 - cy;
            for (int x = 0; x < width; x++)
            {
                double rx = x + 0.5 - cx;
                double t = Math.Clamp((rx * axisX + ry * axisY) / halfExtent / 2 + 0.5, 0, 1);
                int i = rowStart + x * 4;
                pixels[i] = (byte)Math.Round(b1 + (b2 - b1) * t);
                pixels[i + 1] = (byte)Math.Round(g1 + (g2 - g1) * t);
                pixels[i + 2] = (byte)Math.Round(r1 + (r2 - r1) * t);
                pixels[i + 3] = 255;
            }
        });
        return new PixelBuffer(pixels, width, height, stride);
    }

    /// <summary>最近傍で縮小したコピー。長辺を <paramref name="maxDimension"/> で
    /// 頭打ちにする。ComputeDominantClusters の k-means サンプリングを安く保つため
    /// (ライブプレビュー用ではない)。既に十分小さければ原本をそのまま返す。</summary>
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

    /// <summary>写真全体を時計回りに 90° 回転(幅高さ入れ替え)。配置カードの回転
    /// ボタン用。ソース (x, y) は回転後 (H×W) バッファの (H-1-y, x) へ。</summary>
    public static PixelBuffer RotateClockwise90(PixelBuffer source)
    {
        int width = source.Width, height = source.Height;
        int newWidth = height, newHeight = width;
        int newStride = newWidth * 4;
        var dst = new byte[newStride * newHeight];

        Parallel.For(0, height, y =>
        {
            int srcRowBase = y * source.Stride;
            int dstX = newWidth - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int srcIndex = srcRowBase + x * 4;
                int dstIndex = x * newStride + dstX * 4;
                dst[dstIndex] = source.Pixels[srcIndex];
                dst[dstIndex + 1] = source.Pixels[srcIndex + 1];
                dst[dstIndex + 2] = source.Pixels[srcIndex + 2];
                dst[dstIndex + 3] = source.Pixels[srcIndex + 3];
            }
        });

        return new PixelBuffer(dst, newWidth, newHeight, newStride);
    }

    /// <summary>エッジぼかしステージのみ(重い部分): 切り抜きのシルエットを柔らかく
    /// する。色調整から分離してあるので、結果をキャッシュする呼び出し側は半径が
    /// 変わった時だけ再実行すればよい(明るさ等の調整では不要)。</summary>
    public static PixelBuffer BlurPng(PixelBuffer original, double edgeBlurRadius)
    {
        if (edgeBlurRadius <= 0) return original;
        var pixels = (byte[])original.Pixels.Clone();
        GpuAvatarEdgeBlur.TryApply(pixels, original.Stride, original.Width, original.Height, edgeBlurRadius);
        return original with { Pixels = pixels };
    }

    /// <summary>色ステージのみ: (任意で)ぼかし済みバッファへの色調整。ぼかし直しなしで
    /// スライダーの tick ごとに再実行できる程度に安い。</summary>
    public static WriteableBitmap ApplyColor(PixelBuffer buffer, ColorAdjustments adjustments)
    {
        var pixels = (byte[])buffer.Pixels.Clone();
        GpuColorAdjustments.TryAdjustColors(pixels, buffer.Stride, buffer.Width, buffer.Height, adjustments);

        var bitmap = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, buffer.Width, buffer.Height), pixels, buffer.Stride, 0);
        return bitmap;
    }

    /// <summary>ApplyColor と同じ色調整だが、WriteableBitmap で包まず生の
    /// <see cref="PixelBuffer"/> を返す。「ルック一致」ボタンのバックグラウンド計算用
    /// (WriteableBitmap は生成スレッドに束縛される DispatcherObject なので、Task.Run で
    /// 作ると後で例外/誤動作する)。</summary>
    public static PixelBuffer ApplyColorToPixelBuffer(PixelBuffer buffer, ColorAdjustments adjustments, double photoBlurAmount = 0)
    {
        var pixels = (byte[])buffer.Pixels.Clone();
        AdjustColors(pixels, buffer.Stride, buffer.Width, buffer.Height, adjustments);
        if (photoBlurAmount > 0) ApplyPhotoBlur(pixels, buffer.Stride, buffer.Width, buffer.Height, photoBlurAmount, 1.0);
        return buffer with { Pixels = pixels };
    }

    // ---- 「ルック一致」統計: per-pixel やニューラルではなく、集計した色統計を
    //      比較して一方のルックをもう一方へ寄せる(SolveMatchAdjustments)。 ----

    /// <summary>1レイヤーの画素(アバターは不透明部のみ)の集計色統計。
    /// <see cref="SolveMatchAdjustments"/> がスライダー値を導くのに使う。生の合計を
    /// 保持し(除算前)、領域ごとの平均/重みを再走査なしで遅延計算できるようにしている。</summary>
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

        /// <summary>彩度加重の円環平均色相(度)。<see cref="HueWeightSum"/> が
        /// 無視できない時だけ意味がある(ほぼグレーの画像には合わせる色相が無い)。</summary>
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

    /// <summary><paramref name="buffer"/> を1回走査し、<see cref="SolveMatchAdjustments"/>
    /// が要るものを全部計算する。<paramref name="maskByAlpha"/> は透明/半透明画素
    /// (alpha &lt; 128)をスキップする(アバター切り抜きの透明部が統計を歪めないように)。
    /// Match ボタンごとに1回だけの走査なので、行ごとにローカル集計してマージするリダクション形。</summary>
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

                    // AdjustColors と同じ階調領域の重み付け。ここで合わせる領域が
                    // Highlights/Shadows/Whites/Blacks が実際に動かす領域と一致するように。
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

    /// <summary><paramref name="source"/> の(未調整の)ルックを、このアプリの
    /// パイプラインでできる範囲で <paramref name="target"/>(もう一方のレイヤーの
    /// 現在の調整済みルック)へ寄せるスライダー値を求める。明るさ+コントラストは
    /// 輝度の平均/標準偏差のアフィン一致(Reinhard らの "Color Transfer between Images"
    /// と同じ発想を、このアプリの コントラスト→明るさ の順に写したもの)。彩度は
    /// 平均 HSL 彩度の比一致(Vibrance は 0 のまま。肌色減衰の非線形カーブは素直に
    /// 解けず、彩度だけで同じ範囲を張れるため)。色温度/色かぶりはグレーワールドの
    /// ホワイトバランス近似。Hue は彩度加重の円環平均色相角の差。ハイライト/シャドウ/
    /// 白/黒レベルは各階調領域の残差を個別に詰める(SolveToneRegion。明るさ/コントラストが
    /// その領域平均に既に効く分を差し引いてから)。</summary>
    public static ColorAdjustments SolveMatchAdjustments(LookStats source, LookStats target)
    {
        double sourceStd = Math.Max(source.StdLuma, 1e-3);
        double contrastFactor = Math.Clamp(target.StdLuma / sourceStd, 0.1, 4.0);
        double contrast = Math.Clamp((contrastFactor - 1.0) * 100.0, -100, 100);
        // クランプ後のスライダー値から係数を出し直す。明るさ解(と下の階調領域解)が
        // クランプ前の理想値ではなく実際に適用される値と一致するように。
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

    /// <summary>ハイライト/シャドウ/白/黒レベル解の1領域ぶん。まず
    /// <paramref name="sourceRegionMean"/> を上で解いた コントラスト+明るさ 変換に
    /// 通し、残差を領域スライダーで詰める。スケールは
    /// <paramref name="sourceAvgWeight"/>(その領域の重みがどれだけ自分に集中しているか)
    /// と <paramref name="maxAmt"/>(ハイライト/シャドウ=130、白/黒=150。AdjustColors の定数)。</summary>
    private static double SolveToneRegion(double sourceRegionMean, double sourceAvgWeight, double targetRegionMean, double contrastFactor, double brightnessOffset255, double maxAmt)
    {
        if (sourceAvgWeight < 1e-4) return 0;
        double intermediateMean = 128 + (sourceRegionMean - 128) * contrastFactor + brightnessOffset255;
        double amt = (targetRegionMean - intermediateMean) / sourceAvgWeight;
        return Math.Clamp(amt / maxAmt * 100.0, -100, 100);
    }

    // ---- クラスタ版「ルック一致」: 上の SolveMatchAdjustments の改良版。画像全体の
    //      2モーメント(平均・標準偏差)だけを合わせる代わりに、各レイヤーを Lab 空間の
    //      k-means++ で k=4 の主要色へ落とし、各ソースクラスタを最近傍のターゲット
    //      クラスタとペアにして、そのアンカー点上の加重最小二乗で同じスライダー値を
    //      当てる。多峰の色分布(肌/髪/服 対 空/地面)に対して単一平均より頑健。 ----

    private static double SrgbToLinear(double c8)
    {
        double c = c8 / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private const double LabEpsilon = 0.008856; // (6/29)^3
    private const double LabKappaDenom = 0.128418; // 3*(6/29)^2

    private static double LabF(double t) => t > LabEpsilon ? Math.Cbrt(t) : t / LabKappaDenom + 4.0 / 29.0;

    /// <summary>sRGB (0..255) → CIE L*a*b*(D65)。主要色のクラスタリング/ペアリングの
    /// 知覚的に均一な距離尺度として使うだけ(描画用ではない)。</summary>
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

    /// <summary>主要色クラスタ1つ(<see cref="ComputeDominantClusters"/>): Lab 重心
    /// (距離/ペアリング用)と、<see cref="LookStats"/> が全体で追うのと同じ集計統計
    /// (平均輝度・彩度・色相・WB チャンネル差)。注記以外はクラスタの画素重みで除算済み。</summary>
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

    /// <summary>クラスタリング前に縮小する長辺。k-means は代表的な主要色が取れる
    /// ぶんのサンプルがあれば十分で、数百万点で回しても Match のたびに重くなるだけ。</summary>
    private const int ClusterSampleMaxDimension = 200;

    /// <summary><paramref name="buffer"/> を Lab 空間の k-means++ で <paramref name="k"/>
    /// 個の主要色へ落とす(seed 固定なので同じ画像で毎回同じ結果)。distinct サンプルが
    /// k 未満、または最終割り当てで空クラスタになった場合は k 未満を返す。</summary>
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

        // k-means++ 初期化: 最初の重心は一様ランダム、以降は「これまでの最近傍重心
        // までの二乗距離」に比例する確率で選ぶ。初期重心が色分布に散る。
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

        // Lloyd 法: 割り当て → 再計算 を割り当てが変わらなくなるまで(k=4 なら 12 パス未満で収束)。
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
                // 空クラスタは NaN にせず前の重心を保つ。以降のイテレーションで
                // メンバーが付かなければ、最後の weight=0 フィルタで捨てられる。
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

    /// <summary>クラスタペアリングの信頼度の Lab 距離減衰スケール。ペアにはなったが
    /// 遠いクラスタ(最近傍でも実際は近くない)は、近いペアより fit への寄与を下げる。</summary>
    private const double MatchPairConfidenceScale = 40.0;

    // 解いた「理想」一致値をユーザーに見せる前にかけるスライダーごとの減衰。
    // コントラスト/色温度/色かぶり/彩度 は強めに抑える(小さな統計差で大きく振れる)、
    // それ以外(明るさ/Hue/階調領域)はほぼフル。
    private const double MatchContrastStrength = 0.3;
    private const double MatchColorBalanceStrength = 0.2; // 色温度・色かぶり・彩度
    private const double MatchMinorStrength = 0.3; // 明るさ/色相/トーン領域

    /// <summary><see cref="SolveMatchAdjustments"/> のクラスタ版。各
    /// <paramref name="sourceClusters"/> を Lab 距離で最近傍の
    /// <paramref name="targetClusters"/> とペアにし(複数のソースが同じターゲットに
    /// ペアしてよい)、明るさ/コントラスト/彩度/Hue をそのアンカー点上の加重最小二乗で
    /// 当てる(重みは画素数 × ペアリングの近さ、<see cref="MatchPairConfidenceScale"/>)。
    /// 色温度/色かぶりはクラスタペアを使わず(WB はシーン全体の性質)、色相バンド概念の
    /// ハイライト/シャドウ/白/黒レベルも <see cref="SolveMatchAdjustments"/> と同じ。
    /// どちらかにクラスタが無ければ <see cref="SolveMatchAdjustments"/> へフォールバック。
    /// 返り値は上の減衰定数でスケールダウンする(解いた値は上限扱い)。</summary>
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

            // ---- 明るさ+コントラスト: ペア (sourceLuma-128, targetLuma-128) を通る
            //      加重最小二乗の直線。SolveMatchAdjustments の 2モーメント一致を
            //      k アンカー点へ一般化したもの。 ----
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

            // ---- 彩度: 原点を通る加重回帰(彩度スライダーは乗算項のみ)。重みは上と同じ。 ----
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

            // ---- 色温度/色かぶり: クラスタペアではなく素の全体平均差(理由はメソッド doc)。 ----
            double tempShift = (targetRegionStats.MeanRMinusB - sourceRegionStats.MeanRMinusB) / 2.0;
            double temperature = Math.Clamp(tempShift / 40.0 * 100.0, -100, 100);
            double tintShift = targetRegionStats.MeanGOffset - sourceRegionStats.MeanGOffset;
            double tint = Math.Clamp(tintShift / 40.0 * 100.0, -100, 100);

            // ---- Hue: 各ペアの色相差の円環加重平均。重みはペアリング信頼度 × ペアのうち
            //      彩度信頼度が低い方(HueWeightSum)。どちらかがほぼグレーだと無意味。 ----
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

    /// <summary>(ルック調整済みの)オーバーレイ PNG を指定の画面サイズ/回転/不透明度で
    /// 実ピクセルへ描く。ライブのオーバーレイ表示と一致する。回転後のバウンディング
    /// ボックスぶんパディングしたキャンバスに描くので、回転した角が切れない。返り値の
    /// オフセットは、パディング済みキャンバス左上が未回転の配置矩形左上からどれだけ
    /// ずれているか(写真上に正しく置くのに使う)。</summary>
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
        // Frozen: 以降の合成パイプラインをバックグラウンドスレッドへ渡せるように
        // (このメソッド自体は WPF ビジュアルを描くので UI スレッドが要る。下流は不要)。
        bitmap.Freeze();
        return (bitmap, offsetX, offsetY);
    }

    /// <summary>写真に写真レベルの色調整をかけ、指定位置に描画済みオーバーレイ
    /// (<see cref="RenderOverlayForComposite"/>)をアルファブレンドし、最後に合成結果
    /// 全体へ仕上げエフェクトをかける。<paramref name="overlayLeft"/>/
    /// <paramref name="overlayTop"/> はパディング済みオーバーレイ左上の写真ピクセル座標
    /// (レンダーの OffsetX/OffsetY を含む)。<paramref name="overlayPixels"/> は任意で、
    /// null(アバター未読込)ならブレンドを丸ごとスキップして写真だけに仕上げが乗る。
    /// BitmapSource ではなく生 BGRA32 を受け取るので、このメソッドは byte[]/PixelBuffer/
    /// プリミティブだけを触り、どのスレッドでも安全に走る。</summary>
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
        // 下のエフェクト連鎖(色調整・写真ぼかし・ドロップシャドウ・アバターブレンド・
        // 仕上げ各種・トーングラデ・色収差/カラーブリード・走査線・ビネット・グレイン)は
        // GpuCompositeChain 経由で GPU 往復1回にまとめて走る。photo.Pixels は SOURCE として
        // (クローンせず)渡すので、効果量だけ変わった回はアップロードを省ける
        // (GpuTexturePool.RentUploaded)。`pixels` は GPU ダウンロードで全上書きされるので
        // 未初期化で始めてよい。
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
        // Frozen: バックグラウンドスレッドで構築して返せるように。
        result.Freeze();
        return result;
    }

    /// <summary>切り抜き5パラメータ(アスペクト比 / 幅% / 高さ% / 位置X% / 位置Y%)
    /// からソース内の切り抜き矩形を求める。<see cref="CropToAspect"/> が実際に切り
    /// 出す矩形と、ハンドル/ガイド描画用の ControlPanelWindow.GetCanvasCropRect が
    /// 必ず一致するよう、計算はここ1箇所に集約する。</summary>
    public static (int Left, int Top, int Width, int Height) ComputeCropRect(
        int srcWidth, int srcHeight, double? aspectRatio,
        double offsetXPercent, double offsetYPercent, double widthPercent = 100, double heightPercent = 100)
    {
        if (srcWidth <= 0 || srcHeight <= 0) return (0, 0, Math.Max(0, srcWidth), Math.Max(0, srcHeight));

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
            // 比率固定モード: 同じズーム係数で両軸をスケール(上で比率フィット済み)
            // なので、どのズームでも比率が保たれる。widthPercent が両軸を駆動する。
            heightZoomPercent = widthPercent;
        }
        else
        {
            // 自由: 各軸の 100% がソース全体で、2つのノブは独立(保つ比率が無い)。
            maxCropWidth = srcWidth;
            maxCropHeight = srcHeight;
            heightZoomPercent = heightPercent;
        }

        // widthPercent/heightPercent は切り抜き枠を 100% より縮める(アス比選択の上に
        // 乗るその場ズームのノブ)。
        double widthZoom = Math.Clamp(widthPercent, 1, 100) / 100.0;
        double heightZoom = Math.Clamp(heightZoomPercent, 1, 100) / 100.0;
        int cropWidth = Math.Max(1, (int)Math.Round(maxCropWidth * widthZoom));
        int cropHeight = Math.Max(1, (int)Math.Round(maxCropHeight * heightZoom));

        int left = (int)Math.Round((srcWidth - cropWidth) * Math.Clamp(offsetXPercent, 0, 100) / 100.0);
        int top = (int)Math.Round((srcHeight - cropHeight) * Math.Clamp(offsetYPercent, 0, 100) / 100.0);
        return (left, top, cropWidth, cropHeight);
    }

    public static WriteableBitmap CropToAspect(WriteableBitmap source, double? aspectRatio, double offsetXPercent, double offsetYPercent, double widthPercent = 100, double heightPercent = 100)
    {
        int srcWidth = source.PixelWidth, srcHeight = source.PixelHeight;
        if (srcWidth <= 0 || srcHeight <= 0) return source;

        var (left, top, cropWidth, cropHeight) = ComputeCropRect(
            srcWidth, srcHeight, aspectRatio, offsetXPercent, offsetYPercent, widthPercent, heightPercent);
        if (cropWidth == srcWidth && cropHeight == srcHeight) return source;

        var format = source.Format;
        int bytesPerPixel = (format.BitsPerPixel + 7) / 8;
        int cropStride = cropWidth * bytesPerPixel;
        var buffer = new byte[cropStride * cropHeight];
        source.CopyPixels(new Int32Rect(left, top, cropWidth, cropHeight), buffer, cropStride, 0);

        var result = new WriteableBitmap(cropWidth, cropHeight, source.DpiX, source.DpiY, format, null);
        result.WritePixels(new Int32Rect(0, 0, cropWidth, cropHeight), buffer, cropStride, 0);
        // Frozen: CompositeOverlayOntoPhoto の結果と同じくクロススレッド安全のため
        // (同じパイプラインの最後のステップ)。
        result.Freeze();
        return result;
    }

    /// <summary>同サイズの2つの合成結果を、比較用スライダー1枚の画像に合成する。
    /// <paramref name="splitFraction"/>(0..1)より左は <paramref name="before"/>、
    /// 以降は <paramref name="after"/>。行ごとの byte コピーだけ(ピクセル演算なし)なので
    /// スライダーの tick ごとに再実行できる。</summary>
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

    /// <summary>エッジぼかしの境界検出で画素を「不透明」とみなすアルファ閾値
    /// (0..255)。素朴な 50% 中点ではなく 0 に近い値にしてある: 中点だと半透明の
    /// デザイン要素(半透明バイザー等)が「背景」に分類され、その縁にも人工的な
    /// 境界ができて、幅が狭ければ全面がぼける。ほぼ完全透明だけを「外」とすれば、
    /// 半透明の内部ディテールは触られず、真のシルエット境界(完全に 0 まで落ちる)は
    /// 正しく検出される。GpuAvatarEdgeBlur にも同じ閾値のコピーがある(要同期)。</summary>
    internal const int EdgeBlurForegroundAlphaThreshold = 10;


    /// <summary>各画素の RGB(アルファは不変)に、ホワイトバランス(色温度/色かぶり)→
    /// 色相回転 → 自然な彩度 → 彩度 → コントラスト → 明るさ の順で適用する
    /// (写真編集ソフトと概ね同じ順。色被りを直してから調子を整える)。</summary>
    private static void AdjustColors(byte[] pixels, int stride, int width, int height, ColorAdjustments adj)
    {
        if (adj.IsIdentity) return;

        double satFactor = 1 + adj.Saturation / 100.0;
        double contrastFactor = 1 + adj.Contrast / 100.0;
        double brightnessOffset = adj.Brightness / 100.0 * 255.0;
        double tempShift = adj.Temperature / 100.0 * 40.0;
        double tintShift = adj.Tint / 100.0 * 40.0;
        // 0.65 係数: 素の (1-s) だけだと vibranceAmt=1 で全画素が s=1 まで飽和して
        // しまい、彩度=100 より遥かに強い。0.65 で「彩度=100 と同程度の強さ」に寄せる。
        double vibranceAmt = adj.Vibrance / 100.0 * 0.65;

        // ハイライト/シャドウ/白/黒レベル: ハードな階調分割ではなく、輝度ベースの
        // なめらかな重み(0..1)でスケールした加算シフト。シャドウ/ハイライトは広く緩い
        // ランプ、白/黒はそれを二乗して極端側へ集中させる。大きさは明るさ最大と同程度に
        // 感じるよう手調整(物理的根拠は無い)。
        bool useToneRegions = adj.Highlights != 0 || adj.Shadows != 0 || adj.Whites != 0 || adj.Blacks != 0;
        double highlightsAmt = adj.Highlights / 100.0 * 130.0;
        double shadowsAmt = adj.Shadows / 100.0 * 130.0;
        double whitesAmt = adj.Whites / 100.0 * 150.0;
        double blacksAmt = adj.Blacks / 100.0 * 150.0;

        // ティント: 固定 RGB への平坦な lerp ではなく、輝度を保つ色被せ。ターゲット色を
        // 各画素の輝度で先にスケール(targetR = ColorTintR * lum01)するので、シャドウは
        // 暗い版・ハイライトは明るい版へ寄り、ColorTintStrength=100 でも階調が潰れない
        // (セピア調と同じ発想を任意色へ一般化)。
        bool useColorTint = adj.ColorTintStrength != 0;
        double colorTintT = adj.ColorTintStrength / 100.0;

        // 色相回転行列(グレー/輝度軸まわりの回転)。ピクセルループ外で1回だけ算出。
        // CSS hue-rotate() / SVG feColorMatrix hueRotate の標準係数。
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
                    // 本物の HSL 彩度調整。くすんだ画素ほど強く、鮮やかな画素ほど弱く
                    // 彩度を上げる(Adobe の Vibrance の定義)。さらに肌色近辺の色相では
                    // 効果を減衰する(肌は元々彩度が低く、ポートレートで一番強くブーストされて
                    // 顔から過飽和になりがちなため)。床 0.5 は、この減衰が効き過ぎて
                    // Vibrance=100 でも肌色画素がほぼ動かない、という状態を防ぐ。
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

    /// <summary>典型的な肌色の概略色相(度、オレンジ寄り)。Vibrance の減衰に使う。</summary>
    private const double SkinHueDegrees = 30.0;

    /// <summary>標準の smoothstep: <paramref name="edge0"/> 以下で 0、
    /// <paramref name="edge1"/> 以上で 1、間はイージング。</summary>
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

    // ---- 仕上げエフェクト: 最終合成結果に1回だけ適用(レイヤーごとではない)。
    //      同じアバターで複数写真を合成しても、出力が一貫した「フィルムで撮った」
    //      見た目になる(各レイヤーからグレイン/ビネットが重畳しない)。 ----

    internal const int GrainSeed = 20260101;

    /// <summary><see cref="GenerateArNoise"/> のキャッシュ。ノイズ場は
    /// width/height/seed で完全に決まる(写真ごとに固定)ので、毎レンダー作り直すのは無駄。</summary>
    private static readonly Dictionary<(int Width, int Height, int Seed), double[]> GrainNoiseCache = new();

    /// <summary>指定サイズのグレインノイズ場を前もって作りキャッシュする。そのサイズの
    /// 初回レンダーでコストを払わないため。写真読み込みごとに1回呼ぶ。</summary>
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

    /// <summary>自己回帰ノイズ場: 各サンプルはハッシュ乱数インパルスを、生成済みの
    /// 左/上の近傍へ寄せたもの(AV1 のフィルムグレイン合成と同じ AR モデルの発想)。
    /// per-pixel の砂嵐ではなく小さな有機的な塊になる。前の列/行に依存するラスタ走査
    /// なので、このファイルの他と違い行並列化できない。</summary>
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

    /// <summary>整数座標からの安い決定的な擬似ノイズ。per-pixel 並列コードが Random を
    /// 共有できない箇所の代わり。戻り値は -1..1。</summary>
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

    /// <summary>四隅へ向かって放射状に暗くする。<paramref name="amount"/> は 0..100、0 = オフ。</summary>
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

    /// <summary>単一 int チャンネルに対する分離2パス(横→縦)ボックスブラー。
    /// ApplyPhotoBlur のブラープリミティブ(GPU 経路がホットループを担う今、
    /// 呼び出し元は ApplyColorToPixelBuffer のみ)。</summary>
    private static int[] BoxBlur2D(int[] src, int width, int height, int radius)
    {
        var horizontal = new int[width * height];
        BoxBlur1D(src, horizontal, width, height, radius, horizontalPass: true);
        var vertical = new int[width * height];
        BoxBlur1D(horizontal, vertical, width, height, radius, horizontalPass: false);
        return vertical;
    }

    /// <summary>行(横パス)/列(縦パス)がそれぞれ独立のスライディング窓和なので、
    /// 反復間で共有可変状態なしで行/列並列化できる。</summary>
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

    /// <summary>エッジではなく写真全体をぼかす、簡易的な被写界深度風の背景ソフト効果。
    /// アバターを重ねる前に写真へ適用するので、アバターはシャープなまま背景が柔らかくなる。
    /// <paramref name="amount"/> は 0..100、0 = オフ。<paramref name="scale"/> は縮小
    /// プレビューバッファで作業する呼び出し元がピクセル半径を比例縮小するためのもの。
    ///
    /// デカール(アバター背面)合成のように、何かを貼る「前」に背景をぼかしておきたい
    /// 呼び出し元向けに公開する。</summary>
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

    // ApplySoftness と ApplySharpness の共通。両者は同じローカルコントラスト操作
    // (画素 vs 小半径ぼかし)を逆方向に押すだけなので、同じぼかしを参照しないと
    // 互いの逆として成立しない。
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

    /// <summary>ドロップシャドウの色を写真既存ピクセルへどう合成するか。Multiply は
    /// 既存を暗く/色付け(伝統的な「影」)、Normal は影色を平坦にアルファブレンド
    /// (下地無視)、Additive は影色の光を足して明るく(影というより「グロー」だが
    /// 創作用に用意)。値は GpuDropShadow が DropShadowBlendShader へ渡す int と一致
    /// (HLSL は enum 不可なので GPU 側は <c>(int)</c> で受ける)。</summary>
    public enum DropShadowBlendMode { Multiply = 0, Normal = 1, Additive = 2 }

}
