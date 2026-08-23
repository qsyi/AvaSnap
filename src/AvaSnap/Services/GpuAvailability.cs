using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>Caches whether a DX12-capable GPU/driver is actually available,
/// checked once instead of every one of GpuColorAdjustments/
/// GpuCompositePipeline/GpuFinishingEffects re-attempting GraphicsDevice.
/// GetDefault() (and eating its exception, on a machine where it fails) on
/// every single render -- including every tick of a slider drag. A machine
/// that can't do DX12 compute isn't going to start being able to mid-
/// session, so once determined, the result is trusted for the rest of the
/// app's lifetime.</summary>
public static class GpuAvailability
{
    private static bool? _isAvailable;
    private static GraphicsDevice? _device;

    /// <summary>The default GPU device, or null if none is available --
    /// callers should bail out to their CPU fallback immediately when this
    /// is null, without attempting any texture allocation.</summary>
    public static GraphicsDevice? Device
    {
        get
        {
            if (_isAvailable is null)
            {
                try
                {
                    _device = GraphicsDevice.GetDefault();
                    _isAvailable = true;
                }
                catch (Exception)
                {
                    _isAvailable = false;
                }
            }
            return _device;
        }
    }
}
