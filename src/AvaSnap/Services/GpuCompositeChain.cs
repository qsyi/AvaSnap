using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>CompositeOverlayOntoPhoto の GPU エフェクト連鎖(色調補正・写真ぼかし・
/// ドロップシャドウ・アバターブレンド・ソフト/シャープ/クラリティ/フェード/グロー/
/// ライトリーク・トーングラデ・色収差/カラーブリード・走査線・ビネット・グレイン)を
/// 9ステージまとめて GPU 往復1回で走らせる: アップロード1回 → 同じ GPU 常駐テクスチャ上で
/// 全ステージ連続ディスパッチ → ダウンロード1回。各ステージの byte[] 版とは別に、
/// テクスチャへ直接かける ApplyToTexture/BlendIntoTexture を持たせてある。以前は
/// ステージごとに写真を GPU へ上げ下げしており、その転送が総時間の大半を占めていた。
///
/// さらに、各ステージ後の結果をチェックポイント(GPU 同士の安いコピー)し、それを
/// 生んだ入力を覚えておく。スライダードラッグ中は1ステージの入力しか変わらないので、
/// <see cref="TryRun"/> は前回と入力が一致する最後の境界を探し、生の写真ではなく
/// そのチェックポイントからテクスチャを復元して、そこから先だけディスパッチする。
/// 写真/アバターの差し替えや連鎖前方のパラメータ変更は従来どおり下流を全無効化する。
///
/// このキャッシュは static・プロセス寿命・1スロット(「前回の入力」だけが鍵)。
/// GpuTexturePool と同じ前提で、GPU パイプライン呼び出しは _compositeRenderGate が
/// 直列化するので追加の同期は無し。</summary>
public static class GpuCompositeChain
{
    private const int StageCount = 9;

    /// <summary>TryRun が受け取る、最終ピクセルに影響し得る全パラメータを連鎖順に
    /// まとめたもの。byte[] フィールドは内容ではなく参照で比較する(丸ごと差し替え
    /// しかしない前提。record の自動生成 Equals は配列を参照比較するので、これが
    /// 手書きの深い比較より正しい道具になる)。</summary>
    private sealed record ChainInputs(
        byte[] Photo, int PhotoWidth, int PhotoHeight, ImageAdjustment.ColorAdjustments ColorAdj, int BlurRadius,
        byte[]? Overlay, int OverlayStride, int OverlayWidth, int OverlayHeight, int OverlayLeft, int OverlayTop,
        double DropShadowAmount, double DropShadowDirection, double DropShadowDistance, double DropShadowBlur,
        byte DropShadowColorB, byte DropShadowColorG, byte DropShadowColorR, double DropShadowScale,
        bool DropShadowTone, double DropShadowDotSize, int DropShadowBlendMode,
        double SoftnessAmount, double SharpnessAmount, double FinishDetailScale,
        double ClarityAmount, double ClarityScale, double FadeAmount,
        double GlowAmount, double GlowScale,
        double LightLeakAmount, double LightLeakAngle, double LightLeakDistance,
        byte LightLeakColorB, byte LightLeakColorG, byte LightLeakColorR,
        double SkinWbAmount, byte SkinWbR, byte SkinWbG, byte SkinWbB,
        double ToneGradientAmount, double ToneGradientRotation,
        byte ToneGradientLightR, byte ToneGradientLightG, byte ToneGradientLightB,
        byte ToneGradientDarkR, byte ToneGradientDarkG, byte ToneGradientDarkB,
        double ChromaticAberrationAmount, double ColorBleedAmount, double VhsScale,
        double ScanlineAmount, double VignetteAmount, double GrainAmount);

    private static ChainInputs? _lastInputs;

    /// <summary>前回と入力が変わった最初のステージ番号(0..StageCount)を返す。その手前は
    /// 前回と同じ出力なのでチェックポイントから復元できる。パラメータ単位ではなく
    /// ステージ単位の粗さ(例: ドロップシャドウ系のどれを変えても stage 1・2 をまとめて
    /// 無効化)。全入力一致なら StageCount(前回の最終結果をそのまま使える)。</summary>
    private static int FirstDifferingStage(ChainInputs? prev, ChainInputs cur)
    {
        if (prev is null) return 0;

        if (!ReferenceEquals(prev.Photo, cur.Photo) || prev.PhotoWidth != cur.PhotoWidth || prev.PhotoHeight != cur.PhotoHeight
            || prev.ColorAdj != cur.ColorAdj || prev.BlurRadius != cur.BlurRadius)
        {
            return 0;
        }

        if (!ReferenceEquals(prev.Overlay, cur.Overlay) || prev.OverlayStride != cur.OverlayStride
            || prev.OverlayWidth != cur.OverlayWidth || prev.OverlayHeight != cur.OverlayHeight
            || prev.OverlayLeft != cur.OverlayLeft || prev.OverlayTop != cur.OverlayTop
            || prev.DropShadowAmount != cur.DropShadowAmount || prev.DropShadowDirection != cur.DropShadowDirection
            || prev.DropShadowDistance != cur.DropShadowDistance || prev.DropShadowBlur != cur.DropShadowBlur
            || prev.DropShadowColorB != cur.DropShadowColorB || prev.DropShadowColorG != cur.DropShadowColorG
            || prev.DropShadowColorR != cur.DropShadowColorR || prev.DropShadowScale != cur.DropShadowScale
            || prev.DropShadowTone != cur.DropShadowTone || prev.DropShadowDotSize != cur.DropShadowDotSize
            || prev.DropShadowBlendMode != cur.DropShadowBlendMode)
        {
            return 1;
        }

        // stage 2 (AvatarBlend) は上で確認したオーバーレイ以外に固有の入力を持たない
        // ので、ここまで来たなら有効。最初の差分ステージになることは無い。

        if (prev.SoftnessAmount != cur.SoftnessAmount || prev.SharpnessAmount != cur.SharpnessAmount
            || prev.FinishDetailScale != cur.FinishDetailScale || prev.ClarityAmount != cur.ClarityAmount
            || prev.ClarityScale != cur.ClarityScale || prev.FadeAmount != cur.FadeAmount
            || prev.GlowAmount != cur.GlowAmount || prev.GlowScale != cur.GlowScale
            || prev.LightLeakAmount != cur.LightLeakAmount || prev.LightLeakAngle != cur.LightLeakAngle
            || prev.LightLeakDistance != cur.LightLeakDistance || prev.LightLeakColorB != cur.LightLeakColorB
            || prev.LightLeakColorG != cur.LightLeakColorG || prev.LightLeakColorR != cur.LightLeakColorR
            || prev.SkinWbAmount != cur.SkinWbAmount || prev.SkinWbR != cur.SkinWbR
            || prev.SkinWbG != cur.SkinWbG || prev.SkinWbB != cur.SkinWbB)
        {
            return 3;
        }

        if (prev.ToneGradientAmount != cur.ToneGradientAmount || prev.ToneGradientRotation != cur.ToneGradientRotation
            || prev.ToneGradientLightR != cur.ToneGradientLightR || prev.ToneGradientLightG != cur.ToneGradientLightG
            || prev.ToneGradientLightB != cur.ToneGradientLightB || prev.ToneGradientDarkR != cur.ToneGradientDarkR
            || prev.ToneGradientDarkG != cur.ToneGradientDarkG || prev.ToneGradientDarkB != cur.ToneGradientDarkB)
        {
            return 4;
        }

        if (prev.ChromaticAberrationAmount != cur.ChromaticAberrationAmount || prev.ColorBleedAmount != cur.ColorBleedAmount
            || prev.VhsScale != cur.VhsScale)
        {
            return 5;
        }

        if (prev.ScanlineAmount != cur.ScanlineAmount)
        {
            return 6;
        }

        if (prev.VignetteAmount != cur.VignetteAmount)
        {
            return 7;
        }

        if (prev.GrainAmount != cur.GrainAmount)
        {
            return 8;
        }

        return StageCount;
    }

    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="outputPixels"/> は不変)。
    /// <paramref name="photoPixels"/> は書き換えない pristine な写真バッファ
    /// (GpuCompositePipeline.TryRun と同じ契約。効果量だけ変わった回はアップロードを
    /// スキップできる)。<paramref name="overlayPixels"/> が null ならドロップシャドウと
    /// アバターブレンドを丸ごとスキップ。</summary>
    public static bool TryRun(
        byte[] photoPixels, byte[] outputPixels, int photoStride, int photoWidth, int photoHeight,
        ImageAdjustment.ColorAdjustments colorAdj, int photoBlurRadiusPixels,
        byte[]? overlayPixels, int overlayStride, int overlayWidth, int overlayHeight,
        double overlayLeft, double overlayTop,
        double dropShadowAmount, double dropShadowDirection, double dropShadowDistance, double dropShadowBlur,
        byte dropShadowColorB, byte dropShadowColorG, byte dropShadowColorR, double dropShadowScale,
        bool dropShadowTone, double dropShadowDotSize, int dropShadowBlendMode,
        double softnessAmount, double sharpnessAmount, double finishDetailScale,
        double clarityAmount, double clarityScale,
        double fadeAmount,
        double glowAmount, double glowScale,
        double lightLeakAmount, double lightLeakAngle, double lightLeakDistance,
        byte lightLeakColorB, byte lightLeakColorG, byte lightLeakColorR,
        double skinWbAmount, byte skinWbR, byte skinWbG, byte skinWbB,
        double toneGradientAmount, double toneGradientRotation,
        byte toneGradientLightR, byte toneGradientLightG, byte toneGradientLightB,
        byte toneGradientDarkR, byte toneGradientDarkG, byte toneGradientDarkB,
        double chromaticAberrationAmount, double colorBleedAmount, double vhsScale,
        double scanlineAmount,
        double vignetteAmount,
        double grainAmount)
    {
        if (photoStride != photoWidth * 4 || photoPixels.Length < photoStride * photoHeight || outputPixels.Length < photoStride * photoHeight) return false;
        if (GpuAvailability.Device is not { } device) return false;

        int left = 0, top = 0;
        if (overlayPixels is not null)
        {
            left = (int)Math.Round(overlayLeft);
            top = (int)Math.Round(overlayTop);
        }

        var cur = new ChainInputs(
            photoPixels, photoWidth, photoHeight, colorAdj, photoBlurRadiusPixels,
            overlayPixels, overlayStride, overlayWidth, overlayHeight, left, top,
            dropShadowAmount, dropShadowDirection, dropShadowDistance, dropShadowBlur,
            dropShadowColorB, dropShadowColorG, dropShadowColorR, dropShadowScale,
            dropShadowTone, dropShadowDotSize, dropShadowBlendMode,
            softnessAmount, sharpnessAmount, finishDetailScale,
            clarityAmount, clarityScale, fadeAmount,
            glowAmount, glowScale,
            lightLeakAmount, lightLeakAngle, lightLeakDistance, lightLeakColorB, lightLeakColorG, lightLeakColorR,
            skinWbAmount, skinWbR, skinWbG, skinWbB,
            toneGradientAmount, toneGradientRotation,
            toneGradientLightR, toneGradientLightG, toneGradientLightB,
            toneGradientDarkR, toneGradientDarkG, toneGradientDarkB,
            chromaticAberrationAmount, colorBleedAmount, vhsScale,
            scanlineAmount, vignetteAmount, grainAmount);

        try
        {
            Span<Bgra32> outputSpan = MemoryMarshal.Cast<byte, Bgra32>(outputPixels.AsSpan(0, photoStride * photoHeight));

            int firstStage = FirstDifferingStage(_lastInputs, cur);

            // チェックポイントも Rent なので寸法変更時のリサイズは GpuTexturePool 任せ。
            // 寸法変更時は firstStage が必ず 0 になるので、リサイズ直後の中身不定な
            // チェックポイントは読まれず stage 0 から書き直される。
            var checkpoints = new ReadWriteTexture2D<Bgra32, float4>[StageCount];
            for (int i = 0; i < StageCount; i++)
            {
                checkpoints[i] = GpuTexturePool.Rent(device, $"Chain.Checkpoint{i}", photoWidth, photoHeight);
            }

            if (firstStage >= StageCount)
            {
                // 前回から何も変わっていない ── 最終チェックポイントがそのまま答え。
                checkpoints[StageCount - 1].CopyTo(outputSpan);
                _lastInputs = cur;
                return true;
            }

            ReadWriteTexture2D<Bgra32, float4> main = GpuTexturePool.Rent(device, "Chain.Main", photoWidth, photoHeight);

            if (firstStage == 0)
            {
                ReadWriteTexture2D<Bgra32, float4> source = GpuTexturePool.RentUploaded(device, "Chain.Source", photoPixels, photoStride, photoWidth, photoHeight);
                source.CopyTo(main);
            }
            else
            {
                checkpoints[firstStage - 1].CopyTo(main);
            }

            for (int stage = firstStage; stage < StageCount; stage++)
            {
                if (!RunStage(stage, main, device, photoWidth, photoHeight,
                    overlayPixels, overlayStride, overlayWidth, overlayHeight, left, top,
                    colorAdj, photoBlurRadiusPixels,
                    dropShadowAmount, dropShadowDirection, dropShadowDistance, dropShadowBlur,
                    dropShadowColorB, dropShadowColorG, dropShadowColorR, dropShadowScale,
                    dropShadowTone, dropShadowDotSize, dropShadowBlendMode,
                    softnessAmount, sharpnessAmount, finishDetailScale, clarityAmount, clarityScale, fadeAmount,
                    glowAmount, glowScale, lightLeakAmount, lightLeakAngle, lightLeakDistance,
                    lightLeakColorB, lightLeakColorG, lightLeakColorR,
                    skinWbAmount, skinWbR, skinWbG, skinWbB,
                    toneGradientAmount, toneGradientRotation,
                    toneGradientLightR, toneGradientLightG, toneGradientLightB,
                    toneGradientDarkR, toneGradientDarkG, toneGradientDarkB,
                    chromaticAberrationAmount, colorBleedAmount, vhsScale,
                    scanlineAmount, vignetteAmount, grainAmount))
                {
                    return false;
                }

                // このステージのチェックポイントを更新。次回、下流だけ変わった呼び出しが
                // stage 0 ではなくここから復元できる。
                main.CopyTo(checkpoints[stage]);
            }

            main.CopyTo(outputSpan);
            _lastInputs = cur;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><paramref name="main"/> へステージ1つ(<see cref="FirstDifferingStage"/>
    /// が返すのと同じ 0..8 の番号)をディスパッチする。TryRun のループから切り出しただけ。</summary>
    private static bool RunStage(int stage, ReadWriteTexture2D<Bgra32, float4> main, GraphicsDevice device, int photoWidth, int photoHeight,
        byte[]? overlayPixels, int overlayStride, int overlayWidth, int overlayHeight, int left, int top,
        ImageAdjustment.ColorAdjustments colorAdj, int photoBlurRadiusPixels,
        double dropShadowAmount, double dropShadowDirection, double dropShadowDistance, double dropShadowBlur,
        byte dropShadowColorB, byte dropShadowColorG, byte dropShadowColorR, double dropShadowScale,
        bool dropShadowTone, double dropShadowDotSize, int dropShadowBlendMode,
        double softnessAmount, double sharpnessAmount, double finishDetailScale,
        double clarityAmount, double clarityScale, double fadeAmount,
        double glowAmount, double glowScale,
        double lightLeakAmount, double lightLeakAngle, double lightLeakDistance,
        byte lightLeakColorB, byte lightLeakColorG, byte lightLeakColorR,
        double skinWbAmount, byte skinWbR, byte skinWbG, byte skinWbB,
        double toneGradientAmount, double toneGradientRotation,
        byte toneGradientLightR, byte toneGradientLightG, byte toneGradientLightB,
        byte toneGradientDarkR, byte toneGradientDarkG, byte toneGradientDarkB,
        double chromaticAberrationAmount, double colorBleedAmount, double vhsScale,
        double scanlineAmount, double vignetteAmount, double grainAmount)
    {
        switch (stage)
        {
            case 0:
                GpuCompositePipeline.ApplyToTexture(main, device, photoWidth, photoHeight, colorAdj, photoBlurRadiusPixels);
                return true;

            case 1:
                if (overlayPixels is null || dropShadowAmount <= 0) return true;
                return GpuDropShadow.ApplyToTexture(main, device, photoWidth, photoHeight,
                    overlayPixels, overlayStride, overlayWidth, overlayHeight, left, top,
                    dropShadowAmount, dropShadowDirection, dropShadowDistance, dropShadowBlur,
                    dropShadowColorB, dropShadowColorG, dropShadowColorR, dropShadowScale,
                    dropShadowTone, dropShadowDotSize, dropShadowBlendMode);

            case 2:
                if (overlayPixels is null) return true;
                return GpuAvatarBlend.BlendIntoTexture(main, device, photoWidth, photoHeight,
                    overlayPixels, overlayStride, overlayWidth, overlayHeight, left, top);

            case 3:
                // 肌色ホワイトバランス(除算)を仕上げ効果の前に。アバター合成後なので
                // アバターと背景の両方に効く「全体エフェクト」。
                if (skinWbAmount > 0 && !GpuSkinWhiteBalance.ApplyToTexture(main, device, photoWidth, photoHeight,
                        skinWbAmount, skinWbR, skinWbG, skinWbB))
                {
                    return false;
                }
                // GpuFinishingEffects.TryRunPreToneGradient と同じグループ分け。
                return GpuFinishingEffects.ApplyToTexture(main, device, photoWidth, photoHeight,
                    softnessAmount, sharpnessAmount, finishDetailScale,
                    clarityAmount, clarityScale,
                    fadeAmount,
                    glowAmount, glowScale,
                    lightLeakAmount, lightLeakAngle, lightLeakDistance, lightLeakColorB, lightLeakColorG, lightLeakColorR,
                    chromaticAberrationAmount: 0, colorBleedAmount: 0, vhsScale: 1.0,
                    vignetteAmount: 0);

            case 4:
                if (toneGradientAmount <= 0) return true;
                return GpuToneGradient.ApplyToTexture(main, device, photoWidth, photoHeight, toneGradientAmount, toneGradientRotation,
                    toneGradientLightR, toneGradientLightG, toneGradientLightB,
                    toneGradientDarkR, toneGradientDarkG, toneGradientDarkB);

            case 5:
                // GpuFinishingEffects.TryRunPreScanlines と同じグループ分け。
                return GpuFinishingEffects.ApplyToTexture(main, device, photoWidth, photoHeight,
                    softnessAmount: 0, sharpnessAmount: 0, finishDetailScale: 1.0,
                    clarityAmount: 0, clarityScale: 1.0,
                    fadeAmount: 0,
                    glowAmount: 0, glowScale: 1.0,
                    lightLeakAmount: 0, lightLeakAngle: 0, lightLeakDistance: 0, lightLeakColorB: 0, lightLeakColorG: 0, lightLeakColorR: 0,
                    chromaticAberrationAmount, colorBleedAmount, vhsScale,
                    vignetteAmount: 0);

            case 6:
                if (scanlineAmount <= 0) return true;
                return GpuScanlines.ApplyToTexture(main, device, photoWidth, photoHeight, scanlineAmount, vhsScale);

            case 7:
                // GpuFinishingEffects.TryRunVignette と同じグループ分け。
                return GpuFinishingEffects.ApplyToTexture(main, device, photoWidth, photoHeight,
                    softnessAmount: 0, sharpnessAmount: 0, finishDetailScale: 1.0,
                    clarityAmount: 0, clarityScale: 1.0,
                    fadeAmount: 0,
                    glowAmount: 0, glowScale: 1.0,
                    lightLeakAmount: 0, lightLeakAngle: 0, lightLeakDistance: 0, lightLeakColorB: 0, lightLeakColorG: 0, lightLeakColorR: 0,
                    chromaticAberrationAmount: 0, colorBleedAmount: 0, vhsScale: 1.0,
                    vignetteAmount);

            case 8:
                if (grainAmount <= 0) return true;
                return GpuFilmGrain.ApplyToTexture(main, device, photoWidth, photoHeight, grainAmount);

            default:
                return true;
        }
    }
}
