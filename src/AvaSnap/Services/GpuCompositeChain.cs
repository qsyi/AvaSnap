using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>Runs CompositeOverlayOntoPhoto's entire GPU effect chain (color
/// adjust, photo blur, drop shadow, avatar blend, softness/sharpness/
/// clarity/fade/glow/light leak, tone gradient, chromatic aberration/color
/// bleed, scanlines, vignette, film grain) as ONE GPU round trip: upload the
/// photo once, dispatch all 9 stages back-to-back on the same GPU-resident
/// texture (each stage's own ApplyToTexture/BlendIntoTexture method, added
/// alongside its existing byte[]-based TryRun/TryApply for standalone/test
/// use), and download once at the end.
///
/// Before this existed, CompositeOverlayOntoPhoto called each stage's own
/// byte[]-based method in sequence, and each one independently uploaded the
/// full photo to the GPU and downloaded it back before the next stage could
/// even start. Measured directly (see GpuProfile in the repo's scratchpad
/// tooling): a single upload+download round trip for a 1920x1080 buffer
/// costs ~11ms on its own, and the old call sequence did about 9 of them per
/// composite -- roughly 100ms of pure transfer overhead out of a ~220ms
/// total, worse at higher resolutions since transfer scales with pixel
/// count same as compute. Keeping the texture resident across the whole
/// chain cuts that down to one upload and one download total.
///
/// On top of that, this ALSO checkpoints the GPU-resident result after every
/// stage (a cheap GPU-to-GPU copy) and remembers the exact inputs that
/// produced each one. During a real slider drag only ONE stage's own
/// parameters actually change between renders -- every stage before it in
/// the chain would recompute byte-identical output given byte-identical
/// input, so there is nothing to gain from rerunning it. On the next call,
/// <see cref="TryRun"/> finds the LAST stage boundary whose cumulative
/// inputs still match the previous call, restores the GPU texture from that
/// checkpoint instead of the raw source photo, and only dispatches the
/// stages from there onward. Measured directly (see GpuProfile's
/// "ProfileStageSkipPotential"): for a 1920x1080 composite, redoing only
/// film grain costs ~0.7ms against a ~25ms full chain (roughly 30-40x), and
/// even a mid-chain slider like tone gradient sees roughly a 3-4x speedup --
/// dragging any single "finishing touch" slider (grain/vignette/scanlines/
/// tone gradient are the most common late-chain adjustments) becomes close
/// to instant instead of paying for the whole chain on every tick. A photo
/// swap, an avatar swap, or a change to an EARLY-chain parameter (color
/// adjustments, photo blur, drop shadow, avatar placement) still invalidates
/// everything downstream, same as before -- there is no way around
/// recomputing what actually depends on changed input.
///
/// This cache is static, process-lifetime, single-slot (one photo's worth of
/// checkpoints at a time, keyed by nothing but "the last call's inputs") --
/// same assumptions GpuTexturePool itself already makes, and same
/// single-threaded-access contract ControlPanelWindow's own
/// _compositeRenderGate already enforces around every GPU-pipeline call, so
/// no additional synchronization is added here.</summary>
public static class GpuCompositeChain
{
    private const int StageCount = 9;

