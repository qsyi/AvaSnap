using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AvaSnap;

/// <summary>Shared, observable state for the overlay image, bound by both the
/// overlay window (for rendering) and the control panel (for numeric editing).</summary>
public sealed class OverlayState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Batching: several call sites (UndoManager.Apply, the "match look"
    //      buttons, position-estimate recompute) set a dozen-plus properties
    //      as one logical operation. Without batching, each Set below fires
    //      its own PropertyChanged, and subscribers that rebuild real UI on
    //      every notification (OverlayWindow's guide redraw, ControlPanelWindow's
    //      RefreshFromState) end up doing that full rebuild once per FIELD
    //      instead of once per operation. BeginBatch/EndBatch nest (matching
    //      UndoManager's own Begin/Commit depth-counter pattern) so a batched
    //      call from inside another batch just extends the outer one instead
    //      of firing early. A single PropertyChangedEventArgs(null) at the end
    //      -- the standard INotifyPropertyChanged convention for "more than
    //      one property changed" -- tells subscribers to treat it like a full
    //      refresh, same as they'd already have to for an unknown property. ----
    private int _batchDepth;
    private bool _batchChangedAny;

    public void BeginBatch() => _batchDepth++;

    public void EndBatch()
    {
        if (_batchDepth == 0) return; // stray EndBatch with no matching BeginBatch
        _batchDepth--;
        if (_batchDepth > 0) return; // still inside an outer, not-yet-ended BeginBatch
        if (!_batchChangedAny) return;
        _batchChangedAny = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (_batchDepth > 0)
        {
            _batchChangedAny = true;
            return;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string? _imagePath;
    public string? ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }

    private double _x = 200;
    public double X { get => _x; set => Set(ref _x, value); }

    private double _y = 200;
    public double Y { get => _y; set => Set(ref _y, value); }

    // WPF throws if a FrameworkElement's Width/Height is set negative, which
    // would otherwise crash the whole app (observed: a bad detection/preset
    // producing a negative size took down the process from a single Snap
    // click). Clamp defensively here so a bad upstream value degrades to "very
    // small" instead of a hard crash.
    private double _width = 400;
    public double Width { get => _width; set => Set(ref _width, Math.Max(1, value)); }

    private double _height = 300;
    public double Height { get => _height; set => Set(ref _height, Math.Max(1, value)); }

    private double _rotationDegrees;
    public double RotationDegrees { get => _rotationDegrees; set => Set(ref _rotationDegrees, value); }

    private double _opacity = 0.9;
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    private bool _isClickThrough = true;
    public bool IsClickThrough { get => _isClickThrough; set => Set(ref _isClickThrough, value); }

    private bool _isImageVisible = true;
    /// <summary>A full show/hide of the avatar overlay (and its resize/
    /// rotate handles) -- distinct from Opacity, which just fades it, and
    /// from IsClickThrough, which only affects mouse interaction. A mode
    /// toggle like IsClickThrough (not an edit), so it's intentionally NOT
    /// part of the undo snapshot -- see UndoManager's own comment on why
    /// IsClickThrough is excluded, same reasoning applies here.</summary>
    public bool IsImageVisible { get => _isImageVisible; set => Set(ref _isImageVisible, value); }

    // ---- PNG look adjustments: shared between the align-mode live preview
    //      (over VRChat) and the composite-mode preview (over a captured
    //      photo) -- editing from either place changes the same values, since
    //      "how does this cutout look" doesn't depend on what's behind it. ----

    private double _edgeBlurRadius = 5;
    /// <summary>Pixel radius of a blur applied to the PNG's alpha channel only
    /// (softens the cutout's silhouette without blurring its interior detail).
    /// 0 = off.</summary>
    public double EdgeBlurRadius { get => _edgeBlurRadius; set => Set(ref _edgeBlurRadius, Math.Max(0, value)); }

    private double _brightness;
    /// <summary>-100..100, 0 = unchanged.</summary>
    public double Brightness { get => _brightness; set => Set(ref _brightness, value); }

    private double _contrast;
    /// <summary>-100..100, 0 = unchanged.</summary>
    public double Contrast { get => _contrast; set => Set(ref _contrast, value); }

    private double _saturation;
    /// <summary>-100..100, 0 = unchanged, -100 = grayscale.</summary>
    public double Saturation { get => _saturation; set => Set(ref _saturation, value); }

    private double _vibrance;
    /// <summary>-100..100, 0 = unchanged. Like Saturation but scales its effect
    /// down on pixels that are already fairly saturated (skin tones etc.), so
    /// boosting it doesn't oversaturate faces the way plain Saturation does.</summary>
    public double Vibrance { get => _vibrance; set => Set(ref _vibrance, value); }

    private double _temperature;
    /// <summary>-100..100, 0 = unchanged. Negative = cooler (more blue),
    /// positive = warmer (more orange).</summary>
    public double Temperature { get => _temperature; set => Set(ref _temperature, value); }

    private double _tint;
    /// <summary>-100..100, 0 = unchanged. Negative = more magenta, positive =
    /// more green.</summary>
    public double Tint { get => _tint; set => Set(ref _tint, value); }

    private double _hue;
    /// <summary>-180..180 degrees of hue rotation around the color wheel,
    /// 0 = unchanged.</summary>
    public double Hue { get => _hue; set => Set(ref _hue, value); }

    private double _highlights;
    /// <summary>-100..100, 0 = unchanged. Brightens/darkens bright tones more
    /// than dark ones.</summary>
    public double Highlights { get => _highlights; set => Set(ref _highlights, value); }

    private double _shadows;
    /// <summary>-100..100, 0 = unchanged. Brightens/darkens dark tones more
    /// than bright ones.</summary>
    public double Shadows { get => _shadows; set => Set(ref _shadows, value); }

    private double _whites;
    /// <summary>-100..100, 0 = unchanged. Like Highlights but concentrated
    /// more narrowly on the very brightest tones (the white clipping
    /// point).</summary>
    public double Whites { get => _whites; set => Set(ref _whites, value); }

    private double _blacks;
    /// <summary>-100..100, 0 = unchanged. Like Shadows but concentrated more
    /// narrowly on the very darkest tones (the black clipping point).</summary>
    public double Blacks { get => _blacks; set => Set(ref _blacks, value); }

    private double _colorTintStrength;
    /// <summary>0..100, 0 = off. Blends toward ColorTintR/G/B (see
    /// ImageAdjustment.AdjustColors' ColorTint block) -- a luminance-
    /// preserving color wash, distinct from the Temperature/Tint axis
    /// sliders above.</summary>
    public double ColorTintStrength { get => _colorTintStrength; set => Set(ref _colorTintStrength, value); }

    private byte _colorTintR = 255, _colorTintG = 255, _colorTintB = 255;
    public byte ColorTintR { get => _colorTintR; set => Set(ref _colorTintR, value); }
    public byte ColorTintG { get => _colorTintG; set => Set(ref _colorTintG, value); }
    public byte ColorTintB { get => _colorTintB; set => Set(ref _colorTintB, value); }

    // ---- Unity連携ガイド: shown over the live VRChat window (OverlayWindow),
    //      controlled from the control panel's Align-mode side. Purely a
    //      view-time aid, never baked into anything saved. GuideManualFov/
    //      Pitch/Roll are the only source of truth for what's drawn -- a
    //      successful Unity fetch (see ControlPanelWindow's
    //      UnityCameraGuideService.DataUpdated handler) just writes into
    //      these same three fields, same as typing a value in by hand. ----

    private bool _guideVisible;
    public bool GuideVisible { get => _guideVisible; set => Set(ref _guideVisible, value); }

    private double _guideManualFov = 45; // VRChat's own camera FOV default
    public double GuideManualFov { get => _guideManualFov; set => Set(ref _guideManualFov, value); }

    private double _guideManualPitch;
    public double GuideManualPitch { get => _guideManualPitch; set => Set(ref _guideManualPitch, value); }

    private double _guideManualRoll;
    public double GuideManualRoll { get => _guideManualRoll; set => Set(ref _guideManualRoll, value); }
}
