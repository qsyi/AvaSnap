using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>Caches GPU textures across renders instead of GpuColorAdjustments/
/// GpuCompositePipeline/GpuFinishingEffects each allocating (and disposing)
/// fresh ones on every single call -- including every tick of a slider
/// drag. Texture allocation has real driver overhead beyond just the pixel
/// data transfer, so reusing the same physical texture when the requested
/// size hasn't changed avoids paying that repeatedly.
///
/// Callers identify what they want with a <paramref name="key"/> string
/// (e.g. "Composite.A"), not a shared slot number -- the avatar-image path
/// (GpuColorAdjustments, typically PNG-sized) and the photo path
/// (GpuCompositePipeline/GpuFinishingEffects, typically much larger) would
/// otherwise constantly invalidate each other's cached texture if they took
/// turns using the same slot at two different sizes, defeating the whole
/// point. Each distinct key gets its own independently-sized slot.
///
/// Rented textures live for the rest of the app's process lifetime (or
/// until a differently-sized request for the same key replaces them) --
/// there's no explicit release-on-photo-unload, which is an accepted
/// simplification for now (see GPU_MIGRATION_PLAN.md at the repo root):
/// worst case is one extra texture's worth of GPU memory held onto per key
/// until the process exits, not an unbounded leak, since a given key only
/// ever holds ONE texture at a time.</summary>
public static class GpuTexturePool
{
    private sealed record Slot(int Width, int Height, ReadWriteTexture2D<Bgra32, float4> Texture, object? UploadedFrom = null);

    private static readonly Dictionary<string, Slot> Slots = new();

    /// <summary>Returns the cached texture for <paramref name="key"/> if it's
    /// already the requested size, otherwise disposes whatever was there and
    /// allocates a fresh one. The returned texture's CONTENTS are whatever
    /// was left over from the previous use (or undefined, the first time) --
    /// callers that need it to reflect current pixel data must CopyFrom
    /// their own source after renting.</summary>
    public static ReadWriteTexture2D<Bgra32, float4> Rent(GraphicsDevice device, string key, int width, int height)
    {
        if (Slots.TryGetValue(key, out var existing))
        {
            if (existing.Width == width && existing.Height == height)
            {
                return existing.Texture;
            }
            existing.Texture.Dispose();
        }

        var texture = device.AllocateReadWriteTexture2D<Bgra32, float4>(width, height);
        Slots[key] = new Slot(width, height, texture);
        return texture;
    }

    /// <summary>Like <see cref="Rent"/>, but also uploads <paramref name="pixels"/>
    /// into the texture -- SKIPPING that upload if the exact same <paramref name="pixels"/>
    /// array reference (not just equal content -- reference) was already
    /// uploaded to this key at this size. Meant for a source buffer that's
    /// only ever replaced wholesale, never mutated in place (e.g.
    /// ControlPanelWindow's own _photoPixelBuffer.Pixels, replaced only by
    /// TryLoadPhotoPixels), so "same reference" reliably means "same
    /// content, no need to re-upload" -- a slider-drag render that only
    /// changes ADJUSTMENT amounts, not the underlying photo, calls this with
    /// the identical array every time and pays for the CPU->GPU transfer
    /// only once per photo load instead of once per render.</summary>
    public static ReadWriteTexture2D<Bgra32, float4> RentUploaded(GraphicsDevice device, string key, byte[] pixels, int stride, int width, int height)
    {
        if (Slots.TryGetValue(key, out var existing) && existing.Width == width && existing.Height == height)
        {
            if (ReferenceEquals(existing.UploadedFrom, pixels))
            {
                return existing.Texture;
            }
            existing.Texture.CopyFrom(MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height)));
            Slots[key] = existing with { UploadedFrom = pixels };
            return existing.Texture;
        }

        if (existing is not null)
        {
            existing.Texture.Dispose();
        }

        var texture = device.AllocateReadWriteTexture2D<Bgra32, float4>(width, height);
        texture.CopyFrom(MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height)));
        Slots[key] = new Slot(width, height, texture, pixels);
        return texture;
    }
}
