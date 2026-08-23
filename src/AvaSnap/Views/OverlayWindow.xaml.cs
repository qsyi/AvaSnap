using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvaSnap.Services;

namespace AvaSnap.Views;

public partial class OverlayWindow : Window
{
    private readonly OverlayState _state;
    private readonly UndoManager _undo;

    private bool _isDraggingMove;
    private Point _dragStartMouse;
    private double _dragStartX, _dragStartY;

    private readonly VrChatOscListener _oscListener;
    private readonly UnityCameraGuideService _unityCameraGuide;
    private UnityCameraGuideData? _unityGuideData;

    public OverlayWindow(OverlayState state, UndoManager undo, VrChatOscListener oscListener, UnityCameraGuideService unityCameraGuide)
    {
        InitializeComponent();
        _state = state;
        _undo = undo;
        _oscListener = oscListener;
        _unityCameraGuide = unityCameraGuide;
        _unityCameraGuide.DataUpdated += OnUnityGuideDataUpdated;
        _unityCameraGuide.BecameStale += OnUnityGuideBecameStale;

        // Cover the full virtual screen (all monitors) at its true origin, so
        // Canvas coordinates line up 1:1 with screen coordinates regardless of
        // monitor layout.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _state.PropertyChanged += (_, e) => ApplyState(e.PropertyName);
        _state.PropertyChanged += OnAdjustmentPropertyChanged;
        Loaded += (_, _) =>
        {
            ApplyClickThrough(_state.IsClickThrough);
            ApplyState();
        };

        PreviewKeyDown += OverlayWindow_PreviewKeyDown;
        StartHotkeyPolling();

        // Auto-hide: only useful while VRChat's camera UI is actually open,
        // otherwise it's just a floating image over normal gameplay. Driven by
        // VRChat's own /usercamera/Mode OSC output instead of periodic
        // screen-capture + template matching -- cheaper and immune to the
        // false-positive-icon-match risk that approach had.
        _oscListener.CameraModeChanged += OnCameraModeChanged;
        if (_oscListener.IsCameraOpen is { } open) ApplyCameraOpenState(open);
    }

    private void OnCameraModeChanged(bool open) => Dispatcher.Invoke(() => ApplyCameraOpenState(open));

    private bool _manuallyHidden;

    /// <summary>ControlPanelWindow calls this while it's minimized or in
    /// compact mode (see its EnterCompact/ExpandButton_Click/
    /// Window_StateChanged), so the overlay stays hidden even if the user
    /// re-opens VRChat's camera UI in the meantime -- without this,
    /// ApplyCameraOpenState's own Show() would fight the minimize and pop
    /// the overlay back up on top of VRChat unexpectedly. Un-hiding re-syncs
    /// to whatever the camera's ACTUAL state is now (it may have changed
    /// while suppressed), rather than assuming it's still open.</summary>
    public void SetManuallyHidden(bool hidden)
    {
        if (_manuallyHidden == hidden) return;
        _manuallyHidden = hidden;
        if (hidden)
        {
            Hide();
        }
        else if (_oscListener.IsCameraOpen is true)
        {
            Show();
        }
    }

    private void ApplyCameraOpenState(bool open)
    {
        if (_manuallyHidden) return;
        if (open) Show(); else Hide();
    }

    /// <summary>Sets the overlay's initial visibility to match VRChat's actual
    /// camera-UI state at app startup, instead of defaulting to always-visible.
    /// VRChat only sends /usercamera/Mode over OSC on CHANGE, not an initial
    /// snapshot, so if OSC hasn't reported anything yet the real state is simply
    /// unknown -- stay hidden until the first OSC message arrives rather than
    /// guessing (no CV fallback anymore; detection now runs entirely on VRChat's
    /// own OSC output).</summary>
    public void InitializeCameraVisibility()
    {
        if (_oscListener.IsCameraOpen is { } open)
        {
            ApplyCameraOpenState(open);
            return;
        }
        Hide();
    }

