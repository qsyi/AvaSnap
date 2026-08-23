using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>Runs the color-adjustment stage and the photo blur stage as ONE
/// GPU pipeline: uploads the pixel buffer once, dispatches whichever of the
/// two passes are actually needed back-to-back on GPU-resident textures,
/// and downloads once at the end -- color grading and the photo blur are
/// the two heaviest, most-often-active effects in CompositeOverlayOntoPhoto,
/// so folding them into one transfer covers most of the actual cost. Most
/// of the OTHER finishing effects (softness/sharpness/clarity/fade/glow/
/// light leak/chromatic aberration/color bleed/vignette) are covered by a
/// separate GPU pipeline, GpuFinishingEffects, further down the same
/// method. Drop shadow, the avatar alpha-blend, tone gradient, scanlines,
/// and film grain still run as CPU passes -- see GPU_MIGRATION_PLAN.md at
/// the repo root for the plan to fold those in too. Texture allocations
/// themselves are cached across calls by GpuTexturePool, not freshly
/// allocated every render.</summary>
public static class GpuCompositePipeline
{
    /// <summary>Returns false (leaving <paramref name="outputPixels"/>
    /// untouched) if no DX12-capable GPU/driver is available, so the caller
    /// falls back to running ImageAdjustment's own CPU AdjustColors/
    /// ApplyPhotoBlur instead -- same contract as GpuColorAdjustments.
    /// TryAdjustColors. <paramref name="blurRadiusPixels"/> is the ALREADY-
    /// scaled pixel radius (see ApplyPhotoBlur's own radius computation) --
    /// 0 or less skips the blur pass entirely.
    ///
    /// <paramref name="sourcePixels"/> and <paramref name="outputPixels"/>
    /// are deliberately separate buffers, not one buffer processed in
    /// place: <paramref name="sourcePixels"/> should be the caller's
    /// pristine, never-mutated photo buffer (e.g. ControlPanelWindow's own
    /// _photoPixelBuffer.Pixels) so GpuTexturePool.RentUploaded can skip
    /// re-uploading it to the GPU on every render where only the adjustment
    /// AMOUNTS changed, not the photo itself -- see its own doc comment.
    /// <paramref name="outputPixels"/> is a separate, freely-overwritable
    /// buffer the caller wants the processed result written into (it does
    /// NOT need to start as a copy of the source; every byte is overwritten
    /// by the final GPU download regardless).</summary>
    public static bool TryRun(byte[] sourcePixels, byte[] outputPixels, int stride, int width, int height,
        ImageAdjustment.ColorAdjustments colorAdj, int blurRadiusPixels)
    {
        bool needsColor = !colorAdj.IsIdentity;
        bool needsBlur = blurRadiusPixels > 0;
        if (stride != width * 4 || sourcePixels.Length < stride * height || outputPixels.Length < stride * height) return false;
        if (!needsColor && !needsBlur)
        {
            // Nothing to actually process, but outputPixels still needs to
            // end up holding a valid result -- the caller's contract is
            // "outputPixels is the processed photo" regardless of whether
            // any processing was needed, and outputPixels starts as a fresh,
            // uninitialized (all-zero/fully-transparent) buffer. Forgetting
            // this copy left a freshly-loaded photo with default (identity)
            // adjustments compositing as fully transparent -- invisible --
            // until the first slider touch made needsColor/needsBlur true
            // and the GPU path actually ran.
            Array.Copy(sourcePixels, outputPixels, stride * height);
            return true;
        }
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> outputSpan = MemoryMarshal.Cast<byte, Bgra32>(outputPixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> source = GpuTexturePool.RentUploaded(device, "Composite.Source", sourcePixels, stride, width, height);
            ReadWriteTexture2D<Bgra32, float4> textureA = GpuTexturePool.Rent(device, "Composite.A", width, height);
            // A cheap GPU-to-GPU copy, not a CPU round trip -- textureA may
            // hold a PREVIOUS render's processed result (or garbage, the
            // first time), so it always needs refreshing from the pristine
            // source even when the source's own upload above was skipped.
            source.CopyTo(textureA);

            ApplyToTexture(textureA, device, width, height, colorAdj, blurRadiusPixels);

            textureA.CopyTo(outputSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same color+blur pass as <see cref="TryRun"/>, but operating
    /// directly on an already GPU-resident <paramref name="texture"/> --
    /// no upload/download of its own. Used by GpuCompositeChain to run this
    /// as one step of a longer effect chain that keeps the whole photo
    /// resident on the GPU across every stage instead of each stage paying
    /// for its own CPU round trip -- see GpuCompositeChain's own doc
    /// comment for why that round-trip cost matters.</summary>
    internal static void ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height,
        ImageAdjustment.ColorAdjustments colorAdj, int blurRadiusPixels)
    {
        if (!colorAdj.IsIdentity)
        {
            device.For(width, height, GpuColorAdjustments.BuildShader(texture, colorAdj));
        }

        if (blurRadiusPixels > 0)
        {
            // Two ping-ponged textures, not one shared in-place buffer:
            // each pass reads NEIGHBORING pixels, and a compute dispatch
            // gives no ordering guarantee between threads, so a thread
            // could read a neighbor another thread already overwrote this
            // same pass. Horizontal reads texture, writes scratch; vertical
            // reads scratch, writes the final result back into texture.
            ReadWriteTexture2D<Bgra32, float4> scratch = GpuTexturePool.Rent(device, "Composite.B", width, height);

            device.For(width, height, new BoxBlurPassShader(texture, scratch, width, height, blurRadiusPixels, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, texture, width, height, blurRadiusPixels, horizontal: false));
        }
    }
}

/// <summary>One axis of a separable box blur -- see BoxBlur1D's own doc
/// comment in ImageAdjustment.cs for the CPU equivalent this mirrors
/// (clamp-to-edge sampling, constant window-size divisor). Runs as a plain
/// per-pixel sum over 2*radius+1 samples rather than BoxBlur1D's sliding-
/// window O(1)-per-pixel trick -- GPU parallelism across every pixel at
/// once makes the brute-force version fast enough regardless (radius is
/// capped at MaxPhotoBlurRadius=40), and it's far simpler to get right in a
/// shader with no cross-thread state to carry between pixels. Alpha is
/// read through unchanged from source, matching ApplyPhotoBlur's own
/// B/G/R-only scope.</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BoxBlurPassShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width, int height, int radius, bool horizontal) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float sumB = 0f, sumG = 0f, sumR = 0f;
        int windowSize = radius * 2 + 1;

        if (horizontal)
        {
            for (int k = -radius; k <= radius; k++)
            {
                int xx = Hlsl.Clamp(x + k, 0, width - 1);
                float4 px = source[new int2(xx, y)];
                sumB += px.B;
                sumG += px.G;
                sumR += px.R;
            }
        }
        else
        {
            for (int k = -radius; k <= radius; k++)
            {
                int yy = Hlsl.Clamp(y + k, 0, height - 1);
                float4 px = source[new int2(x, yy)];
                sumB += px.B;
                sumG += px.G;
                sumR += px.R;
            }
        }

        float alpha = source[new int2(x, y)].A;
        destination[new int2(x, y)] = new float4(sumR / windowSize, sumG / windowSize, sumB / windowSize, alpha);
    }
}