    /// <summary>Every parameter TryRun receives that can affect the final
    /// pixels, grouped in chain order. Reference-type fields (byte[]
    /// buffers) are compared by REFERENCE, not content -- same "replaced
    /// wholesale, never mutated in place" convention as
    /// GpuTexturePool.RentUploaded and ControlPanelWindow's own
    /// CachedOverlayRender/CachedBeforeCompositeKey, and the reason a
    /// record (whose generated equality uses EqualityComparer&lt;T&gt;.Default
    /// per field, which for arrays IS reference equality) is the right tool
    /// here rather than a manual deep comparison.</summary>
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
        double ToneGradientAmount, double ToneGradientRotation,
        byte ToneGradientLightR, byte ToneGradientLightG, byte ToneGradientLightB,
        byte ToneGradientDarkR, byte ToneGradientDarkG, byte ToneGradientDarkB,
        double ChromaticAberrationAmount, double ColorBleedAmount, double VhsScale,
        double ScanlineAmount, double VignetteAmount, double GrainAmount);

    private static ChainInputs? _lastInputs;

    /// <summary>Returns the index (0..StageCount) of the first stage whose
    /// OWN inputs differ from the previous call -- everything BEFORE that
    /// stage produced byte-identical output last time and can be restored
    /// from its checkpoint instead of recomputed. Deliberately coarser than
    /// per-parameter: e.g. changing ANY drop-shadow parameter invalidates
    /// both DropShadow (stage 1) and AvatarBlend (stage 2) together, since
    /// they share the same overlay input and sit adjacent in the chain --
    /// splitting them would only help the rare case of dragging drop-shadow
    /// sliders specifically, not the far more common single-slider drags
    /// this exists for. Returns StageCount if EVERY input matches (nothing
    /// to do at all -- the caller can reuse the previous final result
    /// outright).</summary>
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

        // Stage 2 (AvatarBlend) has no inputs of its own beyond the overlay
        // ref/dims/position already checked above -- if we got here, those
        // matched, so stage 2 is valid too. Its checkpoint still gets
        // refreshed below like every other stage; it just never needs to be
        // the FIRST differing one.

        if (prev.SoftnessAmount != cur.SoftnessAmount || prev.SharpnessAmount != cur.SharpnessAmount
            || prev.FinishDetailScale != cur.FinishDetailScale || prev.ClarityAmount != cur.ClarityAmount
            || prev.ClarityScale != cur.ClarityScale || prev.FadeAmount != cur.FadeAmount
            || prev.GlowAmount != cur.GlowAmount || prev.GlowScale != cur.GlowScale
            || prev.LightLeakAmount != cur.LightLeakAmount || prev.LightLeakAngle != cur.LightLeakAngle
            || prev.LightLeakDistance != cur.LightLeakDistance || prev.LightLeakColorB != cur.LightLeakColorB
            || prev.LightLeakColorG != cur.LightLeakColorG || prev.LightLeakColorR != cur.LightLeakColorR)
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

    /// <summary>Returns false (leaving <paramref name="outputPixels"/>
    /// untouched) if no DX12-capable GPU/driver is available. <paramref
    /// name="photoPixels"/> should be the caller's pristine, never-mutated
    /// photo buffer (same contract as GpuCompositePipeline.TryRun) so the
    /// source upload can be skipped on renders where only effect amounts
    /// changed, not the photo itself. <paramref name="overlayPixels"/> null
    /// skips drop shadow and the avatar blend entirely, matching
    /// CompositeOverlayOntoPhoto's own "if (overlayPixels is not null)"
    /// gate.</summary>
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
            toneGradientAmount, toneGradientRotation,
            toneGradientLightR, toneGradientLightG, toneGradientLightB,
            toneGradientDarkR, toneGradientDarkG, toneGradientDarkB,
            chromaticAberrationAmount, colorBleedAmount, vhsScale,
            scanlineAmount, vignetteAmount, grainAmount);

        try
        {
            Span<Bgra32> outputSpan = MemoryMarshal.Cast<byte, Bgra32>(outputPixels.AsSpan(0, photoStride * photoHeight));

            int firstStage = FirstDifferingStage(_lastInputs, cur);

            // Every checkpoint texture is rented (not freshly allocated) so
            // resizing on a photo-dimension change is GpuTexturePool's own
            // problem, same as every other texture in this pipeline -- and
            // since a dimension change always makes firstStage come back 0
            // above, a resized (garbage-content) checkpoint is never read
            // from, only written into fresh starting at stage 0.
            var checkpoints = new ReadWriteTexture2D<Bgra32, float4>[StageCount];
            for (int i = 0; i < StageCount; i++)
            {
                checkpoints[i] = GpuTexturePool.Rent(device, $"Chain.Checkpoint{i}", photoWidth, photoHeight);
            }

            if (firstStage >= StageCount)
            {
                // Nothing at all changed since the last call -- the final
                // checkpoint IS this call's answer, no GPU compute needed.
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
                    toneGradientAmount, toneGradientRotation,
                    toneGradientLightR, toneGradientLightG, toneGradientLightB,
                    toneGradientDarkR, toneGradientDarkG, toneGradientDarkB,
                    chromaticAberrationAmount, colorBleedAmount, vhsScale,
                    scanlineAmount, vignetteAmount, grainAmount))
                {
                    return false;
                }

                // Cheap GPU-to-GPU copy -- refreshes this stage's checkpoint
                // so a LATER call that only changes something further down
                // the chain can restore from here instead of stage 0.
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

    /// <summary>Dispatches exactly one stage (by the same 0..8 indices
    /// <see cref="FirstDifferingStage"/> returns) onto <paramref name="main"/>.
    /// Split out from <see cref="TryRun"/> purely so the "run stages
    /// firstStage..8" loop above doesn't have to duplicate each stage's own
    /// call -- the actual per-stage logic is unchanged from the original
    /// unconditional chain.</summary>
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
                // Same grouping as GpuFinishingEffects.TryRunPreToneGradient.
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
                // Same grouping as GpuFinishingEffects.TryRunPreScanlines.
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
                // Same grouping as GpuFinishingEffects.TryRunVignette.
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