    /// <summary>Covers the case the VRChat-focus poll above can't: clicking the
    /// overlay itself (e.g. a Shift-held drag) activates this window, making
    /// AvaSnap's own overlay -- not VRChat -- the foreground window right
    /// after. UndoManager's built-in debounce guards against this overlapping
    /// with the other Ctrl+Z paths (ControlPanelWindow's own handler, the
    /// VRChat-focus poll) for the same physical keypress.</summary>
    private void OverlayWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrl) return;

        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _undo.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Z)
        {
            _undo.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            _undo.Redo();
            e.Handled = true;
        }
    }

    /// <summary>The pristine image pre-converted to a raw BGRA32 pixel buffer,
    /// prepared once on load instead of on every adjustment tweak (the
    /// FormatConvertedBitmap + WriteableBitmap allocation was needless repeated
    /// work on top of the actual per-pixel processing). Look adjustments always
    /// reprocess from this, never from a previously-adjusted result, so
    /// repeated tweaks never compound/degrade quality.</summary>
    private ImageAdjustment.PixelBuffer? _originalPixelBuffer;

    /// <summary>The full-resolution blur stage's own result, cached
    /// separately from the color stage: re-blurring is the expensive part
    /// (four channels, two passes), so it's only redone when the edge-blur
    /// radius itself changes, not on every brightness/contrast/saturation
    /// tweak.</summary>
    private ImageAdjustment.PixelBuffer? _blurredPixelBuffer;
    private double? _blurredAtRadius;

    public void LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        _originalPixelBuffer = ImageAdjustment.PrepareBuffer(bitmap);
        _blurredPixelBuffer = null;
        _blurredAtRadius = null;
        _state.ImagePath = path;
        ApplyImageAdjustments();

        // The box's Width/Height don't otherwise change on load, but
        // Stretch="Fill" would visibly distort a new image whose aspect
        // ratio differs from whatever the box happened to be sized to
        // before (leftover from a previous image, or the default) -- fix
        // the height to match immediately instead of waiting for the next
        // Snap. Adjust around the box's current CENTER (like the mouse-wheel
        // zoom does) rather than keeping the top-left corner fixed, so the
        // image doesn't visibly jump before a Snap recenters it anyway.
        if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            double aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            double centerX = _state.X + _state.Width / 2;
            double centerY = _state.Y + _state.Height / 2;
            double newHeight = _state.Width / aspect;
            _state.Height = newHeight;
            _state.X = centerX - _state.Width / 2;
            _state.Y = centerY - newHeight / 2;
        }
    }

    private static readonly HashSet<string?> AdjustmentPropertyNames = new()
    {
        nameof(OverlayState.EdgeBlurRadius), nameof(OverlayState.Brightness), nameof(OverlayState.Contrast), nameof(OverlayState.Saturation),
        nameof(OverlayState.Vibrance), nameof(OverlayState.Temperature), nameof(OverlayState.Tint), nameof(OverlayState.Hue),
        nameof(OverlayState.Highlights), nameof(OverlayState.Shadows), nameof(OverlayState.Whites), nameof(OverlayState.Blacks),
        nameof(OverlayState.ColorTintStrength), nameof(OverlayState.ColorTintR), nameof(OverlayState.ColorTintG), nameof(OverlayState.ColorTintB),
    };

    // ---- Throttle image reprocessing while a slider is being dragged: a full
    //      re-blur + re-color-adjust on every single tick (dozens/sec) visibly
    //      lagged, especially on larger source images. Reprocess at most every
    //      AdjustmentThrottle, and always schedule a trailing update so the
    //      exact final value still renders shortly after the user stops. ----

    private static readonly TimeSpan AdjustmentThrottle = TimeSpan.FromMilliseconds(80);
    private DateTime _lastAdjustmentApply = DateTime.MinValue;
    private DispatcherTimer? _pendingAdjustmentTimer;

    // ---- Brightness/contrast/saturation/vibrance/temperature/tint/hue AND
    //      edge blur all keep live-updating during a drag, reprocessing the
    //      full-resolution buffer on every tick, throttled by
    //      AdjustmentThrottle above -- edge blur runs on the GPU via
    //      GpuAvatarEdgeBlur (a parallel Jump Flooding distance field, not
    //      the old CPU exact-EDT algorithm this throttle-vs-freeze split
    //      was originally designed around), so it's cheap enough now to
    //      treat the same as every other adjustment instead of freezing it
    //      for the whole drag and only catching up on release. ----

    public void SetColorDragging(bool dragging)
    {
        if (!dragging)
        {
            _pendingAdjustmentTimer?.Stop();
            _lastAdjustmentApply = DateTime.UtcNow;
        }
        ApplyImageAdjustments();
    }

    private void OnAdjustmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A null PropertyName is OverlayState's own "more than one property
        // changed at once" convention (see its BeginBatch doc comment) --
        // since any of those could be an adjustment property, treat it as
        // relevant rather than bailing out (which would silently stop the
        // PNG's color reprocessing from ever re-running after a batched
        // Undo/Redo, Match-look, or Reset-look change).
        if (e.PropertyName is not null && !AdjustmentPropertyNames.Contains(e.PropertyName)) return;

        var elapsed = DateTime.UtcNow - _lastAdjustmentApply;
        if (elapsed >= AdjustmentThrottle)
        {
            _pendingAdjustmentTimer?.Stop();
            _lastAdjustmentApply = DateTime.UtcNow;
            ApplyImageAdjustments();
            return;
        }

        // Too soon since the last apply -- schedule a trailing apply for
        // whatever the value ends up being once the throttle window passes,
        // so a fast flurry of ticks still ends up rendering the latest one.
        _pendingAdjustmentTimer ??= new DispatcherTimer();
        _pendingAdjustmentTimer.Stop();
        _pendingAdjustmentTimer.Interval = AdjustmentThrottle - elapsed;
        _pendingAdjustmentTimer.Tick -= OnPendingAdjustmentTick;
        _pendingAdjustmentTimer.Tick += OnPendingAdjustmentTick;
        _pendingAdjustmentTimer.Start();
    }

    private void OnPendingAdjustmentTick(object? sender, EventArgs e)
    {
        _pendingAdjustmentTimer!.Stop();
        _lastAdjustmentApply = DateTime.UtcNow;
        ApplyImageAdjustments();
    }

    /// <summary>Reprocesses the pristine loaded image with the current look
    /// adjustments (edge blur, brightness, contrast, saturation) and displays
    /// the result -- called on load and whenever any adjustment value changes,
    /// not on every _state change (position/size/rotation don't need the image
    /// itself reprocessed). Re-blurs the full buffer only when the radius
    /// actually changed since the last blur (GPU-side, via
    /// ImageAdjustment.BlurPng/GpuAvatarEdgeBlur), and reuses the cached
    /// result for a cheap color-only reapply the rest of the time -- cheap
    /// enough to just let the throttle above handle drag ticks like every
    /// other adjustment, no separate freeze-during-drag needed.</summary>
    private void ApplyImageAdjustments()
    {
        if (_originalPixelBuffer is null) return;

        if (_blurredPixelBuffer is null || _blurredAtRadius != _state.EdgeBlurRadius)
        {
            _blurredPixelBuffer = ImageAdjustment.BlurPng(_originalPixelBuffer, _state.EdgeBlurRadius);
            _blurredAtRadius = _state.EdgeBlurRadius;
        }
        ImageAdjustment.PixelBuffer blurredSource = _blurredPixelBuffer;

        var adjustments = new ImageAdjustment.ColorAdjustments(
            _state.Brightness, _state.Contrast, _state.Saturation,
            _state.Vibrance, _state.Temperature, _state.Tint, _state.Hue,
            _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks,
            _state.ColorTintStrength, _state.ColorTintR, _state.ColorTintG, _state.ColorTintB);
        OverlayImage.Source = ImageAdjustment.ApplyColor(blurredSource, adjustments);
    }

    /// <summary>The loaded image's native pixel size, or null if none is loaded.
    /// Used to fit (not stretch) the image into a detected VRChat frame while
    /// preserving its own aspect ratio.</summary>
    public Size? ImageNativeSize =>
        OverlayImage.Source is BitmapSource bmp ? new Size(bmp.PixelWidth, bmp.PixelHeight) : null;

    /// <summary>The currently-displayed PNG, already reprocessed with the
    /// current edge-blur/brightness/contrast/saturation look adjustments (see
    /// <see cref="ApplyImageAdjustments"/>). Used by Composite mode to render
    /// the exact same look onto a photo instead of the pristine original.</summary>
    public BitmapSource? AdjustedPngSource => OverlayImage.Source as BitmapSource;

    /// <summary>The pristine loaded PNG with none of the look adjustments
    /// applied (no edge blur, no color grading) -- used for Composite mode's
    /// before/after look-adjustment comparison slider, as the "before" half.</summary>
    public BitmapSource? RawPngSource
    {
        get
        {
            if (_originalPixelBuffer is not { } buffer) return null;
            var bitmap = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, buffer.Width, buffer.Height), buffer.Pixels, buffer.Stride, 0);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>The pristine loaded PNG's raw pixel buffer, same source as
    /// <see cref="RawPngSource"/> but without the extra WriteableBitmap
    /// round-trip -- used by the "look match" buttons, which need to run
    /// ComputeLookStats over it directly.</summary>
    public ImageAdjustment.PixelBuffer? OriginalPixelBuffer => _originalPixelBuffer;

    /// <summary>Guide-relevant OverlayState properties -- the only ones
    /// UpdateGuide's inputs actually depend on (fov/pitch/roll come from
    /// either the manual sliders or Unity's live data, gated by
    /// GuideVisible/GuideSyncWithUnity; the guide's own client-rect/roll
    /// inputs come from elsewhere and already call UpdateGuide directly, see
    /// FollowTick/OnUnityGuideDataUpdated). Every OTHER property (position,
    /// size, rotation, opacity, all the look-adjustment sliders) has no
    /// effect on the guide at all, so rebuilding its ~20 Line shapes from
    /// scratch on every one of THOSE changes (which is what an unconditional
    /// UpdateGuide() call here used to do, on every single slider tick) was
    /// pure waste.</summary>
    private static readonly HashSet<string?> GuideRelevantPropertyNames = new()
    {
        nameof(OverlayState.GuideVisible), nameof(OverlayState.GuideSyncWithUnity),
        nameof(OverlayState.GuideManualFov), nameof(OverlayState.GuideManualPitch), nameof(OverlayState.GuideManualRoll),
    };

    /// <summary><paramref name="changedProperty"/> is the OverlayState
    /// property whose change triggered this call, or null for "unknown/many
    /// properties changed" (the initial Loaded call, and OverlayState's own
    /// batched-change notification -- see its BeginBatch doc comment) --
    /// treated as "refresh everything" since there's no single property to
    /// check.</summary>
    public void ApplyState(string? changedProperty = null)
    {
        Canvas.SetLeft(OverlayImage, _state.X);
        Canvas.SetTop(OverlayImage, _state.Y);
        OverlayImage.Width = _state.Width;
        OverlayImage.Height = _state.Height;
        OverlayImage.Opacity = _state.Opacity;
        OverlayImage.Visibility = _state.IsImageVisible ? Visibility.Visible : Visibility.Collapsed;
        ImageRotateTransform.Angle = _state.RotationDegrees;
        ApplyClickThrough(_state.IsClickThrough);
        UpdateHandles();
        if (changedProperty is null || GuideRelevantPropertyNames.Contains(changedProperty))
        {
            UpdateGuide();
        }
    }

    private void UpdateHandles()
    {
        bool show = !_state.IsClickThrough && _state.IsImageVisible;
        HandlesLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        Canvas.SetLeft(HandlesLayer, _state.X);
        Canvas.SetTop(HandlesLayer, _state.Y);
        HandlesLayer.Width = _state.Width;
        HandlesLayer.Height = _state.Height;
        HandlesRotateTransform.Angle = _state.RotationDegrees;

        double w = _state.Width, h = _state.Height, half = 5;
        PlaceHandle(HandleTL, -half, -half);
        PlaceHandle(HandleTR, w - half, -half);
        PlaceHandle(HandleBL, -half, h - half);
        PlaceHandle(HandleBR, w - half, h - half);

        double gizmoHalf = 8; // half of RotateGizmoHandle's own Width/Height (16)
        double gizmoY = -RotateGizmoOffset;
        RotateGizmoLine.X1 = w / 2;
        RotateGizmoLine.Y1 = 0;
        RotateGizmoLine.X2 = w / 2;
        RotateGizmoLine.Y2 = gizmoY + gizmoHalf;
        PlaceHandle(RotateGizmoHandle, w / 2 - gizmoHalf, gizmoY - gizmoHalf);
    }

    private static void PlaceHandle(UIElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    // ---- Unity連携ガイド: FOV/pitch/roll come from either
    //      UnityCameraGuideService's live data (_state.GuideSyncWithUnity)
    //      or the control panel's manual sliders (_state.GuideManualFov/
    //      Pitch/Roll), independently of whether the guide is even shown
    //      (_state.GuideVisible). ----

    private void OnUnityGuideDataUpdated(UnityCameraGuideData data)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _unityGuideData = data;
            UpdateGuide();
        });
    }

    /// <summary>Deliberately does NOT clear _unityGuideData -- staleness
    /// just means Unity isn't currently focused/exporting (see the simple
    /// isApplicationActive gate on the Unity side), which is the NORMAL
    /// state during most actual shooting (you're looking at VRChat/AvaSnap,
    /// not Unity, while composing). The guide should freeze on the last
    /// known angle through that, not vanish -- it only used to null this
    /// out, which made 同期 effectively show nothing the moment you looked
    /// away from Unity to check it.</summary>
    private void OnUnityGuideBecameStale()
    {
        Dispatcher.BeginInvoke(UpdateGuide);
    }

    /// <summary>Redraws the Unity連携ガイド onto the followed VRChat window's
    /// client rect: a horizon line placed via the active fov/pitch/roll
    /// (using the same pinhole formula the old manual-input version used,
    /// y = h/2 + f*tan(pitch), f = (h/2)/tan(fov/2)) plus the same fan/depth
    /// grid lines spaced by FOV. Roll is handled by wrapping the WHOLE grid
    /// in a RotateTransform around the vanishing point (see
    /// UnityGuideRollRotate/RenderTransformOrigin below) instead of
    /// rotating each line's endpoints by hand -- WPF already does that
    /// perfectly well, and it's the exact same visual effect either way.
    /// When synced but Unity hasn't exported anything (yet, or not
    /// recently -- see UnityCameraGuideService's staleness check), there's
    /// simply nothing to draw, so the guide hides rather than falling back
    /// to the manual values behind its own toggle's back.</summary>
    private void UpdateGuide()
    {
        double fov, pitch, roll;
        if (!_state.GuideVisible)
        {
            UnityGuideFrameClip.Visibility = Visibility.Collapsed;
            return;
        }
        if (_state.GuideSyncWithUnity && _unityGuideData is { } data)
        {
            fov = data.Fov;
            pitch = data.Pitch;
            roll = data.Roll;
        }
        else
        {
            // Also covers "synced but Unity has never exported anything
            // this session" (_unityGuideData still null) -- showing nothing
            // there was more confusing than useful (表示 turned ON with no
            // visible result at all, no feedback that anything's wrong).
            // Falling back to the manual values means 表示=ON always shows
            // SOMETHING; the FOVガイド card's Unity連携状況 badge (未接続)
            // is what actually communicates "these aren't live" now.
            fov = _state.GuideManualFov;
            pitch = _state.GuideManualPitch;
            roll = _state.GuideManualRoll;
        }

        if (_lastKnownClientRect is not { Width: > 0, Height: > 0 } clientRect)
        {
            UnityGuideFrameClip.Visibility = Visibility.Collapsed;
            return;
        }

        // Clipped/sized to the estimated CAMERA frame (the same rect the
        // avatar itself gets fitted to on 位置をリセット -- see
        // VRChatWindowService.ComputeCameraFrameRect's own doc comment),
        // not the whole VRChat window: the window includes UI chrome
        // (camera controls, borders) around the actual photographed area,
        // and a perspective guide for anything outside that isn't
        // meaningful. The clip itself lives on UnityGuideFrameClip (see its
        // own XAML comment on why that has to be the OUTER, un-rotated
        // element) -- UnityGuideCanvas underneath it stays at local (0,0)
        // and only carries the roll RenderTransform.
        var (frameLeft, frameTop, frameWidth, frameHeight) =
            VRChatWindowService.ComputeCameraFrameRect(clientRect, _oscListener.IsLandscape ?? true);

        double w = frameWidth, h = frameHeight;
        Canvas.SetLeft(UnityGuideFrameClip, frameLeft);
        Canvas.SetTop(UnityGuideFrameClip, frameTop);
        UnityGuideFrameClip.Width = w;
        UnityGuideFrameClip.Height = h;
        UnityGuideFrameClip.Visibility = Visibility.Visible;
        UnityGuideCanvas.Width = w;
        UnityGuideCanvas.Height = h;
        UnityGuideCanvas.Children.Clear();

        double centerX = w / 2.0;
        // Clamped to a minimum of 1 degree -- fov=0 makes Math.Tan(0)=0, so
        // f (a divide by that) becomes Infinity, and Infinity * Math.Tan(0)
        // (pitch=0 too, a common case) is NaN, not Infinity -- propagating
        // that into RenderTransformOrigin crashed rather than just failing
        // to render. The manual FOV slider itself now has Minimum="1" too,
        // but the synced value comes straight from Unity's own
        // Camera.fieldOfView with no range guarantee on this side, so the
        // clamp stays here as the real backstop.
        double fovRad = Math.Max(fov, 1.0) * Math.PI / 180.0;
        double f = (h / 2.0) / Math.Tan(fovRad / 2.0);
        double horizonY = h / 2.0 + f * Math.Tan(pitch * Math.PI / 180.0);

        UnityGuideCanvas.RenderTransformOrigin = new Point(0.5, h > 0 ? horizonY / h : 0.5);
        UnityGuideRollRotate.Angle = -roll;

        // Bright yellow-green ("GreenYellow") instead of the app's own
        // muted blue accent -- this overlays real VRChat scenery (skin
        // tones, indoor lighting, etc.), where a low-contrast brand color
        // easily gets lost; a saturated, unusual-in-nature color reads
        // clearly against almost any background.
        var lineBrush = new SolidColorBrush(Color.FromRgb(0xAD, 0xFF, 0x2F));
        lineBrush.Freeze();

        const double PrimaryThickness = 3.0; // the horizon line: the one reference everything else is relative to, so it reads as the "main" line at a glance
        const double SecondaryThickness = 1.75; // fan/depth lines
        var secondaryDashArray = new DoubleCollection { 4, 3 };
        secondaryDashArray.Freeze();

        void AddLine(double x1, double y1, double x2, double y2, double opacity, bool isPrimary = false)
        {
            var main = new System.Windows.Shapes.Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = lineBrush, StrokeThickness = isPrimary ? PrimaryThickness : SecondaryThickness,
                Opacity = opacity, IsHitTestVisible = false,
            };
            // Dashed for the secondary fan/depth lines only -- keeps the
            // horizon (the one thing every other line is measured against)
            // visually distinct as the sole solid line, and reduces how much
            // the fan/depth grid competes with it for attention.
            if (!isPrimary) main.StrokeDashArray = secondaryDashArray;
            UnityGuideCanvas.Children.Add(main);
        }

        const double angleStepDeg = 15;
        const double maxAngleDeg = 75;

        // Every line below is defined to exactly reach the UNROTATED (w,h)
        // box's own edges -- fine at roll=0, but a same-size box rotated
        // around (centerX, horizonY) no longer covers the TRUE (axis-
        // aligned) frame's corners (a rotated rectangle pulls in from its
        // original corners even as it pokes out past the middle of its
        // original edges), so endpoints landing exactly on the unrotated
        // box's edge fall visibly short of the real frame's corners once
        // actually rotated. Extending every "far" endpoint well past where
        // it's actually needed, then letting UnityGuideFrameClip's own
        // (axis-aligned, unrotated) clip trim the excess, means the drawn
        // lines always reach the true frame edges regardless of roll.
        const double ExtendFactor = 2.5;
        Point Extend(double x, double y) => new Point(
            centerX + (x - centerX) * ExtendFactor,
            horizonY + (y - horizonY) * ExtendFactor);

        // For the fan lines above, Extend is correct: they radiate FROM the
        // origin at a fixed angle, so scaling their (x,y) offset from the
        // origin by the same factor on both axes preserves that angle while
        // reaching further out. The depth rows and horizon line below are
        // different -- they're horizontal at a FIXED height (dy from the
        // horizon), not radiating from the origin -- so scaling their Y
        // offset the same way as X pushed their height itself 2.5x further
        // from the horizon than the FOV math intended, which is exactly why
        // rows that should've stayed inside the frame (low FOV -> large f
        // -> large dy -> even larger once wrongly multiplied) ended up
        // pushed outside it. These only need the X reach extended.
        Point ExtendHorizontal(double x, double y) => new Point(centerX + (x - centerX) * ExtendFactor, y);

        for (double angleDeg = -maxAngleDeg; angleDeg <= maxAngleDeg; angleDeg += angleStepDeg)
        {
            double x = centerX + f * Math.Tan(angleDeg * Math.PI / 180.0);
            if (x < -w || x > w * 2) continue;
            var farBottom = Extend(x, h);
            var farTop = Extend(x, 0);
            AddLine(centerX, horizonY, farBottom.X, farBottom.Y, 0.45);
            AddLine(centerX, horizonY, farTop.X, farTop.Y, 0.45);
        }

        // Depth rows: a fixed angle step (matching the fan lines above) looks
        // right at a "typical" FOV, but at a narrow FOV f grows large, so
        // even the very first step (angleStepDeg) already lands past the
        // frame edge -- the old "only draw it if it lands within [0,h]"
        // filter then rejects that row outright, and at narrow enough FOV
        // rejects EVERY row, leaving nothing but the horizon line. Instead,
        // solve for the actual angle that reaches each edge of the frame
        // (atan of the real distance-to-edge over f) and split that into a
        // fixed row COUNT, so rows are mathematically guaranteed to land
        // within the frame regardless of FOV -- no filter needed, and the
        // grid always stays populated.
        const int depthRowCount = 5;
        double angleToBottomRad = Math.Atan2(Math.Max(0, h - horizonY), f);
        double angleToTopRad = Math.Atan2(Math.Max(0, horizonY), f);

        for (int i = 1; i <= depthRowCount; i++)
        {
            double yBelow = horizonY + f * Math.Tan(angleToBottomRad * i / depthRowCount);
            var a = ExtendHorizontal(0, yBelow);
            var b = ExtendHorizontal(w, yBelow);
            AddLine(a.X, a.Y, b.X, b.Y, 0.45);

            double yAbove = horizonY - f * Math.Tan(angleToTopRad * i / depthRowCount);
            var a2 = ExtendHorizontal(0, yAbove);
            var b2 = ExtendHorizontal(w, yAbove);
            AddLine(a2.X, a2.Y, b2.X, b2.Y, 0.45);
        }

        {
            var a = ExtendHorizontal(0, horizonY);
            var b = ExtendHorizontal(w, horizonY);
            AddLine(a.X, a.Y, b.X, b.Y, 0.7, isPrimary: true);
        }
    }

    // ---- Click-through (WS_EX_TRANSPARENT) ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private void ApplyClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = clickThrough ? (style | WS_EX_TRANSPARENT | WS_EX_LAYERED) : (style & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    // ---- Click-through hotkey: click-through is ON by default (clicks pass
    //      through to VRChat for normal play); holding Shift temporarily turns
    //      it OFF so the overlay can be dragged/resized, and releasing it goes
    //      back to click-through. Polled via GetAsyncKeyState instead of a
    //      KeyDown/KeyUp handler because once click-through is active this
    //      window can't receive input at all (that's the point of
    //      WS_EX_TRANSPARENT) and VRChat -- not this window -- holds keyboard
    //      focus, so only a system-wide key-state check sees Shift being held. ----

    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool _lastShiftDown;

    // ---- Undo/Redo while VRChat has focus: Ctrl+Z only reaches our own
    //      PreviewKeyDown handlers when one of AvaSnap's own windows has
    //      keyboard focus -- normally VRChat does, since that's the whole
    //      point of the overlay. Deliberately NOT a global hotkey
    //      (RegisterHotKey/a low-level keyboard hook): that would hijack
    //      Ctrl+Z system-wide, breaking undo in every other app while AvaSnap
    //      is running. Polling + checking the foreground window ourselves
    //      never blocks/consumes the keystroke, so it stays harmless outside
    //      VRChat -- same non-intrusive approach as the Shift click-through
    //      poll above. ----

    private const int VK_CONTROL = 0x11;
    private const int VK_Z = 0x5A;
    private const int VK_Y = 0x59;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private DispatcherTimer? _hotkeyPollTimer;
    private bool _lastCtrlZDown, _lastCtrlShiftZDown, _lastCtrlYDown;

    /// <summary>One timer instead of two separate ones (Shift click-through
    /// used to poll every 30ms, undo/redo every 50ms, both calling
    /// GetAsyncKeyState(VK_SHIFT) independently) -- both are cheap per-tick
    /// P/Invoke checks, so there's no reason to run two perpetual
    /// DispatcherTimers for the app's entire lifetime instead of one.</summary>
    private void StartHotkeyPolling()
    {
        _hotkeyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _hotkeyPollTimer.Tick += (_, _) =>
        {
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (shift != _lastShiftDown)
            {
                _lastShiftDown = shift;
                _state.IsClickThrough = !shift;
            }

            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool z = (GetAsyncKeyState(VK_Z) & 0x8000) != 0;
            bool y = (GetAsyncKeyState(VK_Y) & 0x8000) != 0;

            bool ctrlZDown = ctrl && z && !shift;
            bool ctrlShiftZDown = ctrl && shift && z;
            bool ctrlYDown = ctrl && y;

            // Edge-triggered (only on the transition into "held"), and only
            // while VRChat is the foreground window -- AvaSnap's own windows
            // already get Ctrl+Z via their normal PreviewKeyDown handlers, so
            // this only needs to cover the VRChat-focused case.
            if (_followedHwnd is { } hwnd && GetForegroundWindow() == hwnd)
            {
                if (ctrlShiftZDown && !_lastCtrlShiftZDown) _undo.Redo();
                else if (ctrlZDown && !_lastCtrlZDown) _undo.Undo();
                if (ctrlYDown && !_lastCtrlYDown) _undo.Redo();
            }

            _lastCtrlZDown = ctrlZDown;
            _lastCtrlShiftZDown = ctrlShiftZDown;
            _lastCtrlYDown = ctrlYDown;
        };
        _hotkeyPollTimer.Start();
    }

    // ---- Z-order: stay attached to the VRChat window instead of being
    //      globally "always on top" ----

    /// <summary>
    /// Makes this overlay an "owned" window of the VRChat window (see
    /// <see cref="WindowOwnership"/>) and starts following its position.
    /// </summary>
    public void AttachToOwner(IntPtr ownerHwnd)
    {
        WindowOwnership.SetOwner(this, ownerHwnd);
        StartFollowing(ownerHwnd);
    }

    // ---- Follow: keep the overlay's position locked relative to the VRChat
    //      window if the user drags that window somewhere else on screen.
    //
    // Rather than only polling, this hooks EVENT_OBJECT_LOCATIONCHANGE so the
    // OS notifies us the instant VRChat's window moves -- effectively a lock
    // (no perceptible lag) without the fragility of forcibly reparenting our
    // window into another process's window tree via SetParent, which is not
    // designed to host arbitrary foreign top-level windows and can misbehave.
    // A slow safety-net timer covers the rare case an out-of-context WinEvent
    // gets dropped under load. ----

    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>The followed VRChat window's client rect as of the last known
    /// move/resize, cached instead of re-queried -- callers that just need "roughly
    /// where is VRChat right now" (e.g. the control panel's position display)
    /// should use this instead of their own fresh FindVRChatWindow() + P/Invoke
    /// lookup, which is expensive enough that calling it on every _state
    /// PropertyChanged during a live window drag (many times a second, once per
    /// WinEventHook callback) noticeably backs up the UI thread and made the
    /// overlay visibly lag/jump while VRChat's window was being moved.</summary>
    public System.Drawing.Rectangle? FollowedClientRect => _lastKnownClientRect;

    /// <summary>The VRChat window currently being followed, or null if not
    /// attached yet.</summary>
    public IntPtr? FollowedHwnd => _followedHwnd;

    private IntPtr? _followedHwnd;
    private System.Drawing.Rectangle? _lastKnownClientRect;
    private DispatcherTimer? _followSafetyNetTimer;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate; // keep alive: GC would otherwise collect it while native code holds the pointer

    private void StartFollowing(IntPtr hwnd)
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _followedHwnd = hwnd;
        _lastKnownClientRect = VRChatWindowService.GetClientRectOnScreen(hwnd);

        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        _winEventDelegate = OnWinEvent;
        _winEventHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            _winEventDelegate, processId, threadId, WINEVENT_OUTOFCONTEXT);

        if (_followSafetyNetTimer is null)
        {
            _followSafetyNetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _followSafetyNetTimer.Tick += (_, _) => FollowTick();
            _followSafetyNetTimer.Start();
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != 0) return; // ignore sub-elements, only the window itself
        if (_followedHwnd is not { } followed || hwnd != followed) return;
        FollowTick();
    }

    /// <summary>Fires when the followed VRChat window's client SIZE changes
    /// (resize/maximize/restore) -- passes the already-known hwnd and fresh
    /// client rect so subscribers can react without their own fresh
    /// FindVRChatWindow() scan (that EnumWindows-based lookup is what made the
    /// overlay lag during rapid WinEventHook callbacks before).</summary>
    public event Action<IntPtr, System.Drawing.Rectangle>? ClientResized;

    /// <summary>Tracks the VRChat window's on-screen position, shifting the
    /// overlay by however far the window's top-left corner moved, and raises
    /// <see cref="ClientResized"/> when the size itself changes (no more
    /// auto-rescale math here -- callers decide what to do, e.g. re-run the
    /// position estimate).</summary>
    private void FollowTick()
    {
        if (_followedHwnd is not { } hwnd) return;
        var current = VRChatWindowService.GetClientRectOnScreen(hwnd);
        if (current is null) return; // window closed/minimized; leave overlay where it is

        if (_lastKnownClientRect is { } last)
        {
            int dx = current.Value.Left - last.Left;
            int dy = current.Value.Top - last.Top;
            if (dx != 0 || dy != 0)
            {
                _state.X += dx;
                _state.Y += dy;
            }

            if (current.Value.Width != last.Width || current.Value.Height != last.Height)
            {
                ClientResized?.Invoke(hwnd, current.Value);
            }
        }
        _lastKnownClientRect = current;
        UpdateGuide();
    }

    // ---- Mouse interaction: left-drag = move ----

    private void OverlayImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _undo.BeginChange();
        _isDraggingMove = true;
        _dragStartMouse = e.GetPosition(this);
        _dragStartX = _state.X;
        _dragStartY = _state.Y;
        OverlayImage.CaptureMouse();
    }

    private void OverlayImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingMove = false;
        _undo.CommitChange();
        OverlayImage.ReleaseMouseCapture();
    }

    private void OverlayImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingMove) return;
        var current = e.GetPosition(this);
        _state.X = _dragStartX + (current.X - _dragStartMouse.X);
        _state.Y = _dragStartY + (current.Y - _dragStartMouse.Y);
    }

    // ---- Resize handles: 4 corner grips, rotation-aware (mouse movement in
    //      screen space is un-rotated into the overlay's local space before
    //      being applied, so dragging a corner still resizes along the
    //      overlay's own axes even when it's rotated) ----

    private const double MinHandleSize = 20;

    private bool _isDraggingHandle;
    private string? _activeHandleTag;
    private Point _handleDragStartMouse;
    private double _handleStartX, _handleStartY, _handleStartWidth, _handleStartHeight;

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle handle) return;
        _undo.BeginChange();
        _isDraggingHandle = true;
        _activeHandleTag = handle.Tag as string;
        _handleDragStartMouse = e.GetPosition(this);
        _handleStartX = _state.X;
        _handleStartY = _state.Y;
        _handleStartWidth = _state.Width;
        _handleStartHeight = _state.Height;
        handle.CaptureMouse();
        e.Handled = true; // don't let this bubble up to OverlayImage's move-drag handler
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingHandle = false;
        _activeHandleTag = null;
        _undo.CommitChange();
        if (sender is System.Windows.Shapes.Rectangle handle) handle.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingHandle || _activeHandleTag is null) return;

        var current = e.GetPosition(this);
        double screenDx = current.X - _handleDragStartMouse.X;
        double screenDy = current.Y - _handleDragStartMouse.Y;

        // Un-rotate the screen-space drag delta into the overlay's own local
        // axes, so dragging a corner still scales along the overlay's own
        // rotated axes even when it's rotated.
        double rad = -_state.RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double localDx = screenDx * cos - screenDy * sin;
        double localDy = screenDx * sin + screenDy * cos;

        bool left = _activeHandleTag.Contains('L');
        bool top = _activeHandleTag.Contains('T');

        // Locked-aspect corner resize, scaled from the box's center: project
        // the drag onto the corner's own diagonal (center -> the corner's
        // starting position) to get a single continuous scale factor, instead
        // of comparing width-driven vs height-driven candidates each frame --
        // that comparison flip-flopped near the diagonal (the natural drag
        // direction for a corner) and felt jittery.
        double halfW0 = _handleStartWidth / 2;
        double halfH0 = _handleStartHeight / 2;
        double cornerDist0 = Math.Sqrt(halfW0 * halfW0 + halfH0 * halfH0);
        if (cornerDist0 <= 0) return;

        double dirX = (left ? -halfW0 : halfW0) / cornerDist0;
        double dirY = (top ? -halfH0 : halfH0) / cornerDist0;
        double projected = localDx * dirX + localDy * dirY;

        double scale = (cornerDist0 + projected) / cornerDist0;
        if (scale <= 0) return; // dragged past center; ignore rather than invert

        // Lock to the loaded image's own native aspect ratio when available --
        // more robust than the box's current W/H, which could have drifted
        // from the image's true ratio (rounding, or an earlier manual edit).
        double aspect = ImageNativeSize is { Width: > 0, Height: > 0 } native
            ? native.Width / native.Height
            : _handleStartWidth / _handleStartHeight;

        double newWidth = _handleStartWidth * scale;
        double newHeight = newWidth / aspect;

        if (newWidth < MinHandleSize || newHeight < MinHandleSize) return;

        double centerX = _handleStartX + _handleStartWidth / 2;
        double centerY = _handleStartY + _handleStartHeight / 2;
        _state.Width = newWidth;
        _state.Height = newHeight;
        _state.X = centerX - newWidth / 2;
        _state.Y = centerY - newHeight / 2;
    }

    // ---- Rotate gizmo: drag the handle above the box in an arc around the
    //      box's center to rotate. Same soft-snap targets as the control
    //      panel's rotation slider, for a consistent feel between the two. ----

    private const double RotateGizmoOffset = 30;

    private bool _isDraggingRotateGizmo;
    private double _rotateGizmoStartAngle;
    private double _rotateGizmoStartRotation;

    private void RotateGizmo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _undo.BeginChange();
        _isDraggingRotateGizmo = true;
        var mouse = e.GetPosition(this);
        double centerX = _state.X + _state.Width / 2;
        double centerY = _state.Y + _state.Height / 2;
        _rotateGizmoStartAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        _rotateGizmoStartRotation = _state.RotationDegrees;
        RotateGizmoHandle.CaptureMouse();
        e.Handled = true;
    }

    private void RotateGizmo_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRotateGizmo) return;

        var mouse = e.GetPosition(this);
        double centerX = _state.X + _state.Width / 2;
        double centerY = _state.Y + _state.Height / 2;
        double currentAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        double newRotation = _rotateGizmoStartRotation + (currentAngle - _rotateGizmoStartAngle);

        newRotation = SoftSnapAngle(newRotation, 5, -180, -90, 0, 90, 180);
        _state.RotationDegrees = newRotation;
    }

    private void RotateGizmo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRotateGizmo = false;
        _undo.CommitChange();
        RotateGizmoHandle.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static double SoftSnapAngle(double value, double tolerance, params double[] targets)
    {
        foreach (var target in targets)
        {
            if (Math.Abs(value - target) <= tolerance) return target;
        }
        return value;
    }
}
