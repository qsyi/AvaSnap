using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AvaSnap.Services;

namespace AvaSnap.Views;

/// <summary>Composite-mode-only state that isn't part of OverlayState (photo
/// look, grain/vignette, and where the avatar sits on the photo) -- folded
/// into UndoManager's single timeline via CaptureExtra/ApplyExtra so Ctrl+Z
/// covers it too, alongside the avatar-image look it already tracked.</summary>
public sealed record CompositeSnapshot(
    double PhotoBrightness, double PhotoContrast, double PhotoSaturation,
    double PhotoVibrance, double PhotoTemperature, double PhotoTint, double PhotoHue,
    double PhotoHighlights, double PhotoShadows, double PhotoWhites, double PhotoBlacks,
    double PhotoColorTintStrength, byte PhotoColorTintR, byte PhotoColorTintG, byte PhotoColorTintB,
    double PhotoBlurAmount,
    double GrainAmount, double VignetteAmount,
    double SoftnessAmount, double SharpnessAmount,
    double FadeAmount, double GlowAmount,
    double ChromaticAberrationAmount, double ColorBleedAmount, double ScanlineAmount,
    double ClarityAmount, double LightLeakAmount, double LightLeakAngle, double LightLeakDistance,
    byte LightLeakColorB, byte LightLeakColorG, byte LightLeakColorR,
    double ToneGradientAmount, double ToneGradientRotation,
    byte ToneGradientLightR, byte ToneGradientLightG, byte ToneGradientLightB,
    byte ToneGradientDarkR, byte ToneGradientDarkG, byte ToneGradientDarkB,
    double DropShadowAmount, double DropShadowDirection, double DropShadowDistance, double DropShadowBlur,
    byte DropShadowColorB, byte DropShadowColorG, byte DropShadowColorR,
    ImageAdjustment.DropShadowBlendMode DropShadowBlendMode,
    double? CanvasAspectRatio, double CanvasCropOffsetX, double CanvasCropOffsetY, double CanvasCropWidthPercent, double CanvasCropHeightPercent,
    double CompositePlaceX, double CompositePlaceY, double CompositePlaceWidth, double CompositePlaceHeight,
    double CompositeRotation);

public partial class ControlPanelWindow : Window
{
    private readonly OverlayState _state;
    private readonly OverlayWindow _overlayWindow;
    private readonly UndoManager _undo;
    private readonly VrChatOscListener _oscListener;
    private readonly ScreenshotWatcherService _screenshotWatcher;
    private readonly UnityCameraGuideService _unityCameraGuide;
    private bool _suppressEvents;

    public ControlPanelWindow(OverlayState state, OverlayWindow overlayWindow, UndoManager undo, VrChatOscListener oscListener, ScreenshotWatcherService screenshotWatcher, UnityCameraGuideService unityCameraGuide)
    {
        _state = state;
        _overlayWindow = overlayWindow;
        _undo = undo;
        _oscListener = oscListener;
        _screenshotWatcher = screenshotWatcher;
        _unityCameraGuide = unityCameraGuide;
        _suppressEvents = true;
        InitializeComponent();
        ThemeToggleButton.IsChecked = ThemeService.IsDarkMode;
        _suppressEvents = false;
        _defaultMinWidth = MinWidth;
        _defaultMinHeight = MinHeight;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        TitleBarVersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = 40;

        // FOVガイドの「Unity連携状況」表示: DataUpdated fires on
        // UnityCameraGuideService's own thread context via its
        // Dispatcher.BeginInvoke calls, so no extra marshaling needed here.
        // Unity is request-driven (see RequestGuideButton_Click/
        // UnityCameraGuideService.RequestUpdate); no more "同期" toggle --
        // a successful fetch just writes into GuideManualFov/Pitch/Roll
        // directly, the same as typing a value in by hand. OverlayWindow
        // picks this up on its own via _state.PropertyChanged, same as
        // every other OverlayState field; no separate DataUpdated
        // subscription needed over there any more.
        _unityCameraGuide.DataUpdated += data =>
        {
            UpdateUnityConnectionStatus(hasFetched: true);
            _state.GuideManualFov = data.Fov;
            _state.GuideManualPitch = data.Pitch;
            _state.GuideManualRoll = data.Roll;
            _suppressEvents = true;
            RefreshGuideManualDisplay();
            _suppressEvents = false;
        };
        UpdateUnityConnectionStatus(hasFetched: false);

        _state.PropertyChanged += (_, e) => { RefreshFromState(e.PropertyName); ScheduleCompositeRender(); };
        RefreshFromState();
        RefreshWatchFolderText();
        RefreshPhotoLookUI();
        RefreshFinishUI();
        RefreshSkipAvatarUI();
        PreviewKeyDown += ControlPanelWindow_PreviewKeyDown;

        // Set here (after InitializeComponent), not via IsChecked="True" in
        // XAML directly: that fired LookLinkToggle_Changed synchronously
        // WHILE still parsing the rest of the XAML, before LookLinkConnector
        // (declared later in the file) was assigned to its field yet --
        // EnsureLookLinkAdorner crashed on a null reference. This still fires
        // the same Checked handler, but only once every named element in the
        // file actually exists. CompositePanel is still Collapsed at this
        // point, though, so the resulting label/adorner positions won't be
        // meaningful yet -- ShowComposite's own UpdateLinkedRowStyles call
        // fixes that up for real the first time the user actually opens
        // Composite mode.
        LookLinkToggle.IsChecked = true;

        // Fold the composite-mode-only fields into the shared undo timeline
        // (see CompositeSnapshot) so Ctrl+Z covers photo look/grain/vignette/
        // placement too, not just the avatar-image look already in OverlayState.
        _undo.CaptureExtra = CaptureCompositeSnapshot;
        _undo.ApplyExtra = ApplyCompositeSnapshot;
        _undo.Applied += OnUndoRedoApplied;

        // Re-apply the position estimate automatically when the VRChat
        // window's size changes (resize/maximize/restore) or its orientation
        // changes -- the estimate depends on both, so the old position is
        // stale the moment either one does. Neither needs a full re-attach
        // (Z-order + WinEventHook are already established); they just
        // reapply the estimate using the already-known hwnd/rect.
        _overlayWindow.ClientResized += OnVrChatClientResized;
        _oscListener.OrientationChanged += OnOscOrientationChanged;

        // The overlay itself stays hidden until VRChat's camera is confirmed
        // open (see OverlayWindow.InitializeCameraVisibility/ApplyCameraOpenState),
        // which is invisible and confusing if the user doesn't already know
        // that -- show an unmissable banner in Align mode whenever it isn't
        // confirmed open, not just when it's confirmed closed.
        _oscListener.CameraModeChanged += (open) => Dispatcher.Invoke(() => UpdateCameraBanner(open));
        UpdateCameraBanner(_oscListener.IsCameraOpen);

        ShowHome();
    }

    /// <summary>Makes the control panel an "owned" window of the VRChat window
    /// (see <see cref="WindowOwnership"/>), so it comes forward along with
    /// VRChat too instead of getting buried behind it when the user clicks
    /// back into the game.</summary>
    public void AttachToOwner(IntPtr ownerHwnd) => WindowOwnership.SetOwner(this, ownerHwnd);

    /// <summary>Only Visible when the camera is NOT confirmed open (either
    /// confirmed closed, or unknown because VRChat hasn't reported anything
    /// via OSC yet) -- both cases mean the live overlay is currently hidden,
    /// so Align mode is otherwise showing an empty VRChat window with no clue
    /// why nothing appears there.</summary>
    private void UpdateCameraBanner(bool? isOpen)
    {
        CameraClosedBanner.Visibility = isOpen == true ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Navigation: a compact home screen picks between the two modes, each
    //      sized appropriately for its own content (composite mode needs real
    //      room for a photo preview, so it opens larger than the home/align
    //      screens). ----

    // HomeSettingsPanel floats over the top-right corner of Home (anchored
    // below the ⚙️ button, not inside HomePanel's centered StackPanel).
    // Opening it no longer resizes the window -- it just overlays the mode
    // cards underneath, like an ordinary dropdown -- so Home stays a
    // constant size regardless of whether it's open.
    private const double HomeHeight = 460;

    private void ShowHome() => WithRedrawSuspended(() =>
    {
        HomePanel.Visibility = Visibility.Visible;
        AlignPanel.Visibility = Visibility.Collapsed;
        CompositePanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        LicensePanel.Visibility = Visibility.Collapsed;
        TitleBarMinimizeButton.Visibility = Visibility.Collapsed;
        TitleBarMaximizeButton.Visibility = Visibility.Visible;
        HomeSettingsToggle.Visibility = Visibility.Visible;
        Width = 400;
        Height = HomeHeight;
        PinToRightEdge();

        // Home isn't for positioning anything, so the live overlay just
        // sits on top of VRChat unhelpfully here regardless of whether its
        // camera UI happens to be open -- SetManuallyHidden (not a plain
        // Hide()) also keeps it suppressed if the camera opens while still
        // on Home, see its own doc comment.
        _overlayWindow.SetManuallyHidden(true);
    });

    // ⚙️ only makes sense on Home (the watch folder isn't a per-mode
    // setting), so every other Show*/EnterCompact hides both it and its
    // dropdown -- otherwise they'd float on top of Align/Composite/Compact
    // too, since neither is nested inside HomePanel's own Visibility toggle.
    private void HideHomeSettings()
    {
        HomeSettingsToggle.Visibility = Visibility.Collapsed;
        HomeSettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowAlign() => WithRedrawSuspended(() =>
    {
        HomePanel.Visibility = Visibility.Collapsed;
        AlignPanel.Visibility = Visibility.Visible;
        CompositePanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        LicensePanel.Visibility = Visibility.Collapsed;
        TitleBarMinimizeButton.Visibility = Visibility.Visible;
        TitleBarMaximizeButton.Visibility = Visibility.Visible;
        HideHomeSettings();
        Width = 440;
        Height = 880;
        PinToRightEdge();

        // Un-suppresses whatever ShowHome/EnterCompact set -- re-syncs to
        // VRChat's actual current camera state rather than assuming open.
        _overlayWindow.SetManuallyHidden(false);
    });

    private void ShowComposite()
    {
        WithRedrawSuspended(() =>
        {
            HomePanel.Visibility = Visibility.Collapsed;
            AlignPanel.Visibility = Visibility.Collapsed;
            CompositePanel.Visibility = Visibility.Visible;
            CompactPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            LicensePanel.Visibility = Visibility.Collapsed;
            TitleBarMinimizeButton.Visibility = Visibility.Visible;
            TitleBarMaximizeButton.Visibility = Visibility.Visible;
            HideHomeSettings();
            // Rescanned fresh every time this mode is entered (not cached),
            // so it always reflects whatever's actually in the watch folder
            // right now rather than a snapshot from whenever it was last
            // refreshed.
            RefreshRecentPhotosUI();
            // Close to the full work area, just a bit smaller -- this mode
            // benefits the most from extra room (photo preview + two columns
            // of controls), unlike Home/Align which are single narrow
            // columns.
            Width = SystemParameters.WorkArea.Width - 60;
            Height = SystemParameters.WorkArea.Height - 60;
            PinToRightEdge();

            // The actual render (see below) is deferred rather than done
            // right here: it can be genuinely slow on a large photo (the
            // distance-transform-based edge blur especially), and
            // WithRedrawSuspended holds off repainting until this WHOLE
            // action finishes, so showing the loading text from inside here
            // wouldn't do anything -- it couldn't paint until the slow call
            // already returned, same as not showing it at all.
            ShowCompositeLoading();
        });

        // Now that WithRedrawSuspended's action above has finished and
        // repainted once (showing the loading text), queue the actual render
        // for right after. ApplicationIdle (lower priority than the
        // Background this used before) only runs once the dispatcher queue
        // is genuinely empty of every higher-priority item, which includes
        // WPF's own Render-priority layout/paint pass -- a stronger
        // guarantee that the window (loading spinner and all) has actually
        // finished being shown before this heavy computation starts, versus
        // Background alone (which could in principle still race a slow
        // first-time layout pass over the newly-Visible CompositePanel).
        // Also (re-)establishes 一括調整's label highlighting/connector
        // adorner here rather than at construction time: _lookLinked/
        // LookLinkToggle both default to on, but EnsureLookLinkAdorner/
        // PositionLookLinkConnector need an actual layout pass over
        // CompositePanel to have happened (for AdornerLayer.GetAdornerLayer
        // and TranslatePoint to give sensible answers), which doesn't happen
        // while it's Collapsed -- true from construction until the user
        // opens Composite mode for the first time, hence doing it here
        // instead.
        Dispatcher.InvokeAsync(() =>
        {
            UpdateLinkedRowStyles();
            FinishMatchRender();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Shows CompositeLoadingPanel (spinner + text) and starts its
    /// rotation. Paired with <see cref="HideCompositeLoading"/> -- always
    /// call both rather than setting CompositeLoadingPanel.Visibility
    /// directly, or the spinner keeps spinning (wasting a little CPU)
    /// forever after the panel is hidden.</summary>
    private void ShowCompositeLoading()
    {
        CompositeLoadingPanel.Visibility = Visibility.Visible;
        ((Storyboard)FindResource("CompositeLoadingSpinStoryboard")).Begin(this, isControllable: true);
    }

    private void HideCompositeLoading()
    {
        CompositeLoadingPanel.Visibility = Visibility.Collapsed;
        ((Storyboard)FindResource("CompositeLoadingSpinStoryboard")).Stop(this);
    }

    /// <summary>Each mode has a different Width, but WPF resizing keeps the
    /// LEFT edge fixed -- switching from Home (420 wide) to a wider mode
    /// (Align 760 / Composite 1080) without also moving Left just grows the
    /// window off the right edge of the screen. Re-anchors the right edge at
    /// the same 20px-from-the-work-area-edge spot the window starts at,
    /// every time the width changes; clamps to the work area's left edge too,
    /// for a mode wider than the whole screen.</summary>
    private void PinToRightEdge()
    {
        double left = SystemParameters.WorkArea.Right - Width - 20;
        if (left < SystemParameters.WorkArea.Left) left = SystemParameters.WorkArea.Left;
        Left = left;
    }

    // ---- Mode switches change Width/Height/Left and toggle several panels'
    //      Visibility as separate property writes, each of which can trigger
    //      its own native window move/resize/repaint -- visible as a brief
    //      flicker (grow-then-reposition, or old-panel-gone-before-new-panel-
    //      shown). WM_SETREDRAW suppresses repainting the native window for
    //      the whole batch of changes, then forces exactly one repaint at the
    //      end once everything's already in its final state. ----

    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private void WithRedrawSuspended(Action action)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            action();
            return;
        }
        SendMessage(hwnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            action();
        }
        finally
        {
            SendMessage(hwnd, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
        }
    }

    private void AlignModeButton_Click(object sender, RoutedEventArgs e) => ShowAlign();

    private void CompositeModeButton_Click(object sender, RoutedEventArgs e) => ShowComposite();

    private void BackToHome_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void HomeSettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        HomeSettingsPanel.Visibility = HomeSettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool _aboutContentLoaded;
    private bool _licenseContentLoaded;

    private void AboutButton_Click(object sender, RoutedEventArgs e) => ShowAbout();
    private void LicenseButton_Click(object sender, RoutedEventArgs e) => ShowLicense();
    private void TitleBarUpdateButton_Click(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>PatchNotesText is populated once, the first time this opens,
    /// from PATCHNOTES.md -- embedded as a WPF resource (see the csproj) so
    /// it's readable from inside the shipped exe with no network access
    /// needed. License/third-party notices live in the separate
    /// LicensePanel instead (see ShowLicense).</summary>
    private void ShowAbout() => WithRedrawSuspended(() =>
    {
        if (!_aboutContentLoaded)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            AboutVersionText.Text = $"バージョン {version.Major}.{version.Minor}.{version.Build}";
            PatchNotesText.Text = LoadEmbeddedText("Assets/PATCHNOTES.md");
            _aboutContentLoaded = true;
        }

        // The title-bar update button IS the notification (see
        // ShowUpdateAvailableNotification) -- moot now that the user is
        // looking straight at the update section this opens into.
        TitleBarUpdateButton.Visibility = Visibility.Collapsed;

        HomePanel.Visibility = Visibility.Collapsed;
        AlignPanel.Visibility = Visibility.Collapsed;
        CompositePanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Visible;
        LicensePanel.Visibility = Visibility.Collapsed;
        TitleBarMinimizeButton.Visibility = Visibility.Collapsed;
        TitleBarMaximizeButton.Visibility = Visibility.Visible;
        HideHomeSettings();
        Width = 440;
        Height = 640;
        PinToRightEdge();

        _ = RefreshUpdateSectionAsync();
    });

    /// <summary>LicenseText/ThirdPartyNoticesText are populated once, the
    /// first time this opens, from LICENSE.md/THIRD-PARTY-NOTICES.md --
    /// embedded as WPF resources (see the csproj) so they're readable from
    /// inside the shipped exe with no network access needed, which several
    /// of the notices in there (SIL OFL, MIT) require.</summary>
    private void ShowLicense() => WithRedrawSuspended(() =>
    {
        if (!_licenseContentLoaded)
        {
            LicenseText.Text = LoadEmbeddedText("Assets/LICENSE.md");
            ThirdPartyNoticesText.Text = LoadEmbeddedText("Assets/THIRD-PARTY-NOTICES.md");
            _licenseContentLoaded = true;
        }

        HomePanel.Visibility = Visibility.Collapsed;
        AlignPanel.Visibility = Visibility.Collapsed;
        CompositePanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        LicensePanel.Visibility = Visibility.Visible;
        TitleBarMinimizeButton.Visibility = Visibility.Collapsed;
        TitleBarMaximizeButton.Visibility = Visibility.Visible;
        HideHomeSettings();
        Width = 440;
        Height = 640;
        PinToRightEdge();
    });

    /// <summary>Called from App.xaml.cs once a background CheckForUpdatesAsync
    /// finds something newer than the running build. This button IS the
    /// notification (no separate badge elsewhere) -- nothing downloads
    /// until the user clicks it, opens バージョン情報, and picks a version
    /// themselves (see UpdateApplyButton_Click).</summary>
    public void ShowUpdateAvailableNotification() => TitleBarUpdateButton.Visibility = Visibility.Visible;

    /// <summary>Real WindowState.Maximized (not a hand-rolled fill-the-
    /// workarea substitute) specifically so two pieces of native behavior
    /// come for free: dragging the title bar of a maximized window
    /// restores it and keeps following the cursor (built into DragMove(),
    /// which TitleBar_MouseLeftButtonDown already calls), and edge-drag
    /// resizing (ResizeMode="CanResize"). RestoreBounds tracks the pre-
    /// maximize size/position automatically -- no manual bookkeeping
    /// needed here, unlike an earlier version of this that faked
    /// maximizing via manual Width/Height/Left/Top.</summary>
    private void TitleBarMaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Keeps the maximize button's icon in sync regardless of HOW
    /// WindowState changed -- the button above, double-clicking the title
    /// bar (not currently wired, but this covers it if that's ever added),
    /// Aero Snap, or the taskbar's own right-click menu.</summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- WindowStyle="None" + WindowState="Maximized" has a well-known
    //      WPF bug: without this hook, a maximized borderless window
    //      expands to the monitor's FULL bounds (covering the taskbar)
    //      instead of just its work area. Intercepting WM_GETMINMAXINFO and
    //      filling in the actual work-area bounds ourselves is the standard
    //      fix -- see OnSourceInitialized, which installs this hook once
    //      the window's Win32 handle exists (too early in the constructor). ----

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public MonitorRECT rcMonitor;
        public MonitorRECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            hwndSource.AddHook(WindowProc);
        }
    }

    // WM_NCCALCSIZE: ResizeMode="CanResize" reserves a thin non-client
    // border even with WindowStyle="None" -- DWM paints its own default
    // (white) background into that sliver since none of our WPF content
    // renders there, which is what showed up as a hairline at the top
    // edge. Treating the whole window as client area (no border reserved)
    // removes it, but ALSO removes the OS's own edge-hit-testing that
    // "no border" area would otherwise still provide for drag-to-resize --
    // see WM_NCHITTEST below, which is what actually restores that.
    private const int WM_NCCALCSIZE = 0x0083;

    // WM_NCACTIVATE: DWM's default handling repaints a non-client border
    // on activate/deactivate (e.g. switching back from another window) even
    // though WM_NCCALCSIZE above already claims there's no non-client area
    // -- that's the white flash reported when refocusing this window.
    // Returning TRUE and marking the message handled (instead of letting
    // DefWindowProc run) skips that default repaint entirely.
    private const int WM_NCACTIVATE = 0x0086;

    // WM_NCHITTEST: with WM_NCCALCSIZE above claiming zero non-client area,
    // Windows has nothing left to classify as a resize edge, so dragging
    // near the window's border stopped resizing it. Classifying the outer
    // few pixels as HTLEFT/HTRIGHT/HTTOP/HTBOTTOM/corners ourselves (pure
    // hit-testing, no visible border reserved) hands that back to the OS's
    // own native resize-drag loop, exactly like a normal window's border.
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeGripThickness = 6;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out MonitorRECT lpRect);

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            long l = lParam.ToInt64();
            int x = unchecked((short)(l & 0xFFFF));
            int y = unchecked((short)((l >> 16) & 0xFFFF));
            GetWindowRect(hwnd, out var rect);

            bool onLeft = x < rect.Left + ResizeGripThickness;
            bool onRight = x >= rect.Right - ResizeGripThickness;
            bool onTop = y < rect.Top + ResizeGripThickness;
            bool onBottom = y >= rect.Bottom - ResizeGripThickness;

            int hit = (onTop, onBottom, onLeft, onRight) switch
            {
                (true, _, true, _) => HTTOPLEFT,
                (true, _, _, true) => HTTOPRIGHT,
                (_, true, true, _) => HTBOTTOMLEFT,
                (_, true, _, true) => HTBOTTOMRIGHT,
                (true, _, _, _) => HTTOP,
                (_, true, _, _) => HTBOTTOM,
                (_, _, true, _) => HTLEFT,
                (_, _, _, true) => HTRIGHT,
                _ => HTCLIENT,
            };
            if (hit != HTCLIENT)
            {
                handled = true;
                return new IntPtr(hit);
            }
            // Not on an edge -- fall through to default handling (HTCLIENT),
            // which leaves the title bar's own DragMove()-based dragging and
            // every button's own click handling untouched.
            return IntPtr.Zero;
        }
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_NCACTIVATE)
        {
            handled = true;
            return new IntPtr(1);
        }
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref monitorInfo);
                var work = monitorInfo.rcWork;
                var bounds = monitorInfo.rcMonitor;
                mmi.ptMaxPosition.X = Math.Abs(work.Left - bounds.Left);
                mmi.ptMaxPosition.Y = Math.Abs(work.Top - bounds.Top);
                mmi.ptMaxSize.X = Math.Abs(work.Right - work.Left);
                mmi.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Populates UpdateVersionCombo from every version currently
    /// published to the repo's release feed (newest first, newest
    /// preselected) and sets UpdateStatusText accordingly. Runs every time
    /// AboutPanel opens (not cached like the license text) so it reflects
    /// whatever's actually published right now, not a snapshot from
    /// whenever the panel was last opened.</summary>
    private async Task RefreshUpdateSectionAsync()
    {
        if (!UpdateService.IsInstalled)
        {
            UpdateStatusText.Text = "この起動方法(開発ビルドなど)ではアップデート機能を利用できません。";
            UpdateVersionRow.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateStatusText.Text = "バージョン情報を確認しています…";
        UpdateVersionRow.Visibility = Visibility.Collapsed;

        var versions = await UpdateService.GetAvailableVersionsAsync();
        if (versions.Count == 0)
        {
            UpdateStatusText.Text = "バージョン情報を取得できませんでした。ネットワーク接続を確認してください。";
            return;
        }

        var current = UpdateService.CurrentVersion;
        UpdateStatusText.Text = current is not null && versions[0].Version > current
            ? "新しいバージョンがあります。"
            : "現在のバージョンは最新です。";

        UpdateVersionCombo.Items.Clear();
        foreach (var asset in versions)
        {
            var label = $"v{asset.Version}" + (asset.Version == current ? "(現在)" : "");
            UpdateVersionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = asset });
        }
        UpdateVersionCombo.SelectedIndex = 0; // newest first, preselected as the default
        UpdateVersionRow.Visibility = Visibility.Visible;
    }

    private async void UpdateApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateVersionCombo.SelectedItem is not ComboBoxItem { Tag: Velopack.VelopackAsset asset }) return;

        UpdateApplyButton.IsEnabled = false;
        UpdateVersionCombo.IsEnabled = false;
        UpdateStatusText.Text = "ダウンロード中…";
        try
        {
            await UpdateService.DownloadAndApplyAsync(asset, percent =>
                Dispatcher.Invoke(() => UpdateStatusText.Text = $"ダウンロード中… {percent}%"));
            // ApplyUpdatesAndRestart (inside DownloadAndApplyAsync) restarts
            // the process itself -- normal execution doesn't continue past
            // this point on success.
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"アップデートに失敗しました: {ex.Message}";
            UpdateApplyButton.IsEnabled = true;
            UpdateVersionCombo.IsEnabled = true;
        }
    }

    private static string LoadEmbeddedText(string packRelativePath)
    {
        var uri = new Uri($"pack://application:,,,/{packRelativePath}", UriKind.Absolute);
        var info = Application.GetResourceStream(uri);
        if (info is null) return "";
        using var reader = new StreamReader(info.Stream);
        return reader.ReadToEnd();
    }

    // ---- Custom title bar: WindowStyle="None" removes the native one (it
    //      was just wasteful chrome), so dragging and closing have to be
    //      hand-rolled. Checks whether the press originated on a Button (the
    //      close button) before starting a drag -- an earlier version instead
    //      had the close button mark PreviewMouseLeftButtonDown handled on
    //      itself to stop the drag, but that also suppressed the button's own
    //      internal click-on-release logic (ButtonBase's class handler for the
    //      paired bubbling MouseLeftButtonDown never saw the event, since it
    //      was already Handled by the time it got there), so the button never
    //      registered a press and Click never fired. Checking the origin here
    //      instead leaves the button's own event handling untouched. ----

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && HasAncestorOrSelf<Button>(source)) return;
        DragMove();
    }

    private static bool HasAncestorOrSelf<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T) return true;
        }
        return false;
    }

    // ---- Shared Slider template (see Window.Resources): pressing down
    //      anywhere on PART_Track and dragging without releasing should keep
    //      following the mouse the whole time, not just jump once on the
    //      initial click (IsMoveToPointEnabled's part) and then ignore
    //      further movement. Track.ValueFromPoint does the actual
    //      point-to-value math (accounting for the Thumb's own width the
    //      same way the Track already does internally), so this only needs
    //      to capture the mouse and keep feeding it the current point. ----

    private bool _sliderTrackDragging;
    private Track? _sliderTrackDraggingTrack;
    private Point _sliderTrackPendingPoint;
    private bool _sliderTrackPendingUpdate;

    private void SliderTrack_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Track track) return;
        // A click that lands on the Thumb itself is left alone -- it
        // already drags correctly via its own built-in mechanism.
        if (e.OriginalSource is DependencyObject source && HasAncestorOrSelf<Thumb>(source)) return;

        _sliderTrackDragging = true;
        _sliderTrackDraggingTrack = track;
        track.CaptureMouse();
        track.Value = track.ValueFromPoint(e.GetPosition(track));
        CompositionTarget.Rendering += SliderTrackDragging_Rendering;
        // Without this, the still-unhandled event keeps tunneling/bubbling
        // into the DecreaseRepeatButton/IncreaseRepeatButton underneath
        // (styled transparent, but still functionally real RepeatButtons),
        // which then ALSO fires its own built-in Click-driven LargeChange
        // step on top of the value this just set -- the two fighting over
        // the Value each press/move is what made track-dragging feel
        // heavier/laggier than grabbing the Thumb directly.
        e.Handled = true;
    }

    /// <summary>Only records the latest mouse position here -- the actual
    /// Value assignment (and everything it cascades into: RefreshFromState
    /// touching ~20 controls, ScheduleCompositeRender, etc.) happens at most
    /// once per rendered frame, via CompositionTarget.Rendering below, not
    /// once per raw WM_MOUSEMOVE. A high-poll-rate mouse can deliver far
    /// more of those than the UI can (or needs to) redraw for; the built-in
    /// Thumb drag is implicitly bound to the same render cadence, which is
    /// why grabbing the Thumb directly didn't feel this heavy.</summary>
    private void SliderTrack_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_sliderTrackDragging) return;
        if (sender is not Track track) return;
        _sliderTrackPendingPoint = e.GetPosition(track);
        _sliderTrackPendingUpdate = true;
        e.Handled = true;
    }

    private void SliderTrackDragging_Rendering(object? sender, EventArgs e)
    {
        if (!_sliderTrackPendingUpdate || _sliderTrackDraggingTrack is null) return;
        _sliderTrackPendingUpdate = false;
        _sliderTrackDraggingTrack.Value = _sliderTrackDraggingTrack.ValueFromPoint(_sliderTrackPendingPoint);
    }

    private void SliderTrack_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sliderTrackDragging) return;
        _sliderTrackDragging = false;
        CompositionTarget.Rendering -= SliderTrackDragging_Rendering;
        _sliderTrackDraggingTrack = null;
        _sliderTrackPendingUpdate = false;
        if (sender is Track track) track.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ThemeService.Apply(ThemeToggleButton.IsChecked == true);
    }

    // ---- Compact mode: shrinks the whole window down to a small widget
    //      pinned to the top-right corner, so it doesn't cover VRChat while
    //      the user is positioning things live. Remembers which mode and
    //      where the window was so "元に戻す" can put it back exactly. ----

    private enum PanelMode { Align, Composite }

    private PanelMode _preCompactMode;
    private double _preCompactLeft, _preCompactTop;
    private readonly double _defaultMinWidth;
    private readonly double _defaultMinHeight;

    private void EnterCompact(PanelMode mode) => WithRedrawSuspended(() =>
    {
        // A tiny corner widget should never be shown maximized.
        WindowState = WindowState.Normal;
        _preCompactMode = mode;
        _preCompactLeft = Left;
        _preCompactTop = Top;

        HomePanel.Visibility = Visibility.Collapsed;
        AlignPanel.Visibility = Visibility.Collapsed;
        CompositePanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        AboutPanel.Visibility = Visibility.Collapsed;
        LicensePanel.Visibility = Visibility.Collapsed;
        TitleBarMinimizeButton.Visibility = Visibility.Collapsed;
        // Compact mode has its own "make it bigger again" mechanism
        // (ExpandButton), so the maximize button would be redundant here.
        TitleBarMaximizeButton.Visibility = Visibility.Collapsed;
        HideHomeSettings();
        CompactModeText.Text = mode == PanelMode.Align ? "位置合わせモード" : "写真合成モード";

        MinWidth = 260;
        // +4 vs this panel's original budget: the title bar grew 28->32px
        // when it was resized to match Windows' standard caption height,
        // which otherwise ate straight into CompactPanel's already-tight
        // content row and squished its button/text.
        MinHeight = 104;
        Width = 300;
        Height = 116;
        PinToRightEdge();
        Top = 20;

        // The alignment overlay only helps while actively positioning the
        // avatar against VRChat's camera UI; once minimized to the compact
        // widget, it just sits on top of VRChat unhelpfully, so hide it
        // until ExpandButton_Click brings it back. SetManuallyHidden (not a
        // plain Hide()) also keeps it hidden if the user reopens VRChat's
        // camera while minimized -- see its own doc comment.
        _overlayWindow.SetManuallyHidden(true);
    });

    /// <summary>Shared by Align and Composite mode now (moved into the
    /// title bar itself -- see TitleBarMinimizeButton's own XAML comment),
    /// so it has to work out which mode is actually open rather than being
    /// told directly like the two per-mode buttons it replaced were.</summary>
    private void TitleBarMinimizeButton_Click(object sender, RoutedEventArgs e) =>
        EnterCompact(CompositePanel.Visibility == Visibility.Visible ? PanelMode.Composite : PanelMode.Align);

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => WithRedrawSuspended(() =>
    {
        MinWidth = _defaultMinWidth;
        MinHeight = _defaultMinHeight;
        Left = _preCompactLeft;
        Top = _preCompactTop;
        _overlayWindow.SetManuallyHidden(false);
        if (_preCompactMode == PanelMode.Align) ShowAlign(); else ShowComposite();
    });

    private void OnVrChatClientResized(IntPtr hwnd, System.Drawing.Rectangle region) => ApplyPositionEstimate(hwnd, region);

    private void OnOscOrientationChanged(bool landscape) => Dispatcher.Invoke(() =>
    {
        if (_overlayWindow.FollowedHwnd is { } hwnd && _overlayWindow.FollowedClientRect is { } region)
        {
            ApplyPositionEstimate(hwnd, region);
        }
        // Not attached yet: nothing to reposition -- the next manual Reset
        // will pick up this orientation anyway.
    });

    private void ControlPanelWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _colorPickTarget != ColorPickTarget.None)
        {
            BeginColorPick(_colorPickTarget);
            e.Handled = true;
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrl) return;

        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PerformUndoOrRedo(isRedo: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Z)
        {
            PerformUndoOrRedo(isRedo: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            PerformUndoOrRedo(isRedo: true);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+Z/Ctrl+Y can jump across a whole batch of look-
    /// adjustment values at once (e.g. undoing a Match button's result --
    /// see MatchAvatarToPhotoButton_Click), which hits the exact same full-
    /// resolution-recomposite cost that motivated FinishMatchRender/
    /// ShowCompositeLoading there: without the same treatment here, Undo/
    /// Redo would apply instantly but then freeze the UI thread for that
    /// recomposite with no loading indicator to explain why. Wraps the
    /// actual jump with _isCompositeDragging (so whatever intermediate
    /// renders it triggers along the way don't get treated as the final
    /// save-quality result -- see RenderCompositePreview's own doc comment)
    /// and reuses FinishMatchRender for the one render that actually
    /// commits after -- only while Composite mode is actually showing,
    /// since that's the only place this cost exists.</summary>
    private void PerformUndoOrRedo(bool isRedo)
    {
        bool showsComposite = CompositePanel.Visibility == Visibility.Visible;
        if (showsComposite) ShowCompositeLoading();

        _isCompositeDragging = true;
        if (isRedo) _undo.Redo(); else _undo.Undo();
        _isCompositeDragging = false;

        if (showsComposite) FinishMatchRender();
    }

    /// <summary>Reacts to an actual Undo/Redo jump (see UndoManager.Applied):
    /// flashes every row whose value the jump actually changed -- avatar
    /// placement/look via the OverlaySnapshot fields, photo look/placement/
    /// finishing effects via the CompositeSnapshot Extra -- covering every
    /// field either snapshot carries, not just the look-adjustment sliders.
    /// Also shows a small fading undo/redo icon unconditionally, since a
    /// jump can legitimately change nothing about (or find no row for) a
    /// couple of fields -- see the Width/Height/CompositePlaceHeight
    /// comments below -- and the icon alone still confirms something
    /// happened.</summary>
    /// <summary>Which row to flash for each OverlaySnapshot field (see
    /// OnUndoRedoApplied) -- a table instead of ~20 near-identical "if
    /// (before.X != after.X) FlashRow(...)" lines, so adding a new undoable
    /// field is one row here instead of a new if-statement to remember.
    /// Built lazily (not a static initializer) since it closes over this
    /// instance's own named XAML elements. A few entries key on more than
    /// one field at once (the tint/light-leak/color groups below) by
    /// returning a value tuple -- Equals on a boxed tuple does the same
    /// field-by-field structural comparison the old "||"-chained conditions
    /// did.</summary>
    private List<(Func<OverlaySnapshot, object?> Key, FrameworkElement Row)>? _overlayFlashTable;

    private List<(Func<OverlaySnapshot, object?> Key, FrameworkElement Row)> BuildOverlayFlashTable() => new()
    {
        // No X/Y/Width/Height/RotationDegrees rows left in Align mode at all
        // (see this panel's own XAML comment on their removal -- OverlayWindow's
        // own drag handles are the only UI for these now) and no other row in
        // this card is a FlashRow-compatible anchor for them (FlashRow needs
        // anchor.Parent to be the Grid row itself, and ImageVisibleToggle's
        // parent is an inner StackPanel, not that Grid), so these five simply
        // aren't flashed on undo/redo any more -- the live overlay window
        // visibly jumping to the restored position is its own feedback.
        (s => s.Opacity, OpacitySlider),
        (s => s.EdgeBlurRadius, CompositeEdgeBlurSlider),
        // Brightness..Blacks only have a slider on the Composite side now
        // (see AlignPanel's own comment on why they were dropped from Align
        // mode), so only that one flashes.
        (s => s.Brightness, CompositeBrightnessSlider),
        (s => s.Contrast, CompositeContrastSlider),
        (s => s.Saturation, CompositeSaturationSlider),
        (s => s.Vibrance, CompositeVibranceSlider),
        (s => s.Temperature, CompositeTemperatureSlider),
        (s => s.Tint, CompositeTintSlider),
        (s => s.Hue, CompositeHueSlider),
        (s => s.Highlights, CompositeHighlightsSlider),
        (s => s.Shadows, CompositeShadowsSlider),
        (s => s.Whites, CompositeWhitesSlider),
        (s => s.Blacks, CompositeBlacksSlider),
        (s => (s.ColorTintStrength, s.ColorTintR, s.ColorTintG, s.ColorTintB), CompositeColorTintStrengthSlider),
    };

    /// <summary>Same idea as <see cref="BuildOverlayFlashTable"/>, for
    /// CompositeSnapshot's fields.</summary>
    private List<(Func<CompositeSnapshot, object?> Key, FrameworkElement Row)>? _compositeFlashTable;

    private List<(Func<CompositeSnapshot, object?> Key, FrameworkElement Row)> BuildCompositeFlashTable() => new()
    {
        (s => s.PhotoBrightness, PhotoBrightnessSlider),
        (s => s.PhotoContrast, PhotoContrastSlider),
        (s => s.PhotoSaturation, PhotoSaturationSlider),
        (s => s.PhotoVibrance, PhotoVibranceSlider),
        (s => s.PhotoTemperature, PhotoTemperatureSlider),
        (s => s.PhotoTint, PhotoTintSlider),
        (s => s.PhotoHue, PhotoHueSlider),
        (s => s.PhotoHighlights, PhotoHighlightsSlider),
        (s => s.PhotoShadows, PhotoShadowsSlider),
        (s => s.PhotoWhites, PhotoWhitesSlider),
        (s => s.PhotoBlacks, PhotoBlacksSlider),
        (s => (s.PhotoColorTintStrength, s.PhotoColorTintR, s.PhotoColorTintG, s.PhotoColorTintB), PhotoColorTintStrengthSlider),
        (s => s.PhotoBlurAmount, PhotoBlurSlider),
        (s => s.GrainAmount, GrainSlider),
        (s => s.VignetteAmount, VignetteSlider),
        (s => s.SoftnessAmount, SoftnessSlider),
        (s => s.SharpnessAmount, SharpnessSlider),
        (s => s.FadeAmount, FadeSlider),
        (s => s.GlowAmount, GlowSlider),
        (s => s.ChromaticAberrationAmount, ChromaticAberrationSlider),
        (s => s.ColorBleedAmount, ColorBleedSlider),
        (s => s.ScanlineAmount, ScanlineSlider),
        (s => s.ClarityAmount, ClaritySlider),
        (s => (s.LightLeakAmount, s.LightLeakAngle, s.LightLeakDistance, s.LightLeakColorB, s.LightLeakColorG, s.LightLeakColorR), LightLeakSlider),
        (s => s.ToneGradientAmount, ToneGradientSlider),
        (s => s.ToneGradientRotation, ToneGradientDirectionSlider),
        (s => s.DropShadowAmount, DropShadowSlider),
        (s => s.DropShadowDirection, DropShadowDirectionSlider),
        (s => s.DropShadowDistance, DropShadowDistanceSlider),
        (s => s.DropShadowBlur, DropShadowBlurSlider),
        (s => (s.DropShadowColorB, s.DropShadowColorG, s.DropShadowColorR), DropShadowColorButton),
        (s => s.DropShadowBlendMode, DropShadowBlendModeCombo),
        // No dedicated crop-width/position row anymore (see 切り抜き幅/位置X/Y's
        // own removal comment elsewhere -- the interactive 切り抜きモード drag
        // replaced them), so those two undo-tracked properties have no
        // FlashRow-compatible anchor left; simply not flashed on undo/redo.
        (s => s.CanvasAspectRatio, CanvasAspectCombo),
        // No dedicated X/Y/幅/回転 rows anymore either (see
        // AvatarPlacementModeToggle_Changed's own removal comment) -- all 5
        // properties instead flash the toggle's own row as one group.
        (s => (s.CompositePlaceX, s.CompositePlaceY, s.CompositePlaceWidth, s.CompositePlaceHeight, s.CompositeRotation), AvatarPlacementModeToggle),
    };

    private void OnUndoRedoApplied(bool isRedo, OverlaySnapshot before, OverlaySnapshot after, object? extraBefore, object? extraAfter)
    {
        _overlayFlashTable ??= BuildOverlayFlashTable();
        foreach (var (key, row) in _overlayFlashTable)
        {
            if (!Equals(key(before), key(after))) FlashRow(row);
        }

        if (extraBefore is CompositeSnapshot pb && extraAfter is CompositeSnapshot pa)
        {
            _compositeFlashTable ??= BuildCompositeFlashTable();
            foreach (var (key, row) in _compositeFlashTable)
            {
                if (!Equals(key(pb), key(pa))) FlashRow(row);
            }
        }

        ShowUndoRedoReaction(isRedo);
    }

    /// <summary>Briefly highlights one row (its enclosing Grid -- found via
    /// <paramref name="anchor"/>'s own Parent, so none of the ~50 existing
    /// rows need an x:Name added just for this) with a fading tint, inserted
    /// behind the row's own content so the label/slider/box still read
    /// clearly on top of it. <paramref name="anchor"/> is typically a
    /// Slider, but a couple of rows (Width/Height) only have a TextBox --
    /// any FrameworkElement sitting directly in the row's Grid works.</summary>
    private void FlashRow(FrameworkElement? anchor)
    {
        if (anchor?.Parent is not Grid row) return;
        var flash = new Border
        {
            Background = (Brush)FindResource("PrimaryTintBrush"),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
        };
        Grid.SetColumnSpan(flash, Math.Max(1, row.ColumnDefinitions.Count));
        row.Children.Insert(0, flash);

        var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(550));
        fade.Completed += (_, _) => row.Children.Remove(flash);
        flash.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Fades UndoRedoReactionBadge in, holds briefly, then fades it
    /// back out -- shown for every Undo/Redo that actually applies a
    /// change, regardless of whether OnUndoRedoApplied found any look-
    /// adjustment row to flash alongside it.</summary>
    private void ShowUndoRedoReaction(bool isRedo)
    {
        UndoReactionIcon.Visibility = isRedo ? Visibility.Collapsed : Visibility.Visible;
        RedoReactionIcon.Visibility = isRedo ? Visibility.Visible : Visibility.Collapsed;
        UndoRedoReactionBadge.Visibility = Visibility.Visible;

        var keyFrames = new DoubleAnimationUsingKeyFrames();
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
        keyFrames.Completed += (_, _) => UndoRedoReactionBadge.Visibility = Visibility.Collapsed;
        UndoRedoReactionBadge.BeginAnimation(OpacityProperty, keyFrames);
    }

    /// <summary>Which OverlayState properties each RefreshFromState section
    /// below actually depends on -- gates that section instead of running
    /// unconditionally on every single _state change (position/size/
    /// rotation/opacity, all 12 look sliders, guide toggles, etc. all used
    /// to get rewritten together on, say, a single Rotation slider tick).
    /// Same pattern already used for OverlayWindow.ApplyState/UpdateGuide's
    /// own gating. <paramref name="changedProperty"/> null means "unknown/
    /// many properties changed" (the initial call, LoadImageFile's full
    /// reload, and OverlayState's own batched-change notification -- see
    /// its BeginBatch doc comment) -- treated as "refresh everything".</summary>
    private static readonly HashSet<string?> PositionRefreshPropertyNames = new()
    {
        nameof(OverlayState.X), nameof(OverlayState.Y), nameof(OverlayState.Width), nameof(OverlayState.Height),
        nameof(OverlayState.RotationDegrees), nameof(OverlayState.Opacity), nameof(OverlayState.IsImageVisible),
    };

    private static readonly HashSet<string?> GuideRefreshPropertyNames = new()
    {
        nameof(OverlayState.GuideVisible),
    };

    private static readonly HashSet<string?> LookRefreshPropertyNames = new()
    {
        nameof(OverlayState.EdgeBlurRadius), nameof(OverlayState.Brightness), nameof(OverlayState.Contrast), nameof(OverlayState.Saturation),
        nameof(OverlayState.Vibrance), nameof(OverlayState.Temperature), nameof(OverlayState.Tint), nameof(OverlayState.Hue),
        nameof(OverlayState.Highlights), nameof(OverlayState.Shadows), nameof(OverlayState.Whites), nameof(OverlayState.Blacks),
        nameof(OverlayState.ColorTintStrength), nameof(OverlayState.ColorTintR), nameof(OverlayState.ColorTintG), nameof(OverlayState.ColorTintB),
    };

    private void RefreshFromState(string? changedProperty = null)
    {
        _suppressEvents = true;
        try
        {
            bool all = changedProperty is null;

            if (all || PositionRefreshPropertyNames.Contains(changedProperty))
            {
                // No X/Y/幅/高さ/回転(度) fields left to sync here at all (see
                // this panel's own XAML comment on their removal) -- only
                // 不透明度/表示 still have Align-mode UI of their own.
                double opacityPercent = _state.Opacity * 100;
                OpacityBox.Text = opacityPercent.ToString("F0", CultureInfo.InvariantCulture);
                OpacitySlider.Value = opacityPercent;
                ImageVisibleToggle.IsChecked = _state.IsImageVisible;
            }

            if (all || GuideRefreshPropertyNames.Contains(changedProperty))
            {
                GuideVisibleToggle.IsChecked = _state.GuideVisible;
                RefreshGuideManualDisplay();
            }

            if (all || changedProperty == nameof(OverlayState.ImagePath))
            {
                string imageFileName = string.IsNullOrEmpty(_state.ImagePath) ? "(画像未読み込み)" : Path.GetFileName(_state.ImagePath);
                ImagePathText.Text = imageFileName;
                CompositeImagePathText.Text = imageFileName;
            }

            if (all || LookRefreshPropertyNames.Contains(changedProperty))
            {
                // Composite panel's mirrored PNG controls (see the "PNG look"
                // handlers below) -- same _state, kept in sync from here too.
                // The look sliders (Brightness..Blacks), and now 境界ぼかし too,
                // only exist on the Composite side (see AlignPanel's own comment
                // on why they were dropped from Align mode), so these read
                // straight from _state instead of mirroring an Align-mode
                // Box.Text that no longer exists.
                CompositeEdgeBlurBox.Text = _state.EdgeBlurRadius.ToString("F0", CultureInfo.InvariantCulture);
                CompositeEdgeBlurSlider.Value = _state.EdgeBlurRadius;
                CompositeBrightnessBox.Text = _state.Brightness.ToString("F0", CultureInfo.InvariantCulture);
                CompositeBrightnessSlider.Value = _state.Brightness;
                CompositeContrastBox.Text = _state.Contrast.ToString("F0", CultureInfo.InvariantCulture);
                CompositeContrastSlider.Value = _state.Contrast;
                CompositeSaturationBox.Text = _state.Saturation.ToString("F0", CultureInfo.InvariantCulture);
                CompositeSaturationSlider.Value = _state.Saturation;
                CompositeVibranceBox.Text = _state.Vibrance.ToString("F0", CultureInfo.InvariantCulture);
                CompositeVibranceSlider.Value = _state.Vibrance;
                CompositeTemperatureBox.Text = _state.Temperature.ToString("F0", CultureInfo.InvariantCulture);
                CompositeTemperatureSlider.Value = _state.Temperature;
                CompositeTintBox.Text = _state.Tint.ToString("F0", CultureInfo.InvariantCulture);
                CompositeTintSlider.Value = _state.Tint;
                CompositeHueBox.Text = _state.Hue.ToString("F0", CultureInfo.InvariantCulture);
                CompositeHueSlider.Value = _state.Hue;
                CompositeHighlightsBox.Text = _state.Highlights.ToString("F0", CultureInfo.InvariantCulture);
                CompositeHighlightsSlider.Value = _state.Highlights;
                CompositeShadowsBox.Text = _state.Shadows.ToString("F0", CultureInfo.InvariantCulture);
                CompositeShadowsSlider.Value = _state.Shadows;
                CompositeWhitesBox.Text = _state.Whites.ToString("F0", CultureInfo.InvariantCulture);
                CompositeWhitesSlider.Value = _state.Whites;
                CompositeBlacksBox.Text = _state.Blacks.ToString("F0", CultureInfo.InvariantCulture);
                CompositeBlacksSlider.Value = _state.Blacks;
                CompositeColorTintStrengthBox.Text = _state.ColorTintStrength.ToString("F0", CultureInfo.InvariantCulture);
                CompositeColorTintStrengthSlider.Value = _state.ColorTintStrength;
                CompositeColorTintSwatch.Background = new SolidColorBrush(Color.FromRgb(_state.ColorTintR, _state.ColorTintG, _state.ColorTintB));
                CompositeColorTintHexBox.Text = ToHexColor(_state.ColorTintR, _state.ColorTintG, _state.ColorTintB);
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void LoadImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            Title = "アバター画像(透過PNG)を選択",
        };
        if (dialog.ShowDialog() == true)
        {
            LoadImageFile(dialog.FileName);
        }
    }

    private void LoadImageFile(string path)
    {
        _undo.BeginChange();
        _overlayWindow.LoadImage(path);
        PerformReset();
        _undo.CommitChange();
        RefreshFromState();

        // A different avatar image likely has a different aspect ratio/size
        // than whatever the composite placement was fitted to, so it needs a
        // fresh auto-placement guess too -- same as picking a new photo does
        // (see TryLoadPhotoPixels' own reset of this flag).
        _compositePlacementInitialized = false;
        // Explicitly (re-)loading an avatar is a clear signal the user wants
        // it back in the composite, overriding any earlier "アバターなしで
        // 進める" choice.
        _compositeSkipAvatar = false;
        RefreshSkipAvatarUI();
        ScheduleCompositeRender();
        AddRecentAvatarPath(path);
    }

    // ---- Recent avatars / recent photos: moved to
    //      ControlPanelWindow.RecentFiles.cs (a self-contained concern,
    //      split out per the god-object cleanup -- see that file's own
    //      header comment). ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPngPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (GetDroppedPngPath(e) is { } path)
        {
            LoadImageFile(path);
        }
    }

    private static string? GetDroppedPngPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return Array.Find(files, f => string.Equals(Path.GetExtension(f), ".png", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Manual Reset: establishes the Z-order attachment + move-follow
    /// (only needed here -- the auto-triggers below are already attached by
    /// the time they fire) and then applies the position estimate. Also
    /// invoked automatically right after loading a new image (see
    /// <see cref="LoadImageFile"/>) and once at app startup if VRChat is
    /// already running (see App.OnStartup) -- a freshly-picked image, or a
    /// freshly-launched app, should both start from a known-good,
    /// camera-width-fitted position instead of wherever the box happened to
    /// be (a size/position left over from a previous session, centered but
    /// otherwise untouched, was the actual bug behind "position isn't fitted
    /// to the camera width right after opening the app").</summary>
    private void ResetButton_Click(object sender, RoutedEventArgs e) => PerformReset();

    public void PerformReset()
    {
        var hwnd = VRChatWindowService.FindVRChatWindow();
        if (hwnd is null)
        {
            ResetStatusText.Text = "VRChatウィンドウが見つかりませんでした。";
            return;
        }
        _overlayWindow.AttachToOwner(hwnd.Value);
        AttachToOwner(hwnd.Value);

        var clientRect = VRChatWindowService.GetClientRectOnScreen(hwnd.Value);
        if (clientRect is not { Width: > 0, Height: > 0 } region)
        {
            ResetStatusText.Text = "VRChatにアタッチしました（ウィンドウ位置の取得に失敗、位置は変更していません）。";
            return;
        }
        ApplyPositionEstimate(hwnd.Value, region);
    }

    /// <summary>Positions the overlay at the estimated camera-frame rect for
    /// the current orientation (see the formula above), fitting the loaded
    /// image's own aspect ratio into that frame. Falls back to just
    /// re-centering at the current size if VRChat hasn't reported an
    /// orientation yet. Takes an already-known hwnd/rect -- callers that
    /// already have current values (the resize/orientation auto-triggers)
    /// should never redo a FindVRChatWindow() scan or an AttachToOwner just to
    /// call this. Wrapped in undo for every caller, manual or automatic --
    /// UndoManager's nested Begin/Commit handles any overlap with a
    /// concurrently in-progress unrelated edit correctly.</summary>
    private void ApplyPositionEstimate(IntPtr hwnd, System.Drawing.Rectangle region)
    {
        _undo.BeginChange();
        // X/Y/Width/Height below are one logical move, not four separate
        // ones -- batching collapses them into a single OverlayState
        // notification instead of four (see OverlayState.BeginBatch), which
        // matters here since this runs on every VRChat window resize/move
        // tick, not just the manual "位置をリセット" button.
        _state.BeginBatch();

        // Unknown orientation still gets the same frame-rect formula as a
        // known one, just assuming landscape (the more common case) rather
        // than falling back to a generic re-center -- a landscape-shaped
        // guess is more likely to already be close than a plain center.
        bool? knownOrientation = _oscListener.IsLandscape;
        bool landscape = knownOrientation ?? true;
        var (frameLeft, frameTop, frameWidth, frameHeight) = VRChatWindowService.ComputeCameraFrameRect(region, landscape);

        var nativeSize = _overlayWindow.ImageNativeSize;
        if (nativeSize is { Width: > 0, Height: > 0 } size)
        {
            // Fit (not stretch) the image into the frame, preserving its own
            // aspect ratio, centered within the frame.
            double scale = Math.Min(frameWidth / size.Width, frameHeight / size.Height);
            double fitWidth = size.Width * scale;
            double fitHeight = size.Height * scale;
            _state.Width = fitWidth;
            _state.Height = fitHeight;
            _state.X = frameLeft + (frameWidth - fitWidth) / 2;
            _state.Y = frameTop + (frameHeight - fitHeight) / 2;
        }
        else
        {
            _state.X = frameLeft;
            _state.Y = frameTop;
            _state.Width = frameWidth;
            _state.Height = frameHeight;
        }

        // Cleared rather than a "it worked" confirmation message -- the
        // overlay visibly moving into place already shows that it happened,
        // and this also clears out any earlier error text (VRChatウィンドウが
        // 見つかりませんでした, etc.) that a later successful reset shouldn't
        // leave stuck on screen.
        ResetStatusText.Text = "";

        _state.EndBatch();
        _undo.CommitChange();
    }

    /// <summary>Focus-based undo grouping for text boxes: everything changed
    /// between focus-in and focus-out becomes one undo step, same principle as
    /// the drag-gesture grouping on the overlay's own mouse handlers.</summary>
    private void Field_GotFocus(object sender, RoutedEventArgs e) => _undo.BeginChange();

    private void Field_LostFocus(object sender, RoutedEventArgs e) => _undo.CommitChange();

    /// <summary>Mouse-based undo grouping for sliders specifically: a WPF
    /// Slider keeps keyboard focus after you release the drag (LostFocus only
    /// fires once you click something else), so focus-based grouping alone left
    /// the edit "open" -- Ctrl+Z right after letting go of a slider wouldn't
    /// commit it yet. Tying Begin/Commit to the actual mouse down/up instead
    /// finalizes the moment the drag gesture itself ends. UndoManager's nested
    /// Begin/Commit means this can safely coexist with Field_GotFocus/LostFocus
    /// also firing for the same interaction.</summary>
    private void Field_MouseDown(object sender, MouseButtonEventArgs e) => _undo.BeginChange();

    private void Field_MouseUp(object sender, MouseButtonEventArgs e) => _undo.CommitChange();

    // ---- Brightness/contrast/saturation/vibrance/temperature/tint/hue
    //      (both the avatar-image copies and the photo-look copies) report
    //      their own drag start/end, so intermediate renders during the
    //      drag don't get committed as the save-quality result until it
    //      ends -- see OverlayWindow.SetColorDragging and
    //      RenderCompositePreview's _isCompositeDragging check. Edge blur
    //      itself uses the separate handlers below instead. ----

    private void PngColorSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        Field_MouseDown(sender, e);
        _isCompositeDragging = true;
        _overlayWindow.SetColorDragging(true);
    }

    private void PngColorSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        Field_MouseUp(sender, e);
        _isCompositeDragging = false;
        _overlayWindow.SetColorDragging(false);
        ScheduleCompositeRender();
    }

    // ---- Edge blur: now live-previews during the drag like every other
    //      slider (see OverlayWindow.SetColorDragging) -- it runs on the GPU
    //      via GpuAvatarEdgeBlur these days, cheap enough not to need the
    //      old freeze-until-release treatment. ----

    private void EdgeBlurSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        Field_MouseDown(sender, e);
        _isCompositeDragging = true;
        _overlayWindow.SetColorDragging(true);
    }

    private void EdgeBlurSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        Field_MouseUp(sender, e);
        _isCompositeDragging = false;
        _overlayWindow.SetColorDragging(false);
        ScheduleCompositeRender();
    }

    private void PhotoColorSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        Field_MouseDown(sender, e);
        _isCompositeDragging = true;
    }

    private void PhotoColorSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        Field_MouseUp(sender, e);
        _isCompositeDragging = false;
        ScheduleCompositeRender();
    }

    private void ResetLookButton_Click(object sender, RoutedEventArgs e)
    {
        _undo.BeginChange();
        _state.BeginBatch();
        _state.EdgeBlurRadius = 5; // the default, not 0 -- an unblurred edge isn't the neutral baseline here
        _state.Brightness = 0;
        _state.Contrast = 0;
        _state.Saturation = 0;
        _state.Vibrance = 0;
        _state.Temperature = 0;
        _state.Tint = 0;
        _state.Hue = 0;
        _state.Highlights = 0;
        _state.Shadows = 0;
        _state.Whites = 0;
        _state.Blacks = 0;
        _state.ColorTintStrength = 0;
        _state.ColorTintR = 255;
        _state.ColorTintG = 255;
        _state.ColorTintB = 255;
        _state.EndBatch();
        _undo.CommitChange();
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void OpacityBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (TryParse(OpacityBox.Text, out var percent)) _state.Opacity = percent / 100.0;
    }

    /// <summary>Pulls a dragged slider value onto the nearest target when
    /// within tolerance -- a soft/magnetic snap, not a hard step: the thumb
    /// still moves freely everywhere else, it just settles exactly on a
    /// meaningful value (center, a 90-degree turn, half opacity) when dragged
    /// close to one.</summary>
    private static double SoftSnap(double value, double tolerance, params double[] targets)
    {
        foreach (var target in targets)
        {
            if (Math.Abs(value - target) <= tolerance) return target;
        }
        return value;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(OpacitySlider.Value, 3, 50, 100);
        if (snapped != OpacitySlider.Value)
        {
            _suppressEvents = true;
            OpacitySlider.Value = snapped;
            _suppressEvents = false;
        }
        _state.Opacity = snapped / 100.0;
    }

    // ---- PNG look (edge blur/brightness/contrast/saturation): shared _state,
    //      editable from BOTH the Align panel's controls and the Composite
    //      panel's mirrored controls -- sender-based (not a hardcoded control
    //      name) so the same handler serves both copies. RefreshFromState
    //      (triggered by _state.PropertyChanged right after the assignment
    //      below) re-syncs every box/slider on both sides, so there's no need
    //      to separately write back to the sender's sibling control here. ----

    private void EdgeBlurBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (TryParse(box.Text, out var v) && v >= 0) _state.EdgeBlurRadius = v;
    }

    private void EdgeBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        _state.EdgeBlurRadius = Math.Round(slider.Value);
    }

    private void ImageVisibleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _state.IsImageVisible = ImageVisibleToggle.IsChecked == true;
    }

    /// <summary>Opens Explorer with UnityCameraGuideService's export file
    /// pre-selected, same "/select," trick ScreenshotToastWindow's own
    /// folder button uses -- lets the file be inspected directly (does it
    /// exist yet, when was it last written) when the 接続状況 badge alone
    /// isn't enough to tell what's going on.</summary>
    private void OpenGuideFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{UnityCameraGuideService.FilePath}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Explorer failed to launch; nothing more to do.
        }
    }

    /// <summary>取得ボタン(RequestGuideButton_Click)を押すたびにUnityへ
    /// リクエストを送るだけの単発フェッチなので、もう「今まさに繋がって
    /// いるか」を表す継続的な状態はない -- このAvaSnap起動中に一度でも
    /// 取得できたかどうかの2択で十分(時刻までは表示しない)。</summary>
    private void UpdateUnityConnectionStatus(bool hasFetched)
    {
        var (text, background, foreground) = hasFetched
            ? ("Unity: 取得済み", "PrimaryTintBrush", "PrimaryBrush")
            : ("Unity: 未取得", "HairlineBrush", "TextSecondaryBrush");
        UnityConnectionText.Text = text;
        UnityConnectionBadge.Background = (Brush)FindResource(background);
        UnityConnectionText.Foreground = (Brush)FindResource(foreground);
    }

    /// <summary>「取得」ボタン: UnityのCameraCompositionGuideExporterへ
    /// スナップショットをリクエストする(設定不要、Unityを開いてさえいれば
    /// バックグラウンドで自動応答)。送りっぱなし(応答を待たない) --
    /// Unity Editorが起動していなければ何も起きず、UnityConnectionText
    /// はそのまま(未取得のまま)。</summary>
    private void RequestGuideButton_Click(object sender, RoutedEventArgs e) => _unityCameraGuide.RequestUpdate();

    private void GuideVisibleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _state.GuideVisible = GuideVisibleToggle.IsChecked == true;
    }

    /// <summary>No _suppressEvents management of its own -- every caller is
    /// already inside a _suppressEvents=true scope of its own
    /// (RefreshFromState's, or the constructor's DataUpdated handler), and
    /// this used to manage the flag itself too, which broke when called
    /// from INSIDE RefreshFromState's own scope: setting _suppressEvents=
    /// false partway through RefreshFromState let its later lines' handlers
    /// fire early.</summary>
    private void SetGuideFovPitchRollDisplay(double fov, double pitch, double roll)
    {
        GuideFovBox.Text = fov.ToString("F0", CultureInfo.InvariantCulture);
        GuideFovSlider.Value = fov;
        GuidePitchBox.Text = pitch.ToString("F0", CultureInfo.InvariantCulture);
        GuidePitchSlider.Value = pitch;
        GuideRollBox.Text = roll.ToString("F0", CultureInfo.InvariantCulture);
        GuideRollSlider.Value = roll;
    }

    private void RefreshGuideManualDisplay() =>
        SetGuideFovPitchRollDisplay(_state.GuideManualFov, _state.GuideManualPitch, _state.GuideManualRoll);

    private void GuideFovBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GuideFovBox.Text, out var v) || v < 20 || v > 150) return;
        _state.GuideManualFov = v;
    }

    private void GuideFovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _state.GuideManualFov = Math.Round(GuideFovSlider.Value);
    }

    private void GuidePitchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GuidePitchBox.Text, out var v)) return;
        _state.GuideManualPitch = Math.Clamp(v, -89, 89);
    }

    private void GuidePitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _state.GuideManualPitch = Math.Round(GuidePitchSlider.Value);
    }

    private void GuideRollBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GuideRollBox.Text, out var v)) return;
        _state.GuideManualRoll = v;
    }

    private void GuideRollSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _state.GuideManualRoll = Math.Round(GuideRollSlider.Value);
    }

    private void BrightnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Brightness;
        _state.Brightness = v;
        ShiftPhotoIfLinked(ref _photoBrightness, delta, -100, 100);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Brightness;
        _state.Brightness = rounded;
        ShiftPhotoIfLinked(ref _photoBrightness, delta, -100, 100);
    }

    private void ContrastBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Contrast;
        _state.Contrast = v;
        ShiftPhotoIfLinked(ref _photoContrast, delta, -100, 100);
    }

    private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Contrast;
        _state.Contrast = rounded;
        ShiftPhotoIfLinked(ref _photoContrast, delta, -100, 100);
    }

    private void SaturationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Saturation;
        _state.Saturation = v;
        ShiftPhotoIfLinked(ref _photoSaturation, delta, -100, 100);
    }

    private void SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Saturation;
        _state.Saturation = rounded;
        ShiftPhotoIfLinked(ref _photoSaturation, delta, -100, 100);
    }

    private void VibranceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Vibrance;
        _state.Vibrance = v;
        ShiftPhotoIfLinked(ref _photoVibrance, delta, -100, 100);
    }

    private void VibranceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Vibrance;
        _state.Vibrance = rounded;
        ShiftPhotoIfLinked(ref _photoVibrance, delta, -100, 100);
    }

    private void TemperatureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Temperature;
        _state.Temperature = v;
        ShiftPhotoIfLinked(ref _photoTemperature, delta, -100, 100);
    }

    private void TemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Temperature;
        _state.Temperature = rounded;
        ShiftPhotoIfLinked(ref _photoTemperature, delta, -100, 100);
    }

    private void TintBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Tint;
        _state.Tint = v;
        ShiftPhotoIfLinked(ref _photoTint, delta, -100, 100);
    }

    private void TintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Tint;
        _state.Tint = rounded;
        ShiftPhotoIfLinked(ref _photoTint, delta, -100, 100);
    }

    private void HueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Hue;
        _state.Hue = v;
        ShiftPhotoIfLinked(ref _photoHue, delta, -180, 180);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Hue;
        _state.Hue = rounded;
        ShiftPhotoIfLinked(ref _photoHue, delta, -180, 180);
    }

    private void HighlightsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Highlights;
        _state.Highlights = v;
        ShiftPhotoIfLinked(ref _photoHighlights, delta, -100, 100);
    }

    private void HighlightsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Highlights;
        _state.Highlights = rounded;
        ShiftPhotoIfLinked(ref _photoHighlights, delta, -100, 100);
    }

    private void ShadowsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Shadows;
        _state.Shadows = v;
        ShiftPhotoIfLinked(ref _photoShadows, delta, -100, 100);
    }

    private void ShadowsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Shadows;
        _state.Shadows = rounded;
        ShiftPhotoIfLinked(ref _photoShadows, delta, -100, 100);
    }

    private void WhitesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Whites;
        _state.Whites = v;
        ShiftPhotoIfLinked(ref _photoWhites, delta, -100, 100);
    }

    private void WhitesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Whites;
        _state.Whites = rounded;
        ShiftPhotoIfLinked(ref _photoWhites, delta, -100, 100);
    }

    private void BlacksBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not TextBox box) return;
        if (!TryParse(box.Text, out var v)) return;
        double delta = v - _state.Blacks;
        _state.Blacks = v;
        ShiftPhotoIfLinked(ref _photoBlacks, delta, -100, 100);
    }

    private void BlacksSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (sender is not Slider slider) return;
        double snapped = SoftSnap(slider.Value, 3, 0);
        if (snapped != slider.Value)
        {
            _suppressEvents = true;
            slider.Value = snapped;
            _suppressEvents = false;
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Blacks;
        _state.Blacks = rounded;
        ShiftPhotoIfLinked(ref _photoBlacks, delta, -100, 100);
    }

    // ---- Composite mode: pick a photo (manually, or via a screenshot-watcher
    //      toast), then composite the aligned PNG onto it. The PNG's own look
    //      (edge blur/brightness/contrast/saturation) is the SAME shared _state
    //      used by Align mode; the photo's own brightness/contrast/saturation
    //      below is independent, composite-mode-only state -- the two can be
    //      adjusted separately or together, as requested. ----

    private ImageAdjustment.PixelBuffer? _photoPixelBuffer;
    private string? _photoPath;
    private double _photoBrightness, _photoContrast, _photoSaturation;
    private double _photoVibrance, _photoTemperature, _photoTint, _photoHue;
    private double _photoHighlights, _photoShadows, _photoWhites, _photoBlacks;

    private double _photoColorTintStrength;
    private byte _photoColorTintR = 255, _photoColorTintG = 255, _photoColorTintB = 255;

    /// <summary>Last meaningfully-picked hue/saturation for each of the two
    /// ティント color wheels (avatar-side and photo-side), cached separately
    /// from the RGB fields the same way DropShadow's own _dropShadowHue/Sat
    /// are -- RGB alone can't represent hue when saturation is 0 (gray/
    /// white), so without this the wheel cursor would snap around while
    /// dragging 明度 through gray.</summary>
    private double _avatarColorTintHue, _avatarColorTintSat;
    private double _photoColorTintHue, _photoColorTintSat;

    /// <summary>Whole-photo blur (0..100, 0 = off; defaults to 0). Unlike
    /// 境界ぼかし (which only feathers the avatar cutout's own edge), this
    /// softens the entire background photo, applied before the avatar is
    /// composited on top so the avatar itself stays sharp. No avatar-side
    /// counterpart, so it's excluded from the 一括調整 link the other 7
    /// photo-look fields share.</summary>
    private double _photoBlurAmount;

    private double _grainAmount, _vignetteAmount;

    /// <summary>0..100, 0 = off. Unlike PhotoBlurAmount (photo only, before
    /// the avatar is composited on top), these two apply to the WHOLE final
    /// composite -- avatar and photo together -- as a finishing pass, same
    /// scope as Grain/Vignette. See ImageAdjustment.ApplySoftness/
    /// ApplySharpness.</summary>
    private double _softnessAmount, _sharpnessAmount;

    /// <summary>0..100, 0 = off. Same "whole composite, finishing pass"
    /// scope as Grain/Vignette/Softness/Sharpness. See
    /// ImageAdjustment.ApplyFade/ApplyGlow.</summary>
    private double _fadeAmount, _glowAmount;

    /// <summary>0..100, 0 = off. Same "whole composite, finishing pass"
    /// scope as the rest -- VHS-style artifacts. See
    /// ImageAdjustment.ApplyChromaticAberration/ApplyColorBleed/
    /// ApplyScanlines.</summary>
    private double _chromaticAberrationAmount, _colorBleedAmount, _scanlineAmount;

    /// <summary>0..100, 0 = off. Same "whole composite, finishing pass"
    /// scope as the rest. See ImageAdjustment.ApplyClarity/ApplyLightLeak.</summary>
    private double _clarityAmount, _lightLeakAmount;

    /// <summary>0..360 degrees, clockwise, 0 = straight down -- same free-
    /// angle convention as _dropShadowDirection/_toneGradientRotation
    /// (see ImageAdjustment.ApplyLightLeak's own doc comment for how this
    /// subsumes the old discrete corner/edge/diagonal position system).</summary>
    private double _lightLeakAngle = 225;

    /// <summary>0..1, how far from center toward LightLeakDial's own edge
    /// the light's anchor sits -- 1 (the default) reproduces the original
    /// always-on-the-border behavior, 0 is dead center. See
    /// ImageAdjustment.ApplyLightLeak's own doc comment.</summary>
    private double _lightLeakDistance = 1.0;

    /// <summary>Defaults to the old "暖色" preset's own RGB, now that color
    /// selection is the same custom wheel+RGB popup as ドロップシャドウ
    /// instead of a fixed warm/cool dropdown.</summary>
    private byte _lightLeakColorB = 60, _lightLeakColorG = 160, _lightLeakColorR = 255;

    /// <summary>Cache of the light leak color popup's own last meaningful
    /// hue/saturation -- same reasoning as _dropShadowHue/_dropShadowSat.</summary>
    private double _lightLeakHue, _lightLeakSat;

    /// <summary>0..100, 0 = off. Same "whole composite, finishing pass"
    /// scope as the rest. See ImageAdjustment.ApplyToneGradient -- unlike
    /// LightLeak's fixed tint, this screens a linear gradient built from the
    /// FULL composite's own weighted bright/dark tones (avatar and
    /// background photo together).</summary>
    private double _toneGradientAmount;

    /// <summary>0..360 degrees, clockwise, 0 = straight down -- same
    /// convention as _dropShadowDirection/_lightLeakAngle. Defaults to 180
    /// (straight up, bright at the top) rather than the convention's own
    /// 0 -- see ImageAdjustment.GpuToneGradient's own doc comment for why
    /// the dot points toward bright, not dark.</summary>
    private double _toneGradientRotation = 180;

    /// <summary>The gradient's two endpoint colors -- white/black by
    /// default (matching GpuToneGradient.TryDetectColors' own no-GPU
    /// fallback), user-editable via 明色/暗色, and refreshed from the
    /// current photo on demand by the 自動判定 button (ToneGradientAutoDetectButton_Click)
    /// rather than recomputed on every render like before.</summary>
    private byte _toneGradientLightR = 255, _toneGradientLightG = 255, _toneGradientLightB = 255;
    private byte _toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB;

    /// <summary>Last meaningfully-picked hue/saturation for each of the two
    /// gradient colors' own wheel popups -- same caching reason as
    /// _dropShadowHue/_dropShadowSat (see SyncColorPickerUI's own comment):
    /// RGB alone can't represent hue at saturation 0.</summary>
    private double _toneGradientLightHue, _toneGradientLightSat;
    private double _toneGradientDarkHue, _toneGradientDarkSat;

    /// <summary>0..100, 0 = off. Duplicates the avatar's own silhouette,
    /// offset/blurred/tinted -- see ImageAdjustment.ApplyDropShadow. Only
    /// has any effect with an avatar loaded (needs its shape to duplicate),
    /// so it's simply skipped in RenderCompositePreview's no-avatar branch.</summary>
    private double _dropShadowAmount;

    /// <summary>0..360 degrees, clockwise, 0 = straight down -- same
    /// convention as _toneGradientRotation.</summary>
    private double _dropShadowDirection;

    /// <summary>Offset distance in full-resolution photo pixels.</summary>
    private double _dropShadowDistance = 100;

    private double _dropShadowBlur = 10;

    /// <summary>Defaults to black, the conventional drop shadow color.</summary>
    private byte _dropShadowColorB, _dropShadowColorG, _dropShadowColorR;

    /// <summary>How the shadow color combines with the photo underneath --
    /// see ImageAdjustment.DropShadowBlendMode's own doc comment. Multiply
    /// by default (the original, only-ever-supported look).</summary>
    private ImageAdjustment.DropShadowBlendMode _dropShadowBlendMode = ImageAdjustment.DropShadowBlendMode.Multiply;

    /// <summary>The full quality "after" composite from the last real render
    /// (frozen during an active drag, same treatment as before) -- what Save
    /// actually writes out, regardless of where CompareSlider is sitting.</summary>
    private WriteableBitmap? _lastComposite;

    /// <summary>The "before" counterpart to _lastComposite: same placement/
    /// rotation, but with none of the look adjustments or finishing effects
    /// applied to either layer (see RenderCompositePreview) -- null whenever
    /// there's no photo+avatar to build one from.</summary>
    private WriteableBitmap? _lastBeforeComposite;

    /// <summary>CompareSlider's value, 0..100. 0 (its default) shows
    /// _lastComposite ("after") across the whole preview, same as if this
    /// feature didn't exist; higher values sweep the before/after split line
    /// further right, revealing more of _lastBeforeComposite from the left
    /// edge inward.</summary>
    private double _beforeAfterSplit;

    private void CompareSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _beforeAfterSplit = CompareSlider.Value;
        // Computed lazily, the first time it's actually needed, rather than
        // on every RenderCompositePreview call regardless of whether
        // CompareSlider is even touched -- see ComputeBeforeComposite.
        if (_beforeAfterSplit > 0 && _lastBeforeComposite is null)
        {
            _lastBeforeComposite = ComputeBeforeComposite();
        }
        UpdateComparisonPreview(_lastComposite, _lastBeforeComposite);
    }

    /// <summary>Merges <paramref name="after"/>/<paramref name="before"/>
    /// per CompareSlider's current position and shows the result -- split
    /// separately from RenderCompositePreview's own before/after rendering so
    /// dragging CompareSlider itself only redoes this cheap merge, not the
    /// whole composite pipeline both of those bitmaps came from.</summary>
    private void UpdateComparisonPreview(WriteableBitmap? after, WriteableBitmap? before)
    {
        if (after is null) return;
        PreviewImage.Source = _beforeAfterSplit > 0 && before is not null
            ? ImageAdjustment.MergeBeforeAfter(before, after, _beforeAfterSplit / 100.0)
            : after;
        UpdateCompareSplitLine();
    }

    /// <summary>Positions CompareSplitLine at the same x the merge itself
    /// split on -- PreviewBorder.Width doubles as "the image's own displayed
    /// width" (see SizePreviewToImage), so the fraction converts directly to
    /// a Margin.Left with no separate scale factor to track. Deliberately a
    /// plain Value/100 fraction, NOT adjusted for where the round 16px Thumb
    /// visually centers (WPF's Track insets it by half its own width at each
    /// end, so the thumb's actual center never quite reaches the track's
    /// edges) -- an earlier version compensated for that, but doing so means
    /// the boundary can never reach the image's true 0%/100% edges either,
    /// which matters more: at Value=100 the image should show fully
    /// "before", not still an ~8px sliver of "after" pinned to the edge. The
    /// thumb sitting a few px short of the line at the extremes (and
    /// matching it everywhere else) is the normal, expected look for this
    /// kind of slider -- most image comparison sliders work the same way.</summary>
    private void UpdateCompareSplitLine()
    {
        if (_beforeAfterSplit <= 0 || double.IsNaN(PreviewBorder.Width))
        {
            CompareSplitLine.Visibility = Visibility.Collapsed;
            return;
        }
        CompareSplitLine.Visibility = Visibility.Visible;
        CompareSplitLine.Margin = new Thickness(
            PreviewBorder.Width * _beforeAfterSplit / 100.0 - CompareSplitLine.Width / 2, 0, 0, 0);
    }

    /// <summary>True while the "一括調整" toggle links the avatar-image look
    /// and the photo look: moving one shifts the OTHER by the same delta
    /// instead of forcing them to match -- so if they started at different
    /// values (e.g. the photo deliberately dialed brighter), that difference
    /// is preserved while still moving together. A mode toggle like
    /// IsClickThrough, not itself an undoable edit and doesn't touch either
    /// side's values on its own -- only actually dragging/typing a linked
    /// slider does (see ShiftPhotoIfLinked and each avatar-look handler's own
    /// delta computation below). Defaults to on (matches LookLinkToggle's own
    /// IsChecked="True" in XAML) -- moving avatar-image and photo look
    /// together is the more commonly wanted behavior, so it's the default
    /// rather than something to discover and turn on.</summary>
    private bool _lookLinked = true;

    private void LookLinkToggle_Changed(object sender, RoutedEventArgs e)
    {
        _lookLinked = LookLinkToggle.IsChecked == true;
        UpdateLinkedRowStyles();
    }

    /// <summary>Highlights the label of each shared parameter (in
    /// BOTH the avatar-image look card and the photo look card) and shows the
    /// connector bar+icon between the two cards while 一括調整 is on, so it's
    /// visually obvious which sliders are the ones currently moving together
    /// -- 境界ぼかし/ぼかし/仕上げ have no counterpart on the other side and
    /// are left alone (each card's own divider marks that boundary, and the
    /// connector bar stops there too). Colors the label text itself rather
    /// than adding new elements around it, so it can't disturb the row
    /// alignment between the two cards (see the 3-column layout comment on
    /// CompositePanel) the way a variable-width badge next to the label
    /// could.</summary>
    private void UpdateLinkedRowStyles()
    {
        if (_lookLinked)
        {
            EnsureLookLinkAdorner();
            PositionLookLinkConnector();
        }
        if (_lookLinkAdorner is not null) _lookLinkAdorner.Visibility = _lookLinked ? Visibility.Visible : Visibility.Collapsed;

        var brush = _lookLinked ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondaryBrush");
        var weight = _lookLinked ? FontWeights.SemiBold : FontWeights.Normal;

        foreach (var label in new[]
        {
            AvatarBrightnessLabel, AvatarContrastLabel, AvatarSaturationLabel, AvatarVibranceLabel,
            AvatarTemperatureLabel, AvatarTintLabel, AvatarHueLabel,
            AvatarHighlightsLabel, AvatarShadowsLabel, AvatarWhitesLabel, AvatarBlacksLabel, AvatarColorTintLabel,
            PhotoBrightnessLabel, PhotoContrastLabel, PhotoSaturationLabel, PhotoVibranceLabel,
            PhotoTemperatureLabel, PhotoTintLabel, PhotoHueLabel,
            PhotoHighlightsLabel, PhotoShadowsLabel, PhotoWhitesLabel, PhotoBlacksLabel, PhotoColorTintLabel,
        })
        {
            label.Foreground = brush;
            label.FontWeight = weight;
        }
    }

    private Border? _lookLinkBar;
    private Border? _lookLinkIcon;
    private Adorner? _lookLinkAdorner;

    /// <summary>Builds (once) the bar+icon visuals and attaches them to
    /// LookLinkConnector's AdornerLayer. An Adorner instead of a Popup: a
    /// Popup fixed the original problem (see below) but, with
    /// AllowsTransparency="True" for the rounded corners, turned out to be an
    /// honest-to-god always-on-top WINDOW -- rendered above every other
    /// window on the desktop, not just above this one. An Adorner renders
    /// above the normal visual tree the SAME way a Popup does, but stays
    /// confined to this window (via the AdornerLayer added by the
    /// AdornerDecorator wrapping the window's whole content -- see
    /// ControlPanelWindow.xaml), avoiding that side effect entirely.
    /// The ORIGINAL problem this (and the Popup before it) fixed: every
    /// "Card" Border has a DropShadowEffect (see the Card style), and a WPF
    /// element with a non-null Effect renders its subtree through a separate
    /// composited layer that does NOT reliably respect Panel.ZIndex or
    /// declaration order against a plain sibling overlapping it -- that's why
    /// the icon kept ending up underneath a card's white background no
    /// matter how the Grid ordering/ZIndex was arranged.</summary>
    private void EnsureLookLinkAdorner()
    {
        if (_lookLinkAdorner is not null) return;
        var layer = AdornerLayer.GetAdornerLayer(LookLinkConnector);
        if (layer is null) return;

        _lookLinkBar = new Border
        {
            Width = 3,
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(1.5),
        };
        _lookLinkIcon = new Border
        {
            Width = 22,
            Height = 22,
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(11),
            Child = new TextBlock
            {
                Text = "🔗",
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var canvas = new Canvas();
        canvas.Children.Add(_lookLinkBar);
        canvas.Children.Add(_lookLinkIcon);

        _lookLinkAdorner = new ConnectorAdorner(LookLinkConnector, canvas);
        layer.Add(_lookLinkAdorner);
    }

    /// <summary>Measures where AvatarBrightnessRow (the first of the 7 shared
    /// rows) and AvatarLookDivider (the boundary before 境界ぼかし) actually
    /// rendered, in LookLinkConnector's own coordinate space (which the
    /// adorner shares, since it's anchored to that same element), and
    /// positions LookLinkBar/LookLinkIcon to span exactly that -- both
    /// cards' rows line up (see the 3-column layout comment on
    /// CompositePanel), so the avatar card's own positions are a reliable
    /// stand-in for "where the shared block starts and ends" without needing
    /// to measure the photo card too.</summary>
    private void PositionLookLinkConnector()
    {
        if (_lookLinkBar is null || _lookLinkIcon is null) return;

        double top = AvatarBrightnessRow.TranslatePoint(new Point(0, 0), LookLinkConnector).Y;
        double bottom = AvatarLookDivider.TranslatePoint(new Point(0, 0), LookLinkConnector).Y;
        double height = Math.Max(0, bottom - top);

        // LookLinkConnector is this 12px gutter column; center the 3px bar
        // and 22px icon over it instead of at its left edge.
        Canvas.SetLeft(_lookLinkBar, (12 - _lookLinkBar.Width) / 2.0);
        Canvas.SetTop(_lookLinkBar, top);
        _lookLinkBar.Height = height;

        // Centered on the bar's own span, not the whole column's height.
        Canvas.SetLeft(_lookLinkIcon, (12 - _lookLinkIcon.Width) / 2.0);
        Canvas.SetTop(_lookLinkIcon, top + height / 2 - _lookLinkIcon.Height / 2);
    }

    /// <summary>Hosts an arbitrary UIElement (here, the bar+icon Canvas) as an
    /// Adorner -- a large fixed Measure/Arrange size rather than the
    /// adorned element's own tiny size, since adorners routinely need to
    /// render outside the element they're attached to (that's the entire
    /// point of using one here), and Canvas doesn't use its own assigned
    /// size to constrain where its Canvas.Left/Top-positioned children end up
    /// anyway.</summary>
    private sealed class ConnectorAdorner : Adorner
    {
        private readonly UIElement _child;

        public ConnectorAdorner(UIElement adornedElement, UIElement child) : base(adornedElement)
        {
            _child = child;
            AddVisualChild(child);
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(new Size(2000, 2000));
            return new Size(0, 0);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(0, 0, 2000, 2000));
            return finalSize;
        }
    }

    /// <summary>The avatar-look-changed half of the link: shifts the given
    /// photo-look field by <paramref name="delta"/> (clamped to its valid
    /// range) instead of copying the avatar's new value outright, so an
    /// existing difference between the two survives the move. Called from
    /// each avatar-look slider/box handler with that field's own delta --
    /// there's no single shared OverlayState.PropertyChanged hook for this
    /// direction (unlike the old copy-based link) because computing a delta
    /// needs the value from just before this specific change, which a
    /// property-changed callback firing after the fact no longer has.</summary>
    private void ShiftPhotoIfLinked(ref double photoField, double delta, double min, double max)
    {
        if (!_lookLinked || delta == 0) return;
        photoField = Math.Clamp(photoField + delta, min, max);
        RefreshPhotoLookUI();
        ScheduleCompositeRender();
    }

    /// <summary>True while any color-adjustment slider (avatar-image or
    /// photo look) is being actively dragged -- RenderCompositePreview
    /// skips updating the save-quality result (_lastComposite) until it
    /// goes back to false. See PngColorSlider*/PhotoColorSlider*.</summary>
    private bool _isCompositeDragging;

    /// <summary>True once the user explicitly chooses "アバターなしで進める"
    /// -- makes RenderCompositePreview treat the avatar as absent for this
    /// composite even if one happens to be loaded (e.g. restored from the
    /// last session), since the avatar image is shared global state with
    /// Align mode and unloading it outright would affect that too. Cleared
    /// automatically the moment an avatar is (re-)loaded, since that's an
    /// explicit signal the user wants it back in the composite.</summary>
    private bool _compositeSkipAvatar;

    // ---- Where the avatar lands on the photo: in photo PIXEL coordinates
    //      (not a fraction), initialized once per photo from either VRChat's
    //      live frame or a fit-nicely fallback, then editable via the
    //      CompositeX/Y/Width/Rotation sliders in the "配置" card (or
    //      resettable back to that initial guess) -- independent of Align
    //      mode's own X/Y/Width, which are screen pixels relative to VRChat
    //      and meaningless once divorced from a live VRChat window. ----

    private double _compositePlaceX, _compositePlaceY, _compositePlaceWidth, _compositePlaceHeight;
    private double _compositeRotation;

    private bool _compositePlacementInitialized;

    /// <summary>null = no crop (saved image stays the photo's own size,
    /// today's default) -- otherwise a width/height ratio applied to the
    /// FINISHED composite as the last step. See ImageAdjustment.CropToAspect
    /// and ApplyCanvasCrop below.</summary>
    private double? _canvasAspectRatio;

    /// <summary>0..100, where the crop window sits along whichever axis has
    /// slack once <see cref="_canvasAspectRatio"/> is applied (50 = centered,
    /// the default). Only matters when a non-null ratio is selected.</summary>
    private double _canvasCropOffsetX = 50, _canvasCropOffsetY = 50;

    /// <summary>10..100, percentage of the ratio-maximal crop box (100 =
    /// today's default: the largest box of <see cref="_canvasAspectRatio"/>
    /// that fits in the photo). Values below 100 shrink the crop box without
    /// changing the ratio -- a zoom-in-place knob layered on the aspect-ratio
    /// pick, which also gives the crop position slack on BOTH axes even
    /// when the ratio alone would otherwise pin one axis to the photo's own
    /// full extent.</summary>
    private double _canvasCropWidthPercent = 100;

    /// <summary>10..100, same idea as _canvasCropWidthPercent's own zoom-in-
    /// place knob, but only meaningful in 自由 (free) mode -- i.e. when
    /// _canvasAspectRatio is null. In every fixed-ratio mode the ratio ties
    /// height to width, so _canvasCropWidthPercent alone drives both; 自由
    /// has no ratio to tie them together, so height needs its own
    /// independent knob.</summary>
    private double _canvasCropHeightPercent = 100;

    /// <summary>True while 切り抜きモード is toggled on, keeping the crop
    /// boundary + corner handles up on the preview to drag directly. While
    /// true, RenderCompositePreview skips the final crop step and shows the
    /// FULL uncropped composite instead, with UpdateCanvasCropBoundary
    /// dimming the part that would be thrown away -- the standard photo-
    /// editor crop-tool convention, so it's clear how much of the photo the
    /// current crop keeps without having to guess from the already-cropped
    /// result alone. See CropModeToggle_Changed, CanvasCropHandle_*,
    /// CanvasCropBoundary_*.</summary>
    private bool _isCropModeActive;

    private ImageAdjustment.ColorAdjustments PhotoAdjustments => new(
        _photoBrightness, _photoContrast, _photoSaturation,
        _photoVibrance, _photoTemperature, _photoTint, _photoHue,
        _photoHighlights, _photoShadows, _photoWhites, _photoBlacks,
        _photoColorTintStrength, _photoColorTintR, _photoColorTintG, _photoColorTintB);

    /// <summary>The avatar's own current look-adjustment values, mirroring
    /// <see cref="PhotoAdjustments"/> -- used by the "look match" buttons
    /// (see MatchAvatarToPhotoButton_Click/MatchPhotoToAvatarButton_Click)
    /// to render the avatar's CURRENT look when it's the match target.</summary>
    private ImageAdjustment.ColorAdjustments CurrentAvatarAdjustments => new(
        _state.Brightness, _state.Contrast, _state.Saturation,
        _state.Vibrance, _state.Temperature, _state.Tint, _state.Hue,
        _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks);

    /// <summary>Dominant-color count for both Match buttons' clustering
    /// pass (see ImageAdjustment.ComputeDominantClusters/
    /// SolveMatchAdjustmentsClustered) -- enough to separate an avatar's
    /// main color regions (skin/hair/clothing) without over-fragmenting
    /// into noise.</summary>
    private const int MatchLookClusterCount = 4;

    /// <summary>Nudges the avatar's look-adjustment sliders toward the
    /// background photo's current look (see ImageAdjustment.
    /// SolveMatchAdjustmentsClustered) -- the source stats come from the avatar's
    /// pristine, unadjusted pixels (masked to its opaque cutout only), the
    /// target stats from the photo as CURRENTLY adjusted, so matching
    /// against an already-tweaked photo look targets what's actually
    /// visible right now, not the raw screenshot. Hue is deliberately left
    /// untouched (the solved value is computed but never applied) -- per
    /// explicit request, the avatar's own hue shouldn't shift just because
    /// its clustering happened to pair against an unrelated background
    /// color. Turns 一括調整 off first (matching is a one-sided nudge, the
    /// opposite of what "move together" means) and shows the spinning
    /// loading indicator. The actual number-crunching runs on a background
    /// thread (Task.Run) rather than just being deferred to the next
    /// Dispatcher tick like ShowComposite's own slow render is -- a
    /// synchronous call on the UI thread would still block WPF's own
    /// rendering/animation pump for its whole duration, which is exactly
    /// why the spinner used to visibly freeze mid-spin instead of rotating
    /// the entire time. ComputeLookStats/ComputeDominantClusters/
    /// SolveMatchAdjustmentsClustered and ApplyColorToPixelBuffer (used
    /// here instead of ApplyColorOnly/ApplyColor) all operate on plain
    /// PixelBuffer byte arrays with no WPF/Dispatcher-affinitized objects,
    /// so they're safe to run off the UI thread.</summary>
    private async void MatchAvatarToPhotoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow.OriginalPixelBuffer is not { } avatarRaw) return;
        if (_photoPixelBuffer is not { } photoBuffer) return;

        if (_lookLinked) LookLinkToggle.IsChecked = false;
        MatchAvatarToPhotoButton.IsEnabled = false;
        MatchPhotoToAvatarButton.IsEnabled = false;
        ShowCompositeLoading();

        var photoAdjustments = PhotoAdjustments;
        var result = await Task.Run(() =>
        {
            var sourceStats = ImageAdjustment.ComputeLookStats(avatarRaw, maskByAlpha: true);
            var adjustedPhoto = ImageAdjustment.ApplyColorToPixelBuffer(photoBuffer, photoAdjustments);
            var targetStats = ImageAdjustment.ComputeLookStats(adjustedPhoto, maskByAlpha: false);
            var sourceClusters = ImageAdjustment.ComputeDominantClusters(avatarRaw, maskByAlpha: true, k: MatchLookClusterCount);
            var targetClusters = ImageAdjustment.ComputeDominantClusters(adjustedPhoto, maskByAlpha: false, k: MatchLookClusterCount);
            return ImageAdjustment.SolveMatchAdjustmentsClustered(sourceClusters, targetClusters, sourceStats, targetStats);
        });

        _undo.BeginChange();
        // Each of these 11 assignments fires _state.PropertyChanged, which
        // synchronously triggers a preview render -- with _isCompositeDragging
        // set, none of those intermediate renders gets treated as the save-
        // quality result (see RenderCompositePreview's own doc comment).
        // FinishMatchRender does the one render that actually commits,
        // afterward.
        _isCompositeDragging = true;
        _state.BeginBatch();
        _state.Brightness = result.Brightness;
        _state.Contrast = result.Contrast;
        _state.Saturation = result.Saturation;
        _state.Vibrance = result.Vibrance;
        _state.Temperature = result.Temperature;
        _state.Tint = result.Tint;
        _state.Highlights = result.Highlights;
        _state.Shadows = result.Shadows;
        _state.Whites = result.Whites;
        _state.Blacks = result.Blacks;
        _state.EndBatch();
        _isCompositeDragging = false;
        // The quick low-res renders just triggered above (via
        // _state.PropertyChanged) each re-enable these buttons on their own
        // (see RenderCompositePreview) -- re-disable here so they stay
        // disabled through FinishMatchRender's still-pending full-res pass.
        MatchAvatarToPhotoButton.IsEnabled = false;
        MatchPhotoToAvatarButton.IsEnabled = false;
        _undo.CommitChange();
        FinishMatchRender();
    }

    /// <summary>The reverse of <see cref="MatchAvatarToPhotoButton_Click"/>:
    /// nudges the photo's look-adjustment sliders toward the avatar's
    /// current (already-adjusted) look. Same Hue-left-untouched/一括調整-off/
    /// background-thread treatment as that method.</summary>
    private async void MatchPhotoToAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_photoPixelBuffer is not { } photoBuffer) return;
        if (_overlayWindow.OriginalPixelBuffer is not { } avatarRaw) return;

        if (_lookLinked) LookLinkToggle.IsChecked = false;
        MatchAvatarToPhotoButton.IsEnabled = false;
        MatchPhotoToAvatarButton.IsEnabled = false;
        ShowCompositeLoading();

        var avatarAdjustments = CurrentAvatarAdjustments;
        var result = await Task.Run(() =>
        {
            var sourceStats = ImageAdjustment.ComputeLookStats(photoBuffer, maskByAlpha: false);
            var adjustedAvatar = ImageAdjustment.ApplyColorToPixelBuffer(avatarRaw, avatarAdjustments);
            var targetStats = ImageAdjustment.ComputeLookStats(adjustedAvatar, maskByAlpha: true);
            var sourceClusters = ImageAdjustment.ComputeDominantClusters(photoBuffer, maskByAlpha: false, k: MatchLookClusterCount);
            var targetClusters = ImageAdjustment.ComputeDominantClusters(adjustedAvatar, maskByAlpha: true, k: MatchLookClusterCount);
            return ImageAdjustment.SolveMatchAdjustmentsClustered(sourceClusters, targetClusters, sourceStats, targetStats);
        });

        _undo.BeginChange();
        _photoBrightness = result.Brightness;
        _photoContrast = result.Contrast;
        _photoSaturation = result.Saturation;
        _photoVibrance = result.Vibrance;
        _photoTemperature = result.Temperature;
        _photoTint = result.Tint;
        _photoHighlights = result.Highlights;
        _photoShadows = result.Shadows;
        _photoWhites = result.Whites;
        _photoBlacks = result.Blacks;
        RefreshPhotoLookUI();
        MatchAvatarToPhotoButton.IsEnabled = false;
        MatchPhotoToAvatarButton.IsEnabled = false;
        _undo.CommitChange();
        FinishMatchRender();
    }

    /// <summary>True once <see cref="WarmUpGpuPipelineAsync"/> has actually
    /// run -- see its own doc comment for why this exists at all. Set
    /// synchronously (before the first `await`) so a re-entrant call from
    /// the UI thread never sees the check-then-set race a genuinely async
    /// guard would need.</summary>
    private bool _gpuPipelineWarmedUp;

    /// <summary>Runs the ENTIRE GPU effect chain once, on a tiny throwaway
    /// buffer whose output is discarded, so every shader's one-time driver-
    /// side compile cost (see GpuCompositeChain's own doc comment, and
    /// GpuProfile's "ComputeSharp compiles each shader lazily on its first-
    /// ever dispatch" finding) lands here -- always under
    /// ShowCompositeLoading/HideCompositeLoading, since this only ever runs
    /// from inside FinishMatchRender's own loading-covered dispatch --
    /// instead of on whichever slider the user happens to touch first.
    ///
    /// The composite-mode-entry render alone does NOT already cover this,
    /// even though it also runs under the loading spinner: most finishing
    /// effects default to amount=0 (off), and every one of them
    /// early-returns WITHOUT ever dispatching its own shader when
    /// amount&lt;=0 (see e.g. GpuFilmGrain.ApplyToTexture, GpuFinishingEffects'
    /// own `any` guard) -- so a render at default settings never actually
    /// compiles most of this pipeline. This forces every stage on instead
    /// (non-zero amounts, drop shadow tone AND blur AND a real overlay) so
    /// nothing is skipped. Runs at most once per app session (see
    /// _gpuPipelineWarmedUp) -- every call after the first is a no-op.
    ///
    /// Also warms GpuAvatarEdgeBlur (境界ぼかし) separately: its JFA-based
    /// shaders (ImageAdjustment.BlurPng, used by OverlayWindow.
    /// ApplyImageAdjustments for the avatar's own edge-blur slider, in
    /// EITHER Align or Composite mode) are a completely different shader
    /// pair from anything CompositeOverlayOntoPhoto touches, so the call
    /// above wouldn't have compiled them.</summary>
    private Task WarmUpGpuPipelineAsync()
    {
        if (_gpuPipelineWarmedUp) return Task.CompletedTask;
        _gpuPipelineWarmedUp = true;

        return Task.Run(async () =>
        {
            const int w = 32, h = 32, stride = w * 4;
            var dummyPixels = new byte[stride * h];
            for (int i = 3; i < dummyPixels.Length; i += 4) dummyPixels[i] = 255;
            var dummyPhoto = new ImageAdjustment.PixelBuffer(dummyPixels, w, h, stride);

            var overlayPixels = new byte[stride * h];
            for (int i = 3; i < overlayPixels.Length; i += 4) overlayPixels[i] = 255;

            var adj = new ImageAdjustment.ColorAdjustments(
                Brightness: 10, Contrast: 0, Saturation: 0, Vibrance: 0, Temperature: 0, Tint: 0, Hue: 0,
                Highlights: 0, Shadows: 0, Whites: 0, Blacks: 0,
                ColorTintStrength: 0, ColorTintR: 0, ColorTintG: 0, ColorTintB: 0);

            await _compositeRenderGate.WaitAsync();
            try
            {
                ImageAdjustment.CompositeOverlayOntoPhoto(
                    dummyPhoto, adj,
                    overlayPixels, stride, w, h, 0, 0,
                    grainAmount: 50, vignetteAmount: 50, photoBlurAmount: 50, photoBlurScale: 1.0,
                    softnessAmount: 50, sharpnessAmount: 50, finishDetailScale: 1.0,
                    fadeAmount: 50, glowAmount: 50, glowScale: 1.0,
                    chromaticAberrationAmount: 50, colorBleedAmount: 50, scanlineAmount: 50,
                    vhsScale: 1.0, clarityAmount: 50, clarityScale: 1.0, lightLeakAmount: 50,
                    lightLeakAngle: 45, lightLeakDistance: 0.5,
                    lightLeakColorB: 0, lightLeakColorG: 0, lightLeakColorR: 0,
                    toneGradientAmount: 50, toneGradientRotation: 0,
                    dropShadowAmount: 50, dropShadowDirection: 0, dropShadowDistance: 5, dropShadowBlur: 3,
                    dropShadowColorB: 0, dropShadowColorG: 0, dropShadowColorR: 0, dropShadowScale: 1.0,
                    dropShadowTone: true, dropShadowDotSize: 4, dropShadowBlendMode: ImageAdjustment.DropShadowBlendMode.Normal);

                var edgeBlurPixels = (byte[])overlayPixels.Clone();
                GpuAvatarEdgeBlur.TryApply(edgeBlurPixels, stride, w, h, edgeBlurRadius: 5);
            }
            finally
            {
                _compositeRenderGate.Release();
            }
        });
    }

    /// <summary>Finishes a Match button click: does the one full-resolution
    /// preview render both handlers above still need (see their own
    /// _isCompositeDragging-wrapped quick renders), deferred to the next
    /// Background-priority dispatch so the loading spinner gets a chance to
    /// actually animate before that render starts, and only hides the
    /// loading indicator once it's genuinely done -- a full-resolution
    /// recomposite of a multi-megapixel photo is unavoidably slow enough to
    /// block the UI thread for a moment (see RenderCompositePreview/
    /// CompositeRenderThrottle's own comment on this), so this keeps the
    /// spinner up through that moment instead of hiding it early and
    /// leaving the preview looking frozen with no loading indicator to
    /// explain why. Also where WarmUpGpuPipelineAsync runs (see its own
    /// doc comment) -- this is the first render of every composite-mode
    /// session, so the loading spinner is already up regardless.</summary>
    private void FinishMatchRender()
    {
        _pendingCompositeRenderTimer?.Stop();
        Dispatcher.InvokeAsync(async () =>
        {
            await WarmUpGpuPipelineAsync();
            await RenderCompositePreview();
            _lastCompositeRender = DateTime.UtcNow;
            HideCompositeLoading();
        }, DispatcherPriority.Background);
    }

    private CompositeSnapshot CaptureCompositeSnapshot() => new(
        _photoBrightness, _photoContrast, _photoSaturation,
        _photoVibrance, _photoTemperature, _photoTint, _photoHue,
        _photoHighlights, _photoShadows, _photoWhites, _photoBlacks,
        _photoColorTintStrength, _photoColorTintR, _photoColorTintG, _photoColorTintB,
        _photoBlurAmount,
        _grainAmount, _vignetteAmount,
        _softnessAmount, _sharpnessAmount,
        _fadeAmount, _glowAmount,
        _chromaticAberrationAmount, _colorBleedAmount, _scanlineAmount,
        _clarityAmount, _lightLeakAmount, _lightLeakAngle, _lightLeakDistance,
        _lightLeakColorB, _lightLeakColorG, _lightLeakColorR,
        _toneGradientAmount, _toneGradientRotation,
        _toneGradientLightR, _toneGradientLightG, _toneGradientLightB,
        _toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB,
        _dropShadowAmount, _dropShadowDirection, _dropShadowDistance, _dropShadowBlur,
        _dropShadowColorB, _dropShadowColorG, _dropShadowColorR,
        _dropShadowBlendMode,
        _canvasAspectRatio, _canvasCropOffsetX, _canvasCropOffsetY, _canvasCropWidthPercent, _canvasCropHeightPercent,
        _compositePlaceX, _compositePlaceY, _compositePlaceWidth, _compositePlaceHeight,
        _compositeRotation);

    private void ApplyCompositeSnapshot(object? snapshot)
    {
        if (snapshot is not CompositeSnapshot s) return;
        _photoBrightness = s.PhotoBrightness;
        _photoContrast = s.PhotoContrast;
        _photoSaturation = s.PhotoSaturation;
        _photoVibrance = s.PhotoVibrance;
        _photoTemperature = s.PhotoTemperature;
        _photoTint = s.PhotoTint;
        _photoHue = s.PhotoHue;
        _photoHighlights = s.PhotoHighlights;
        _photoShadows = s.PhotoShadows;
        _photoWhites = s.PhotoWhites;
        _photoBlacks = s.PhotoBlacks;
        _photoColorTintStrength = s.PhotoColorTintStrength;
        _photoColorTintR = s.PhotoColorTintR;
        _photoColorTintG = s.PhotoColorTintG;
        _photoColorTintB = s.PhotoColorTintB;
        _photoBlurAmount = s.PhotoBlurAmount;
        _grainAmount = s.GrainAmount;
        _vignetteAmount = s.VignetteAmount;
        _softnessAmount = s.SoftnessAmount;
        _sharpnessAmount = s.SharpnessAmount;
        _fadeAmount = s.FadeAmount;
        _glowAmount = s.GlowAmount;
        _chromaticAberrationAmount = s.ChromaticAberrationAmount;
        _colorBleedAmount = s.ColorBleedAmount;
        _scanlineAmount = s.ScanlineAmount;
        _clarityAmount = s.ClarityAmount;
        _lightLeakAmount = s.LightLeakAmount;
        _lightLeakAngle = s.LightLeakAngle;
        _lightLeakDistance = s.LightLeakDistance;
        _lightLeakColorB = s.LightLeakColorB;
        _lightLeakColorG = s.LightLeakColorG;
        _lightLeakColorR = s.LightLeakColorR;
        _toneGradientAmount = s.ToneGradientAmount;
        _toneGradientRotation = s.ToneGradientRotation;
        _toneGradientLightR = s.ToneGradientLightR;
        _toneGradientLightG = s.ToneGradientLightG;
        _toneGradientLightB = s.ToneGradientLightB;
        _toneGradientDarkR = s.ToneGradientDarkR;
        _toneGradientDarkG = s.ToneGradientDarkG;
        _toneGradientDarkB = s.ToneGradientDarkB;
        _dropShadowAmount = s.DropShadowAmount;
        _dropShadowDirection = s.DropShadowDirection;
        _dropShadowDistance = s.DropShadowDistance;
        _dropShadowBlur = s.DropShadowBlur;
        _dropShadowColorB = s.DropShadowColorB;
        _dropShadowColorG = s.DropShadowColorG;
        _dropShadowColorR = s.DropShadowColorR;
        _dropShadowBlendMode = s.DropShadowBlendMode;
        _canvasAspectRatio = s.CanvasAspectRatio;
        _canvasCropOffsetX = s.CanvasCropOffsetX;
        _canvasCropOffsetY = s.CanvasCropOffsetY;
        _canvasCropWidthPercent = s.CanvasCropWidthPercent;
        _canvasCropHeightPercent = s.CanvasCropHeightPercent;
        _compositePlaceX = s.CompositePlaceX;
        _compositePlaceY = s.CompositePlaceY;
        _compositePlaceWidth = s.CompositePlaceWidth;
        _compositePlaceHeight = s.CompositePlaceHeight;
        _compositeRotation = s.CompositeRotation;
        RefreshPhotoLookUI();
        RefreshFinishUI();
        RefreshCompositePlacementUI();
        ScheduleCompositeRender();
    }

    /// <summary>The currently-loaded composite photo's path, or null -- read
    /// by App.xaml.cs at exit to persist it, the same way OverlayState's own
    /// ImagePath already is.</summary>
    public string? PhotoPath => _photoPath;

    private bool TryLoadPhotoPixels(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            _photoPixelBuffer = ImageAdjustment.PrepareBuffer(bitmap);
            // Film grain's noise field only depends on width/height (fixed
            // seed), so build it now instead of paying for it on the first
            // render -- see PrecomputeFilmGrainNoise.
            ImageAdjustment.PrecomputeFilmGrainNoise(_photoPixelBuffer.Width, _photoPixelBuffer.Height);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return false;
        }

        _compositePlacementInitialized = false; // a new photo needs its own fresh placement guess
        _photoPath = path;
        PhotoPathText.Text = Path.GetFileName(path);
        return true;
    }

    /// <summary>Loads a photo (from the manual picker or a screenshot-watcher
    /// toast click) and brings the control panel to the front -- a toast is
    /// deliberately non-activating (see <see cref="ScreenshotToastWindow"/>),
    /// so clicking "合成する" on one wouldn't otherwise bring this window
    /// forward on its own.</summary>
    public void LoadPhotoForComposite(string path)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (!TryLoadPhotoPixels(path))
        {
            ShowComposite();
            ShowCompositeSaveStatus("背景写真を読み込めませんでした。", success: false);
            return;
        }

        _photoBrightness = _photoContrast = _photoSaturation = 0;
        _photoVibrance = _photoTemperature = _photoTint = _photoHue = 0;
        _photoHighlights = _photoShadows = _photoWhites = _photoBlacks = 0;
        RefreshPhotoLookUI();
        ClearCompositeSaveStatus();
        ShowComposite();
    }

    /// <summary>Restores the last-used composite photo at startup -- silently,
    /// without switching to Composite mode or stealing focus, the same
    /// "ready if you go there" treatment as the PNG's own restored
    /// ImagePath.</summary>
    public void RestorePhotoSilently(string path) => TryLoadPhotoPixels(path);

    private void PickPhotoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "画像 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Title = "VRChatで撮った背景写真を選択",
        };
        if (dialog.ShowDialog() == true)
        {
            LoadPhotoForComposite(dialog.FileName);
        }
    }

    /// <summary>Where on the photo (as fractions of the photo's own pixel
    /// dimensions) the aligned overlay should land: the live preview places
    /// the overlay at _state.X/Y/Width/Height relative to the estimated
    /// camera-frame rect within VRChat's CURRENT client area, and the photo IS
    /// exactly that frame's content at its own output resolution -- so the
    /// same fractional position/size within the frame maps directly onto the
    /// photo. Takes the photo's own aspect ratio and uses it for the frame's
    /// WIDTH instead of the hardcoded 16:9/9:16 assumption -- otherwise, if
    /// VRChat's camera resolution isn't exactly 16:9/9:16, fracW and fracH
    /// would be scaled by DIFFERENT amounts once applied to the photo's
    /// actual pixel dimensions, stretching the overlay (the reported bug:
    /// the overlay came out taller/narrower than the source PNG even with a
    /// same-resolution test photo, because the frame estimate itself assumed
    /// the wrong aspect ratio, not because of anything about the PNG file).</summary>
    private (double FracX, double FracY, double FracW, double FracH)? ComputeOverlayFrameFraction(double photoAspect)
    {
        if (_oscListener.IsLandscape is not { } landscape) return null;
        var hwnd = VRChatWindowService.FindVRChatWindow();
        if (hwnd is null) return null;
        if (VRChatWindowService.GetClientRectOnScreen(hwnd.Value) is not { Width: > 0, Height: > 0 } region) return null;

        var frame = VRChatWindowService.ComputeCameraFrameRect(region, landscape, photoAspect);
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        double fracX = (_state.X - frame.Left) / frame.Width;
        double fracY = (_state.Y - frame.Top) / frame.Height;
        double fracW = _state.Width / frame.Width;
        double fracH = _state.Height / frame.Height;
        return (fracX, fracY, fracW, fracH);
    }

    /// <summary>One-time-per-photo placement guess: VRChat's live frame if
    /// it's running and has reported a position/orientation, otherwise the
    /// avatar's own aspect ratio fit into a comfortable margin of the photo
    /// (rather than a flat height guess, so it looks reasonable regardless of
    /// the photo's or the avatar's own aspect ratio). After this, the user
    /// drags/wheel-zooms directly on the preview to adjust it -- see
    /// PreviewImage_MouseLeftButtonDown/MouseMove/MouseWheel below -- so
    /// re-running this (via "自動配置に戻す") is the only other way it
    /// changes.</summary>
    private void InitializeCompositePlacementIfNeeded(ImageAdjustment.PixelBuffer photoBuffer, BitmapSource overlaySource)
    {
        if (_compositePlacementInitialized) return;
        _compositePlacementInitialized = true;

        double photoAspect = (double)photoBuffer.Width / photoBuffer.Height;
        var frac = ComputeOverlayFrameFraction(photoAspect);
        if (frac is { } f && f.FracW * photoBuffer.Width > 0 && f.FracH * photoBuffer.Height > 0)
        {
            _compositePlaceX = f.FracX * photoBuffer.Width;
            _compositePlaceY = f.FracY * photoBuffer.Height;
            _compositePlaceWidth = f.FracW * photoBuffer.Width;
            _compositePlaceHeight = f.FracH * photoBuffer.Height;
        }
        else
        {
            // Fit the avatar's own aspect ratio into 100% of the photo --
            // whichever dimension is more constraining (the "longer" side
            // relative to the photo's own shape) determines the scale, so it
            // touches the photo's edge on that axis with no wasted margin.
            var native = _overlayWindow.ImageNativeSize;
            double nativeWidth = native is { Width: > 0 } n ? n.Width : overlaySource.PixelWidth;
            double nativeHeight = native is { Height: > 0 } n2 ? n2.Height : overlaySource.PixelHeight;
            double scale = Math.Min(photoBuffer.Width / nativeWidth, photoBuffer.Height / nativeHeight);
            _compositePlaceWidth = nativeWidth * scale;
            _compositePlaceHeight = nativeHeight * scale;
            _compositePlaceX = (photoBuffer.Width - _compositePlaceWidth) / 2;
            _compositePlaceY = (photoBuffer.Height - _compositePlaceHeight) / 2;
        }
        _compositeRotation = _state.RotationDegrees;
        RefreshCompositePlacementUI();
    }

    /// <summary>No X/Y/幅/回転(度) UI left to sync here at all (see
    /// AvatarPlacementModeToggle_Changed's own removal comment) -- this now
    /// just re-derives the on-preview highlight/handles from
    /// _compositePlaceX/Y/Width/Height/_compositeRotation, alongside
    /// RefreshCanvasAspectUI for the same panel's crop controls.</summary>
    private void RefreshCompositePlacementUI()
    {
        RefreshCanvasAspectUI();
        UpdateAvatarPlacementHighlight();
    }

    /// <summary>Syncs the aspect-ratio radio group and crop-position
    /// sliders to _canvasAspectRatio/_canvasCropOffsetX/Y -- called
    /// alongside RefreshCompositePlacementUI (same 配置 panel, same
    /// refresh occasions: construction, undo/redo, snapshot restore).</summary>
    private void RefreshCanvasAspectUI()
    {
        _suppressEvents = true;
        int index = _canvasAspectRatio switch
        {
            null => 0,
            1.0 => 1,
            0.8 => 2,
            0.5625 => 3,
            1.7778 => 4,
            _ => 5, // カスタム -- any ratio that doesn't match one of the five presets
        };
        CanvasAspectCombo.SelectedIndex = index;
        // Only shown/populated for カスタム -- see CanvasAspectCustomRow's own
        // XAML comment on why these two boxes read _canvasAspectRatio directly
        // rather than needing their own persisted state. RefreshCanvasAspectUI
        // is called constantly for reasons that have nothing to do with these
        // two boxes specifically (e.g. every CanvasCropHandle_MouseMove tick
        // while dragging a crop corner), so it must NOT blindly stomp
        // whatever the user actually typed (e.g. "3"/"4") back to a derived
        // "0.75"/"1" on every one of those calls -- only rewrite when the
        // boxes' own current text no longer reduces to the same ratio
        // (undo/redo, a preset pick, or a fresh custom-ratio load).
        CanvasAspectCustomRow.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 5 && _canvasAspectRatio is { } customRatio)
        {
            bool displayedMatches = TryParse(CanvasAspectCustomWidthBox.Text, out var dw) && dw > 0
                && TryParse(CanvasAspectCustomHeightBox.Text, out var dh) && dh > 0
                && Math.Abs(dw / dh - customRatio) < 0.0005;
            if (!displayedMatches)
            {
                CanvasAspectCustomWidthBox.Text = customRatio.ToString("0.###", CultureInfo.InvariantCulture);
                CanvasAspectCustomHeightBox.Text = "1";
            }
        }
        _suppressEvents = false;
    }

    private void CanvasAspectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (CanvasAspectCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        if (tag == "custom")
        {
            // Keeps whatever ratio is already active (falling back to 1:1
            // only the first time, when there was no ratio at all) instead
            // of resetting it -- picking カスタム right after already being
            // on 4:5, say, should just let the boxes show/refine 0.8:1, not
            // silently jump to some other starting value.
            _canvasAspectRatio ??= 1.0;
            _suppressEvents = true;
            CanvasAspectCustomRow.Visibility = Visibility.Visible;
            CanvasAspectCustomWidthBox.Text = _canvasAspectRatio.Value.ToString("0.###", CultureInfo.InvariantCulture);
            CanvasAspectCustomHeightBox.Text = "1";
            _suppressEvents = false;
            ScheduleCompositeRender();
            return;
        }
        CanvasAspectCustomRow.Visibility = Visibility.Collapsed;
        _canvasAspectRatio = tag == "original" ? null : double.Parse(tag, CultureInfo.InvariantCulture);
        ScheduleCompositeRender();
    }

    /// <summary>Shared by both CanvasAspectCustomWidthBox and
    /// CanvasAspectCustomHeightBox (sender-based, like the PNG-look handlers
    /// elsewhere in this file): _canvasAspectRatio is just their quotient,
    /// recomputed from whichever two numbers are currently in the boxes
    /// every time either one changes.</summary>
    private void CanvasAspectCustomRatio_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(CanvasAspectCustomWidthBox.Text, out var w) || w <= 0) return;
        if (!TryParse(CanvasAspectCustomHeightBox.Text, out var h) || h <= 0) return;
        _canvasAspectRatio = w / h;
        ScheduleCompositeRender();
    }

    private void CropModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool turningOn = CropModeToggle.IsChecked == true && !_isCropModeActive;
        _isCropModeActive = CropModeToggle.IsChecked == true;
        // Captured on the OFF->ON transition only, so a キャンセル click (see
        // PreviewModeCancelButton_Click) can restore exactly what was there
        // right before this particular session of dragging started.
        if (turningOn) _cropModeEntrySnapshot = CaptureCompositeSnapshot();
        // Mutually exclusive with アバター配置モード -- both want the
        // preview's own click-drag gestures to themselves; see
        // AvatarPlacementModeToggle_Changed's matching check.
        if (_isCropModeActive && _isAvatarPlacementModeActive)
        {
            AvatarPlacementModeToggle.IsChecked = false;
        }
        ScheduleCompositeRender();
        // Also update immediately rather than waiting for the (debounced)
        // render to come back around to its own UpdateCanvasCropBoundary
        // call -- toggling should show/hide the boundary+handles the
        // instant it's clicked, not after a render-cycle delay.
        UpdateCanvasCropBoundary();

        CropModeLabel.Foreground = _isCropModeActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        CropModeLabel.FontWeight = _isCropModeActive ? FontWeights.SemiBold : FontWeights.Normal;
        // Avatar placement (X/Y/幅/回転) edits alongside a crop drag would
        // be confusing -- see CompositePlacementControlsPanel's own XAML
        // comment -- so the whole group is disabled instead of left
        // interactive but misleading.
        CompositePlacementControlsPanel.IsEnabled = !_isCropModeActive;
        RefreshSliderLockState();
    }

    /// <summary>No more X/Y/幅/回転(度) sliders at all in Composite mode's
    /// 配置 panel -- this toggle plus direct drag on the preview fully
    /// replaces them (matching how Align mode's own placement always
    /// worked, via OverlayWindow's handle/gizmo drag on the live VRChat
    /// overlay), so there's nothing left to gray out here besides itself.</summary>
    private void AvatarPlacementModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool turningOn = AvatarPlacementModeToggle.IsChecked == true && !_isAvatarPlacementModeActive;
        _isAvatarPlacementModeActive = AvatarPlacementModeToggle.IsChecked == true;
        // Captured on the OFF->ON transition only, so a キャンセル click (see
        // PreviewModeCancelButton_Click) can restore exactly what was there
        // right before this particular session of dragging started.
        if (turningOn) _avatarPlacementModeEntrySnapshot = CaptureCompositeSnapshot();
        // Mutually exclusive with 切り抜きモード; see CropModeToggle_Changed's
        // matching check.
        if (_isAvatarPlacementModeActive && _isCropModeActive)
        {
            CropModeToggle.IsChecked = false;
        }

        AvatarPlacementModeLabel.Foreground = _isAvatarPlacementModeActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        AvatarPlacementModeLabel.FontWeight = _isAvatarPlacementModeActive ? FontWeights.SemiBold : FontWeights.Normal;
        // Switches the preview between the cropped/full-photo view (see
        // RenderCompositePreview's own cropAdjusting local); scheduled, not
        // instant, but UpdateAvatarPlacementHighlight below still repositions
        // the handles/highlight against GetDisplayedCropRect's new answer
        // immediately, so they don't lag a render cycle behind the toggle.
        ScheduleCompositeRender();
        UpdateAvatarPlacementHighlight();
        RefreshSliderLockState();
    }

    /// <summary>Common choke point for every "preview interaction mode"
    /// that wants exclusive ownership of the preview's own drag gestures
    /// (currently 切り抜きモード and アバター配置モード) -- graying out
    /// every look/finishing-effect slider on the right and showing
    /// SliderLockNotice while ANY of them is active, so editing a slider
    /// mid-drag can't be confused with what the drag itself is doing (see
    /// SliderLockNotice's own XAML comment). Called from each mode's own
    /// _Changed handler after it updates its own _is*ModeActive flag; a
    /// future mode just needs to OR its own flag into `locked` below and
    /// call this same method, not duplicate the IsEnabled/Visibility wiring
    /// itself.</summary>
    private void RefreshSliderLockState()
    {
        bool locked = _isCropModeActive || _isAvatarPlacementModeActive;
        CompositeCardsScrollViewer.IsEnabled = !locked;
        SliderLockNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeConfirmBar.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Snapshot taken the moment each mode's toggle flips OFF->ON
    /// (see CropModeToggle_Changed/AvatarPlacementModeToggle_Changed), so
    /// PreviewModeCancelButton_Click can restore exactly what was there
    /// before that particular editing session, not just undo one step.</summary>
    private CompositeSnapshot? _cropModeEntrySnapshot;
    private CompositeSnapshot? _avatarPlacementModeEntrySnapshot;

    /// <summary>確定: keeps whatever's currently set and just leaves the
    /// active mode, the same as clicking its own toggle off directly. Only
    /// one of the two modes can be active at a time (see the mutual-
    /// exclusion checks in each mode's own _Changed handler), so checking
    /// both here and acting on whichever is active is simpler than routing
    /// through a caller-specified mode.</summary>
    private void PreviewModeConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        else if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
    }

    /// <summary>キャンセル: restores the snapshot captured when the active
    /// mode was turned on (see _cropModeEntrySnapshot/
    /// _avatarPlacementModeEntrySnapshot) as one atomic undo step -- reusing
    /// ApplyCompositeSnapshot (normally the undo manager's own replay
    /// callback, see its ApplyExtra wiring) rather than duplicating its
    /// field-by-field restore -- then leaves the mode the same way 確定
    /// does.</summary>
    private void PreviewModeCancelButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _isCropModeActive ? _cropModeEntrySnapshot
            : _isAvatarPlacementModeActive ? _avatarPlacementModeEntrySnapshot
            : null;
        if (snapshot is { } snap)
        {
            _undo.BeginChange();
            ApplyCompositeSnapshot(snap);
            _undo.CommitChange();
        }
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        else if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
    }

    // ---- 切り抜きモード: drag the crop boundary directly on the preview
    //      instead of the 切り抜き幅/位置X/Y sliders. Two drag kinds share
    //      the same CanvasCropBoundaryOutline/handle elements: dragging a
    //      corner resizes (aspect ratio locked, anchored on the opposite
    //      corner); dragging the body moves it. Both work in PHOTO-pixel
    //      space via PreviewBorder.Width/photo.Width, matching
    //      UpdateCanvasCropBoundary's own scale -- while either drag is
    //      live, RenderCompositePreview shows the FULL uncropped photo (see
    //      the cropAdjusting local it reads), so PreviewBorder really does
    //      map 1:1 to the photo's own full extent for the duration. ----

    private enum CropHandleCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    private bool _isDraggingCropHandle;
    private CropHandleCorner _cropDragHandle;
    private Point _cropDragStartMouse;
    private double _cropDragStartWidthPercent, _cropDragStartHeightPercent, _cropDragStartOffsetX, _cropDragStartOffsetY;

    private void CanvasCropHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_photoPixelBuffer is null) return;
        var element = (FrameworkElement)sender;
        _cropDragHandle = Enum.Parse<CropHandleCorner>((string)element.Tag);
        _isDraggingCropHandle = true;
        _cropDragStartMouse = e.GetPosition(PreviewBorder);
        _cropDragStartWidthPercent = _canvasCropWidthPercent;
        _cropDragStartHeightPercent = _canvasCropHeightPercent;
        _cropDragStartOffsetX = _canvasCropOffsetX;
        _cropDragStartOffsetY = _canvasCropOffsetY;
        element.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>In a fixed-ratio mode, only the drag's horizontal component
    /// drives resizing -- width alone already determines height (see
    /// GetCanvasCropRect), so a second independent input from the vertical
    /// component would be redundant, not additive. In 自由 (free) mode
    /// (_canvasAspectRatio null) there's no ratio tying the two together, so
    /// dx and dy each drive their own axis independently instead. Either
    /// way, anchors on whichever corner is diagonally OPPOSITE the one being
    /// dragged: that corner's own photo-pixel position is held fixed by
    /// re-deriving _canvasCropOffsetX/Y from it after the resize, the
    /// standard crop-tool convention.</summary>
    private void CanvasCropHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCropHandle || _photoPixelBuffer is not { } photo) return;
        var (maxCropWidth, maxCropHeight) = GetMaxCropSize(photo.Width, photo.Height);
        if (maxCropWidth <= 0 || maxCropHeight <= 0) return;
        bool isFree = _canvasAspectRatio is null;

        double scale = PreviewBorder.Width / photo.Width;
        var current = e.GetPosition(PreviewBorder);
        double dx = (current.X - _cropDragStartMouse.X) / scale;
        double dy = (current.Y - _cropDragStartMouse.Y) / scale;

        bool right = _cropDragHandle is CropHandleCorner.TopRight or CropHandleCorner.BottomRight;
        bool bottom = _cropDragHandle is CropHandleCorner.BottomLeft or CropHandleCorner.BottomRight;
        double deltaWidth = right ? dx : -dx;
        double deltaHeight = bottom ? dy : -dy;

        double startCropWidth = maxCropWidth * _cropDragStartWidthPercent / 100.0;
        double startCropHeight = isFree
            ? maxCropHeight * _cropDragStartHeightPercent / 100.0
            : maxCropHeight * _cropDragStartWidthPercent / 100.0;
        double newCropWidth = Math.Clamp(startCropWidth + deltaWidth, maxCropWidth * 0.10, maxCropWidth);
        double newCropHeight = isFree
            ? Math.Clamp(startCropHeight + deltaHeight, maxCropHeight * 0.10, maxCropHeight)
            : newCropWidth * maxCropHeight / maxCropWidth;
        double newWidthPercent = newCropWidth / maxCropWidth * 100.0;
        double newHeightPercent = newCropHeight / maxCropHeight * 100.0;

        double startMaxLeft = photo.Width - startCropWidth;
        double startMaxTop = photo.Height - startCropHeight;
        double startLeft = startMaxLeft > 0 ? startMaxLeft * Math.Clamp(_cropDragStartOffsetX, 0, 100) / 100.0 : 0;
        double startTop = startMaxTop > 0 ? startMaxTop * Math.Clamp(_cropDragStartOffsetY, 0, 100) / 100.0 : 0;

        double anchorX = right ? startLeft : startLeft + startCropWidth;
        double anchorY = bottom ? startTop : startTop + startCropHeight;
        double newLeft = right ? anchorX : anchorX - newCropWidth;
        double newTop = bottom ? anchorY : anchorY - newCropHeight;

        double newMaxLeft = photo.Width - newCropWidth;
        double newMaxTop = photo.Height - newCropHeight;
        _canvasCropWidthPercent = newWidthPercent;
        if (isFree) _canvasCropHeightPercent = newHeightPercent;
        _canvasCropOffsetX = newMaxLeft > 0 ? Math.Clamp(newLeft / newMaxLeft * 100.0, 0, 100) : 50;
        _canvasCropOffsetY = newMaxTop > 0 ? Math.Clamp(newTop / newMaxTop * 100.0, 0, 100) : 50;

        RefreshCanvasAspectUI();
        ScheduleCompositeRender();
    }

    private void CanvasCropHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropHandle = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private bool _isDraggingCropBody;
    private Point _cropBodyDragStartMouse;
    private double _cropBodyDragStartOffsetX, _cropBodyDragStartOffsetY;

    private void CanvasCropBoundary_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_photoPixelBuffer is null) return;
        _isDraggingCropBody = true;
        _cropBodyDragStartMouse = e.GetPosition(PreviewBorder);
        _cropBodyDragStartOffsetX = _canvasCropOffsetX;
        _cropBodyDragStartOffsetY = _canvasCropOffsetY;
        CanvasCropBoundaryOutline.CaptureMouse();
        e.Handled = true;
    }

    private void CanvasCropBoundary_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCropBody || _photoPixelBuffer is not { } photo) return;
        var (maxCropWidth, maxCropHeight) = GetMaxCropSize(photo.Width, photo.Height);
        double cropWidth = maxCropWidth * _canvasCropWidthPercent / 100.0;
        double cropHeight = maxCropHeight * (_canvasAspectRatio is null ? _canvasCropHeightPercent : _canvasCropWidthPercent) / 100.0;
        double maxLeft = photo.Width - cropWidth;
        double maxTop = photo.Height - cropHeight;

        double scale = PreviewBorder.Width / photo.Width;
        var current = e.GetPosition(PreviewBorder);
        double dx = (current.X - _cropBodyDragStartMouse.X) / scale;
        double dy = (current.Y - _cropBodyDragStartMouse.Y) / scale;

        double startLeft = maxLeft > 0 ? maxLeft * Math.Clamp(_cropBodyDragStartOffsetX, 0, 100) / 100.0 : 0;
        double startTop = maxTop > 0 ? maxTop * Math.Clamp(_cropBodyDragStartOffsetY, 0, 100) / 100.0 : 0;

        _canvasCropOffsetX = maxLeft > 0 ? Math.Clamp((startLeft + dx) / maxLeft * 100.0, 0, 100) : 50;
        _canvasCropOffsetY = maxTop > 0 ? Math.Clamp((startTop + dy) / maxTop * 100.0, 0, 100) : 50;

        RefreshCanvasAspectUI();
        ScheduleCompositeRender();
    }

    private void CanvasCropBoundary_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropBody = false;
        CanvasCropBoundaryOutline.ReleaseMouseCapture();
    }

    /// <summary>Single choke point for cropping a finished composite to
    /// _canvasAspectRatio -- called at every point RenderCompositePreview/
    /// ComputeBeforeComposite produce a WriteableBitmap that ends up shown,
    /// saved, or compared, so the crop stays consistent no matter which
    /// path built the bitmap.</summary>
    private WriteableBitmap ApplyCanvasCrop(WriteableBitmap composite) =>
        ImageAdjustment.CropToAspect(composite, _canvasAspectRatio, _canvasCropOffsetX, _canvasCropOffsetY, _canvasCropWidthPercent, _canvasCropHeightPercent);

    private void ResetCompositePlacementButton_Click(object sender, RoutedEventArgs e)
    {
        _undo.BeginChange();
        _compositePlacementInitialized = false;
        _ = RenderCompositePreview();
        RefreshCompositePlacementUI();
        _undo.CommitChange();
    }

    /// <summary>Collapses/expands 配置's body, leaving just its header
    /// visible when collapsed -- floating over the preview image (see the
    /// XAML comment on PlacementPanel), it should be easy to tuck out of the
    /// way once placement is set. Not undo-tracked: purely a display
    /// preference, not an edit to anything that gets saved.</summary>
    private bool _placementPanelCollapsed;

    private void PlacementCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _placementPanelCollapsed = !_placementPanelCollapsed;
        PlacementPanelBody.Visibility = _placementPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        PlacementCollapseIcon.Data = Geometry.Parse(_placementPanelCollapsed ? "m18 15-6-6-6 6" : "m6 9 6 6 6-6");
    }

    private void CompositeSkipAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        _compositeSkipAvatar = true;
        RefreshSkipAvatarUI();
        _ = RenderCompositePreview();
    }

    /// <summary>Keeps CompositeSkipAvatarButton's enabled state/label in sync
    /// with <see cref="_compositeSkipAvatar"/> -- needed because the flag
    /// also changes programmatically (LoadImageFile clearing it when an
    /// avatar is (re-)loaded), not just from the button's own click.</summary>
    private void RefreshSkipAvatarUI()
    {
        CompositeSkipAvatarButton.IsEnabled = !_compositeSkipAvatar;
        CompositeSkipAvatarButton.Content = _compositeSkipAvatar ? "アバターなしで進行中" : "アバターなしにする";
    }

    /// <summary>Extracts an overlay-rendered bitmap's raw BGRA32 pixels --
    /// the one piece CompositeOverlayOntoPhoto used to do internally before
    /// it was changed to take raw pixels instead of a BitmapSource (see its
    /// own doc comment: that change is what makes it safe to run on a
    /// background thread). Cheap enough (a single CopyPixels over an
    /// already-rendered, placement-sized bitmap, not a multi-megapixel
    /// photo) to keep on the UI thread right after RenderOverlayForComposite,
    /// rather than adding another background hop just for this.</summary>
    private static (byte[] Pixels, int Stride, int Width, int Height) ExtractBgraPixels(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        BitmapSource converted = source.Format != PixelFormats.Bgra32
            ? new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0)
            : source;
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, stride, width, height);
    }

    /// <summary>Nearest-neighbor downscale, used ONLY to shrink the live
    /// preview render while a slider/position drag is actively in progress
    /// (see RenderCompositePreview's own renderScale computation) --
    /// quality doesn't matter here since the full-resolution result gets
    /// recomputed the instant the drag ends (<see cref="_isCompositeDragging"/>
    /// already gates _lastComposite/save quality separately from this), and
    /// PreviewImage's own Stretch="Uniform" upscales the smaller result
    /// back to fill the preview pane regardless -- exactly the same visual
    /// scaling that already happens today rendering a full-res photo down
    /// INTO a small pane, just running the opposite direction.</summary>
    internal static ImageAdjustment.PixelBuffer DownscalePixelBuffer(ImageAdjustment.PixelBuffer source, int targetWidth, int targetHeight)
    {
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);
        int targetStride = targetWidth * 4;
        var targetPixels = new byte[targetStride * targetHeight];

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = Math.Min(source.Height - 1, y * source.Height / targetHeight);
            int srcRowOffset = srcY * source.Stride;
            int dstRowOffset = y * targetStride;
            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = Math.Min(source.Width - 1, x * source.Width / targetWidth);
                int srcIdx = srcRowOffset + srcX * 4;
                int dstIdx = dstRowOffset + x * 4;
                targetPixels[dstIdx] = source.Pixels[srcIdx];
                targetPixels[dstIdx + 1] = source.Pixels[srcIdx + 1];
                targetPixels[dstIdx + 2] = source.Pixels[srcIdx + 2];
                targetPixels[dstIdx + 3] = source.Pixels[srcIdx + 3];
            }
        }

        return new ImageAdjustment.PixelBuffer(targetPixels, targetWidth, targetHeight, targetStride);
    }

    /// <summary>Single-slot cache for DownscalePixelBuffer's result, keyed by
    /// REFERENCE to the source buffer (same "replaced wholesale, never
    /// mutated in place" convention as GpuTexturePool.RentUploaded and
    /// CachedOverlayRender) plus the target size -- a slider/color drag
    /// calls RenderCompositePreview many times per second with the SAME
    /// _photoPixelBuffer reference and the SAME pane size, so this avoids
    /// redoing the downscale loop on every single tick, only on the first
    /// tick of a drag (or after the window/pane is resized mid-drag).</summary>
    private (ImageAdjustment.PixelBuffer? Source, int TargetWidth, int TargetHeight, ImageAdjustment.PixelBuffer Result)? _cachedDownscaledPhoto;

    private ImageAdjustment.PixelBuffer GetDownscaledPhoto(ImageAdjustment.PixelBuffer source, int targetWidth, int targetHeight)
    {
        if (_cachedDownscaledPhoto is { } cached && ReferenceEquals(cached.Source, source)
            && cached.TargetWidth == targetWidth && cached.TargetHeight == targetHeight)
        {
            return cached.Result;
        }

        var result = DownscalePixelBuffer(source, targetWidth, targetHeight);
        _cachedDownscaledPhoto = (source, targetWidth, targetHeight, result);
        return result;
    }

    /// <summary>Never renders the live preview at less than this fraction of
    /// native resolution, even for a source photo enormously larger than the
    /// preview pane -- purely a floor against the drag preview looking
    /// distractingly blocky, not a correctness requirement (a smaller render
    /// is strictly faster). <see cref="DragPreviewOversample"/> multiplies
    /// the pane's own DIP size before fitting the photo into it, as a cheap
    /// stand-in for not knowing the exact DPI scale factor here -- on a
    /// typical 100%-200%-DPI display this keeps the drag preview close to
    /// (or above) the pane's actual device-pixel size instead of visibly
    /// under-resolving it, while still shrinking a multi-megapixel source
    /// photo down by a large factor.</summary>
    private const double MinDragPreviewScale = 0.2;
    private const double DragPreviewOversample = 2.0;

    /// <summary>Returns 1.0 (full native resolution, same as always) unless
    /// <paramref name="dragging"/> and the preview pane is laid out and
    /// smaller than the photo -- see RenderCompositePreview's own doc
    /// comment on why a full-resolution render on every tick of a drag was
    /// the remaining source of slider lag once GpuCompositeChain's own
    /// checkpoint cache stopped helping (color/placement/drop-shadow
    /// sliders sit at or near the FRONT of that chain, so nothing downstream
    /// can be reused -- but the pane only ever displays the result at a
    /// small fraction of the photo's native pixel count in the first place,
    /// so there's no reason to compute more than that while still
    /// dragging).</summary>
    private double ComputeDragPreviewRenderScale(bool dragging, int photoWidth, int photoHeight)
    {
        if (!dragging) return 1.0;
        double paneWidth = PreviewHost.ActualWidth * DragPreviewOversample;
        double paneHeight = PreviewHost.ActualHeight * DragPreviewOversample;
        if (paneWidth <= 0 || paneHeight <= 0 || photoWidth <= 0 || photoHeight <= 0) return 1.0;

        double fitScale = Math.Min(paneWidth / photoWidth, paneHeight / photoHeight);
        return Math.Clamp(fitScale, MinDragPreviewScale, 1.0);
    }

    /// <summary>Bumped at the top of every RenderCompositePreview call and
    /// re-checked after each await -- a slider drag can call
    /// RenderCompositePreview again before an in-flight background render
    /// finishes, and without this guard the SLOWER (now stale) render could
    /// finish after the newer one and stomp its result back onto the
    /// preview/_lastComposite. Same pattern as RefreshRecentPhotosUI's own
    /// _recentPhotosScanToken.</summary>
    private int _compositeRenderToken;

    /// <summary>See RenderCompositePreview's own comment right before where
    /// this is used -- caches RenderOverlayForComposite's (WPF-visual-tree-
    /// dependent, UI-thread-only) output so a render triggered by a slider
    /// that has nothing to do with the overlay's own look/placement/
    /// rotation can skip redoing it.</summary>
    private readonly record struct CachedOverlayRender(
        BitmapSource? Source, double Width, double Height, double Rotation,
        byte[] Pixels, int Stride, int PixelsWidth, int PixelsHeight, double OffsetX, double OffsetY);

    private CachedOverlayRender? _cachedOverlayRender;

    /// <summary>Recomputes and displays the composite: photo (with its own
    /// independent look applied) plus the aligned PNG (with the shared Align-
    /// mode look, resized/rotated to match) on top, at the current placement
    /// (see <see cref="InitializeCompositePlacementIfNeeded"/> and the preview
    /// drag/wheel handlers). Falls back to showing whichever one IS loaded
    /// alone when only the photo or only the avatar image has been loaded.
    /// While a color/placement drag is in progress (see
    /// <see cref="_isCompositeDragging"/>), skips updating the saved-quality
    /// result (<see cref="_lastComposite"/>) until the drag ends, so a mid-
    /// drag preview frame never gets treated as the thing "保存" would
    /// actually save.
    ///
    /// The actual pixel crunching (CompositeOverlayOntoPhoto + CropToAspect --
    /// everything except RenderOverlayForComposite's WPF-visual-tree render,
    /// which has to stay on the UI thread) runs inside Task.Run: a full-
    /// resolution recomposite of a multi-megapixel VRChat screenshot used to
    /// block the UI thread directly on every throttled tick of a slider
    /// drag. Callers that don't need to wait for the result (ScheduleCompositeRender's
    /// own two call sites) can call this without awaiting; callers that do
    /// (FinishMatchRender, so it doesn't hide the loading spinner early)
    /// await the returned Task.
    ///
    /// _compositeRenderGate serializes the Task.Run bodies specifically
    /// (not the whole method -- RenderOverlayForComposite is unrelated WPF
    /// rendering, not this): the GPU pipeline's caches (GpuTexturePool's
    /// Slots dictionary, GpuFilmGrain's static noise buffer) and ComputeSharp's
    /// own GraphicsDevice command submission were built assuming exactly
    /// one composite render runs at a time (true back when everything was
    /// synchronous on the UI thread), and were never made thread-safe for
    /// two Task.Run bodies actually executing concurrently on different
    /// pool threads -- something a slow-enough render (or a CPU fallback)
    /// could trigger even with the throttle above. Re-checking the token
    /// right after acquiring the gate means a render that went stale while
    /// waiting its turn skips the now-pointless work instead of computing a
    /// result just to discard it.</summary>
    private readonly SemaphoreSlim _compositeRenderGate = new(1, 1);

    private async Task RenderCompositePreview()
    {
        int token = ++_compositeRenderToken;
        var overlaySource = _compositeSkipAvatar ? null : _overlayWindow.AdjustedPngSource;

        if (_photoPixelBuffer is not { } photoBuffer)
        {
            // No photo yet -- show the PNG alone rather than leaving the
            // preview blank, if one's already loaded.
            PreviewImage.Source = overlaySource;
            _lastComposite = null;
            _lastBeforeComposite = null;
            SaveCompositeButton.IsEnabled = false;
            MatchAvatarToPhotoButton.IsEnabled = false;
            MatchPhotoToAvatarButton.IsEnabled = false;
            SizePreviewToImage();
            return;
        }

        bool dragging = _isCompositeDragging;
        // Also skips the final crop while アバター配置モード is on, not just
        // during 切り抜きモード itself: otherwise positioning/resizing the
        // avatar near or past the crop's own edge would show it clipped away
        // by a crop that hasn't even been committed to yet -- see
        // GetDisplayedCropRect, which every avatar coordinate conversion
        // must agree with this flag on.
        bool cropAdjusting = _isCropModeActive || _isAvatarPlacementModeActive;

        if (overlaySource is null)
        {
            // No avatar loaded -- run the photo through the same finishing-
            // effects pipeline CompositeOverlayOntoPhoto applies when an
            // avatar IS present, just with the blend step skipped (null
            // overlay), so grain/vignette/glow/light leak/tone gradient/etc.
            // all still affect what actually gets saved instead of silently
            // doing nothing.
            double photoOnlyScale = ComputeDragPreviewRenderScale(dragging, photoBuffer.Width, photoBuffer.Height);
            var renderPhotoBuffer = photoOnlyScale < 1.0
                ? GetDownscaledPhoto(photoBuffer, (int)Math.Round(photoBuffer.Width * photoOnlyScale), (int)Math.Round(photoBuffer.Height * photoOnlyScale))
                : photoBuffer;
            var photoAdjustments = PhotoAdjustments;
            var snap = CaptureCompositeSnapshot();

            await _compositeRenderGate.WaitAsync();
            WriteableBitmap after;
            try
            {
                if (token != _compositeRenderToken) return; // superseded while waiting for the gate
                after = await Task.Run(() =>
                {
                    var result = ImageAdjustment.CompositeOverlayOntoPhoto(
                        renderPhotoBuffer, photoAdjustments,
                        grainAmount: snap.GrainAmount, vignetteAmount: snap.VignetteAmount,
                        photoBlurAmount: snap.PhotoBlurAmount, photoBlurScale: photoOnlyScale,
                        softnessAmount: snap.SoftnessAmount, sharpnessAmount: snap.SharpnessAmount, finishDetailScale: photoOnlyScale,
                        fadeAmount: snap.FadeAmount, glowAmount: snap.GlowAmount, glowScale: photoOnlyScale,
                        chromaticAberrationAmount: snap.ChromaticAberrationAmount, colorBleedAmount: snap.ColorBleedAmount,
                        scanlineAmount: snap.ScanlineAmount, vhsScale: photoOnlyScale,
                        clarityAmount: snap.ClarityAmount, clarityScale: photoOnlyScale,
                        lightLeakAmount: snap.LightLeakAmount, lightLeakAngle: snap.LightLeakAngle, lightLeakDistance: snap.LightLeakDistance,
                        lightLeakColorB: snap.LightLeakColorB, lightLeakColorG: snap.LightLeakColorG, lightLeakColorR: snap.LightLeakColorR,
                        toneGradientAmount: snap.ToneGradientAmount, toneGradientRotation: snap.ToneGradientRotation,
                        toneGradientLightR: snap.ToneGradientLightR, toneGradientLightG: snap.ToneGradientLightG, toneGradientLightB: snap.ToneGradientLightB,
                        toneGradientDarkR: snap.ToneGradientDarkR, toneGradientDarkG: snap.ToneGradientDarkG, toneGradientDarkB: snap.ToneGradientDarkB);
                    return cropAdjusting ? result : ImageAdjustment.CropToAspect(result, snap.CanvasAspectRatio, snap.CanvasCropOffsetX, snap.CanvasCropOffsetY, snap.CanvasCropWidthPercent, snap.CanvasCropHeightPercent);
                });
            }
            finally
            {
                _compositeRenderGate.Release();
            }

            if (token != _compositeRenderToken) return; // a newer render started meanwhile; this result is stale

            // ComputeBeforeComposite (not an inline recompute here) --
            // it's cached, and unlike this branch's own "after" it doesn't
            // depend on any of the finishing-effect sliders that triggered
            // this render in the first place.
            WriteableBitmap? before = _beforeAfterSplit > 0 ? ComputeBeforeComposite() : null;
            if (!dragging)
            {
                _lastComposite = after;
                _lastBeforeComposite = before;
            }
            UpdateComparisonPreview(after, before);
            SaveCompositeButton.IsEnabled = true;
            MatchAvatarToPhotoButton.IsEnabled = false;
            MatchPhotoToAvatarButton.IsEnabled = false;
            SizePreviewToImage();
            UpdateCanvasCropBoundary();
            return;
        }

        InitializeCompositePlacementIfNeeded(photoBuffer, overlaySource);

        // Full resolution unless a drag is actively in progress -- see
        // ComputeDragPreviewRenderScale's own doc comment. _compositePlaceX/
        // Y/Width/Height above stay in FULL-RES photo-pixel coordinates
        // regardless (that's the canonical space placement/undo/save all
        // reason about); only the values used for the actual render below
        // get scaled down, in lockstep with renderPhotoBuffer.
        double previewScale = ComputeDragPreviewRenderScale(dragging, photoBuffer.Width, photoBuffer.Height);
        var scaledPhotoBuffer = previewScale < 1.0
            ? GetDownscaledPhoto(photoBuffer, (int)Math.Round(photoBuffer.Width * previewScale), (int)Math.Round(photoBuffer.Height * previewScale))
            : photoBuffer;

        double placeLeft = _compositePlaceX * previewScale;
        double placeTop = _compositePlaceY * previewScale;
        double placeWidth = _compositePlaceWidth * previewScale;
        double placeHeight = _compositePlaceHeight * previewScale;

        // Opacity is fixed at 100% for the actual composite, regardless of
        // the Align-mode slider: that slider's whole purpose is seeing
        // through the overlay to line it up with the live (opaque) VRChat
        // background, not something meant to end up baked into the output.
        // Stays on the UI thread (renders an actual WPF visual tree) --
        // everything from here on is plain byte[]/PixelBuffer work and can
        // move to Task.Run below.
        //
        // Cached across renders whose inputs to THIS step didn't change --
        // most of the ~20 composite-only sliders (grain, vignette, photo
        // look, canvas crop, every finishing effect) have nothing to do
        // with the overlay's own placement/rotation/look, but every render
        // used to redo this WPF visual-tree render + pixel extraction
        // anyway, which is exactly why dragging any of those felt far
        // heavier whenever an avatar was loaded (the photo-only branch
        // above never pays this cost at all). overlaySource's reference
        // only changes when OverlayWindow.ApplyImageAdjustments actually
        // reprocesses the avatar's own look (see its own doc comment) --
        // same reference-equality-as-cache-key idea GpuTexturePool.
        // RentUploaded already uses for the photo buffer.
        double overlayLeft, overlayTop;
        byte[] overlayPixels;
        int overlayStride, overlayWidth, overlayHeight;
        if (_cachedOverlayRender is { } cached
            && ReferenceEquals(cached.Source, overlaySource)
            && cached.Width == placeWidth && cached.Height == placeHeight && cached.Rotation == _compositeRotation)
        {
            overlayPixels = cached.Pixels;
            overlayStride = cached.Stride;
            overlayWidth = cached.PixelsWidth;
            overlayHeight = cached.PixelsHeight;
            overlayLeft = placeLeft - cached.OffsetX;
            overlayTop = placeTop - cached.OffsetY;
        }
        else
        {
            var (overlayRendered, offsetX, offsetY) = ImageAdjustment.RenderOverlayForComposite(
                overlaySource, placeWidth, placeHeight, _compositeRotation, opacity: 1.0);
            (overlayPixels, overlayStride, overlayWidth, overlayHeight) = ExtractBgraPixels(overlayRendered);
            overlayLeft = placeLeft - offsetX;
            overlayTop = placeTop - offsetY;
            _cachedOverlayRender = new CachedOverlayRender(
                overlaySource, placeWidth, placeHeight, _compositeRotation,
                overlayPixels, overlayStride, overlayWidth, overlayHeight, offsetX, offsetY);
        }

        var fullPhotoAdjustments = PhotoAdjustments;
        var fullSnap = CaptureCompositeSnapshot();

        // Finishing effects (film grain, vignette) apply exactly once, to the
        // final composite result only -- not per-layer -- so they read as
        // "one photo shot on film", not doubled-up texture from both the
        // avatar and the background separately.
        await _compositeRenderGate.WaitAsync();
        WriteableBitmap afterComposite;
        try
        {
            if (token != _compositeRenderToken) return; // superseded while waiting for the gate
            afterComposite = await Task.Run(() =>
            {
                var result = ImageAdjustment.CompositeOverlayOntoPhoto(
                    scaledPhotoBuffer, fullPhotoAdjustments,
                    overlayPixels, overlayStride, overlayWidth, overlayHeight, overlayLeft, overlayTop,
                    fullSnap.GrainAmount, fullSnap.VignetteAmount, fullSnap.PhotoBlurAmount, previewScale,
                    fullSnap.SoftnessAmount, fullSnap.SharpnessAmount, previewScale,
                    fullSnap.FadeAmount, fullSnap.GlowAmount, previewScale,
                    fullSnap.ChromaticAberrationAmount, fullSnap.ColorBleedAmount, fullSnap.ScanlineAmount, previewScale,
                    fullSnap.ClarityAmount, previewScale, fullSnap.LightLeakAmount, fullSnap.LightLeakAngle, fullSnap.LightLeakDistance,
                    fullSnap.LightLeakColorB, fullSnap.LightLeakColorG, fullSnap.LightLeakColorR,
                    fullSnap.ToneGradientAmount, fullSnap.ToneGradientRotation,
                    fullSnap.ToneGradientLightR, fullSnap.ToneGradientLightG, fullSnap.ToneGradientLightB,
                    fullSnap.ToneGradientDarkR, fullSnap.ToneGradientDarkG, fullSnap.ToneGradientDarkB,
                    fullSnap.DropShadowAmount, fullSnap.DropShadowDirection, fullSnap.DropShadowDistance, fullSnap.DropShadowBlur,
                    fullSnap.DropShadowColorB, fullSnap.DropShadowColorG, fullSnap.DropShadowColorR, previewScale,
                    // トーン風(ハーフトーン)UIは削除済み: 常時オフのプレーンな影のみ。
                    false, 8, fullSnap.DropShadowBlendMode);
                return cropAdjusting ? result : ImageAdjustment.CropToAspect(result, fullSnap.CanvasAspectRatio, fullSnap.CanvasCropOffsetX, fullSnap.CanvasCropOffsetY, fullSnap.CanvasCropWidthPercent, fullSnap.CanvasCropHeightPercent);
            });
        }
        finally
        {
            _compositeRenderGate.Release();
        }

        if (token != _compositeRenderToken) return; // a newer render started meanwhile; this result is stale

        // "Before" is deliberately NOT recomputed here on every render --
        // doubling the compositing work (a second full RenderOverlayForComposite
        // + CompositeOverlayOntoPhoto pass) on every single render, including
        // ones that have nothing to do with the comparison slider, was a real,
        // measurable source of lag opening Composite mode. Only build it when
        // CompareSlider is actually in use (see ComputeBeforeComposite and its
        // other call site in CompareSlider_ValueChanged, which lazily builds
        // it the first time the slider moves off 0).
        WriteableBitmap? beforeComposite = _beforeAfterSplit > 0 ? ComputeBeforeComposite() : null;

        UpdateComparisonPreview(afterComposite, beforeComposite);
        if (!dragging)
        {
            _lastComposite = afterComposite;
            _lastBeforeComposite = beforeComposite;
        }
        SaveCompositeButton.IsEnabled = true;
        MatchAvatarToPhotoButton.IsEnabled = true;
        MatchPhotoToAvatarButton.IsEnabled = true;
        SizePreviewToImage();
        UpdateCanvasCropBoundary();
    }

    /// <summary>Builds the "before" comparison composite (see
    /// _lastBeforeComposite): the current placement/rotation, but with none
    /// of the look adjustments or finishing effects applied to either layer.
    /// Self-contained (re-derives placement/scale from the current fields
    /// rather than reusing RenderCompositePreview's locals) since it's also
    /// called independently from CompareSlider_ValueChanged, not just from
    /// there. Stays synchronous (unlike RenderCompositePreview's own "after"
    /// computation) -- it's the secondary, less-frequent half of a render
    /// (only needed while the compare slider is actually in use), so it
    /// isn't the thing that was blocking the UI thread on every drag tick.
    ///
    /// ComputeBeforeComposite's result depends only on the photo,
    /// the avatar's PRISTINE pixels (never its look-adjusted ones -- "before"
    /// means before any look adjustment), placement/rotation, and canvas
    /// crop -- never on any color/finishing-effect slider. But RenderCompositePreview
    /// calls ComputeBeforeComposite fresh on every single render whenever the
    /// compare slider is active (see its own call site), which used to mean
    /// a full second RenderOverlayForComposite + CompositeOverlayOntoPhoto
    /// pass on every grain/vignette/photo-color/etc tick too, even though
    /// none of those affect this result at all. Self-invalidating: recomputes
    /// whenever any field in the key actually differs from last time
    /// (PixelBuffer records compare their byte[] Pixels by REFERENCE, not
    /// content, matching the rest of this codebase's "replaced wholesale,
    /// never mutated in place" convention), so there's no separate
    /// invalidation call needed anywhere else.</summary>
    private readonly record struct CachedBeforeCompositeKey(
        ImageAdjustment.PixelBuffer? Photo, ImageAdjustment.PixelBuffer? Overlay, bool SkipAvatar,
        double PlaceLeft, double PlaceTop, double PlaceWidth, double PlaceHeight, double Rotation,
        double? CanvasAspectRatio, double CanvasCropOffsetX, double CanvasCropOffsetY);

    private CachedBeforeCompositeKey? _cachedBeforeCompositeKey;
    private WriteableBitmap? _cachedBeforeCompositeResult;

    private WriteableBitmap? ComputeBeforeComposite()
    {
        if (_photoPixelBuffer is not { } photoBuffer) return null;

        var key = new CachedBeforeCompositeKey(
            photoBuffer, _compositeSkipAvatar ? null : _overlayWindow.OriginalPixelBuffer, _compositeSkipAvatar,
            _compositePlaceX, _compositePlaceY, _compositePlaceWidth, _compositePlaceHeight, _compositeRotation,
            _canvasAspectRatio, _canvasCropOffsetX, _canvasCropOffsetY);
        if (_cachedBeforeCompositeKey == key && _cachedBeforeCompositeResult is not null)
        {
            return _cachedBeforeCompositeResult;
        }

        // Blocks briefly (synchronous Wait, not WaitAsync -- this method
        // isn't async) if RenderCompositePreview's own Task.Run is
        // currently mid-flight on a background thread -- see
        // _compositeRenderGate's own doc comment on why the GPU pipeline
        // can't safely run from two threads at once. A short block here is
        // an acceptable trade: this path is cached and comparatively rare
        // (only when the compare slider is actually in use), unlike the
        // per-tick "after" computation the gate primarily protects against.
        _compositeRenderGate.Wait();
        WriteableBitmap result;
        try
        {
            if (_compositeSkipAvatar || _overlayWindow.RawPngSource is not { } rawOverlaySource)
            {
                // No avatar loaded (or explicitly skipped) -- "before" is just
                // the untouched photo.
                result = ApplyCanvasCrop(ImageAdjustment.CompositeOverlayOntoPhoto(photoBuffer, default));
            }
            else
            {
                double placeLeft = _compositePlaceX;
                double placeTop = _compositePlaceY;
                double placeWidth = _compositePlaceWidth;
                double placeHeight = _compositePlaceHeight;

                var (rawOverlayRendered, rawOffsetX, rawOffsetY) = ImageAdjustment.RenderOverlayForComposite(
                    rawOverlaySource, placeWidth, placeHeight, _compositeRotation, opacity: 1.0);
                var (rawOverlayPixels, rawOverlayStride, rawOverlayWidth, rawOverlayHeight) = ExtractBgraPixels(rawOverlayRendered);
                result = ApplyCanvasCrop(ImageAdjustment.CompositeOverlayOntoPhoto(
                    photoBuffer, default,
                    rawOverlayPixels, rawOverlayStride, rawOverlayWidth, rawOverlayHeight,
                    placeLeft - rawOffsetX, placeTop - rawOffsetY));
            }
        }
        finally
        {
            _compositeRenderGate.Release();
        }

        _cachedBeforeCompositeKey = key;
        _cachedBeforeCompositeResult = result;
        return result;
    }

    // ---- Shift+drag (or, persistently, アバター配置モード) directly on the
    //      preview moves the avatar (_compositePlaceX/Y -- there's no
    //      longer a slider UI for these at all, see
    //      AvatarPlacementModeToggle_Changed's own removal comment), with a
    //      highlighted rect + resize/rotate handles tracing its current
    //      placement while either is active -- see
    //      PreviewImage_MouseLeftButtonDown/Window_PreviewKeyDown below. ----

    /// <summary>Persistent alternative to holding Shift: while on, the
    /// avatar's bounding box + corner handles + rotate gizmo stay up and
    /// dragging anywhere on the preview moves the avatar without needing
    /// Shift at all. Mutually exclusive with _isCropModeActive -- see
    /// AvatarPlacementModeToggle_Changed/CropModeToggle_Changed, both of
    /// which turn the other off.</summary>
    private bool _isAvatarPlacementModeActive;

    private bool _isDraggingAvatarPlacement;
    private System.Windows.Point _avatarDragStartMouse;
    private double _avatarDragStartPlaceX, _avatarDragStartPlaceY;

    /// <summary>The current canvas-crop rectangle in ORIGINAL (pre-crop)
    /// photo pixels -- mirrors ImageAdjustment.CropToAspect's own math
    /// exactly. Needed because PreviewImage.Source is the POST-crop
    /// composite bitmap (see SizePreviewToImage, which sizes PreviewBorder
    /// to THAT bitmap's own pixel size) while _compositePlaceX/Y/Width/
    /// Height are defined in PRE-crop photo-pixel space -- converting
    /// between screen position and placement coordinates has to account for
    /// the crop offset, or the highlight/drag would drift from the actual
    /// avatar the moment a canvas aspect ratio is active. _canvasAspectRatio
    /// null means 自由 (free): still an active crop, just with
    /// _canvasCropWidthPercent/_canvasCropHeightPercent shrinking each axis
    /// independently against the full photo instead of both deriving from
    /// one ratio-fit box (see GetMaxCropSize).</summary>
    private (double Left, double Top, double Width, double Height) GetCanvasCropRect(int photoWidth, int photoHeight)
    {
        if (photoWidth <= 0 || photoHeight <= 0) return (0, 0, photoWidth, photoHeight);

        var (maxCropWidth, maxCropHeight) = GetMaxCropSize(photoWidth, photoHeight);
        double widthZoom = Math.Clamp(_canvasCropWidthPercent, 1, 100) / 100.0;
        double heightZoom = _canvasAspectRatio is null ? Math.Clamp(_canvasCropHeightPercent, 1, 100) / 100.0 : widthZoom;
        double cropWidth = Math.Max(1, Math.Round(maxCropWidth * widthZoom));
        double cropHeight = Math.Max(1, Math.Round(maxCropHeight * heightZoom));
        double maxLeft = photoWidth - cropWidth;
        double maxTop = photoHeight - cropHeight;
        double left = Math.Round(maxLeft * Math.Clamp(_canvasCropOffsetX, 0, 100) / 100.0);
        double top = Math.Round(maxTop * Math.Clamp(_canvasCropOffsetY, 0, 100) / 100.0);
        return (left, top, cropWidth, cropHeight);
    }

    /// <summary>What every avatar-placement screen&lt;-&gt;photo coordinate
    /// conversion should treat "the area PreviewBorder currently displays"
    /// as: the real canvas crop normally, but the FULL uncropped photo while
    /// either 切り抜きモード or アバター配置モード is active. Must always
    /// agree with RenderCompositePreview's own `cropAdjusting` local (see its
    /// definition), which renders the full uncropped composite during either
    /// of those exact same two modes -- otherwise the avatar highlight/
    /// handles/gizmo would be positioned against a crop rect PreviewBorder
    /// isn't actually showing, clipping them out of view the moment the
    /// avatar is placed outside the (still-uncommitted) crop bounds. Unlike
    /// GetCanvasCropRect's other few callers (UpdateCanvasCropBoundary, the
    /// crop-handle drag handlers), which draw/adjust the crop boundary
    /// itself and so need the TRUE rect regardless of which mode is
    /// active.</summary>
    private (double Left, double Top, double Width, double Height) GetDisplayedCropRect(int photoWidth, int photoHeight) =>
        _isCropModeActive || _isAvatarPlacementModeActive ? (0, 0, photoWidth, photoHeight) : GetCanvasCropRect(photoWidth, photoHeight);

    /// <summary>The largest box of _canvasAspectRatio's ratio that fits
    /// inside the photo (100% zoom) -- factored out of GetCanvasCropRect so
    /// the interactive corner-handle drag (CanvasCropHandle_MouseMove) can
    /// reuse the exact same ratio-fitting math instead of re-deriving it.
    /// Returns the full photo size in 自由 (free) mode (_canvasAspectRatio
    /// null) -- the 100% baseline each axis's own independent zoom knob
    /// then shrinks from.</summary>
    private (double MaxWidth, double MaxHeight) GetMaxCropSize(int photoWidth, int photoHeight)
    {
        if (_canvasAspectRatio is not { } ratio || ratio <= 0 || photoWidth <= 0 || photoHeight <= 0)
        {
            return (photoWidth, photoHeight);
        }
        double srcRatio = (double)photoWidth / photoHeight;
        double maxCropWidth, maxCropHeight;
        if (ratio > srcRatio)
        {
            maxCropWidth = photoWidth;
            maxCropHeight = Math.Max(1, Math.Round(photoWidth / ratio));
        }
        else
        {
            maxCropHeight = photoHeight;
            maxCropWidth = Math.Max(1, Math.Round(photoHeight * ratio));
        }
        maxCropWidth = Math.Min(maxCropWidth, photoWidth);
        maxCropHeight = Math.Min(maxCropHeight, photoHeight);
        return (maxCropWidth, maxCropHeight);
    }

    private const double AvatarHandleSize = 10;
    private const double AvatarRotateGizmoOffset = 24;
    private const double AvatarRotateGizmoSize = 16;

    /// <summary>Shows/hides and positions AvatarPlacementHighlight (and, only
    /// while アバター配置モード is on -- a quick Shift-drag is for
    /// repositioning, not fiddly resize/rotate work -- the corner handles +
    /// rotate gizmo too) -- visible while Shift is held OR
    /// _isAvatarPlacementModeActive, Composite mode is actually the open
    /// panel, and an avatar is loaded (nothing to highlight otherwise).
    /// Called from Window_PreviewKeyDown/Up (so it reacts the instant Shift
    /// is pressed/released, not just on the next mouse move),
    /// AvatarPlacementModeToggle_Changed, RefreshCompositePlacementUI (so it
    /// stays in sync when placement changes while either is active), and
    /// PreviewImage_MouseMove/AvatarHandle_MouseMove/AvatarRotateGizmo_MouseMove
    /// during an active drag.</summary>
    private void UpdateAvatarPlacementHighlight()
    {
        bool shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool hasAvatar = !_compositeSkipAvatar && _overlayWindow.AdjustedPngSource is not null;
        if ((!shiftHeld && !_isAvatarPlacementModeActive) || !hasAvatar || CompositePanel.Visibility != Visibility.Visible
            || _photoPixelBuffer is not { } photo || double.IsNaN(PreviewBorder.Width) || PreviewBorder.Width <= 0)
        {
            AvatarPlacementHighlight.Visibility = Visibility.Collapsed;
            AvatarHandlesLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;

        double width = _compositePlaceWidth * scale;
        double height = _compositePlaceHeight * scale;
        double marginX = (_compositePlaceX - crop.Left) * scale;
        double marginY = (_compositePlaceY - crop.Top) * scale;
        AvatarPlacementHighlight.Width = width;
        AvatarPlacementHighlight.Height = height;
        AvatarPlacementHighlight.Margin = new Thickness(marginX, marginY, 0, 0);
        AvatarPlacementHighlightRotate.CenterX = width / 2;
        AvatarPlacementHighlightRotate.CenterY = height / 2;
        AvatarPlacementHighlightRotate.Angle = _compositeRotation;
        AvatarPlacementHighlight.Visibility = Visibility.Visible;

        if (!_isAvatarPlacementModeActive)
        {
            AvatarHandlesLayer.Visibility = Visibility.Collapsed;
            return;
        }

        // AvatarHandlesLayer's own PARENT is a plain Grid, not a Canvas, so
        // Canvas.SetLeft/Top on the layer itself would be silently ignored
        // by layout (that attached property only does anything when the
        // immediate parent is a Canvas) -- Margin is what actually moves it
        // within a Grid cell, matching AvatarPlacementHighlight's own
        // Margin-based positioning above. The handles' OWN positions within
        // this layer (PlaceAvatarHandle below) are correct as Canvas.Left/
        // Top, since this layer itself IS a Canvas for its children.
        AvatarHandlesLayer.Margin = new Thickness(marginX, marginY, 0, 0);
        AvatarHandlesLayer.Width = width;
        AvatarHandlesLayer.Height = height;
        AvatarHandlesRotateTransform.Angle = _compositeRotation;

        double half = AvatarHandleSize / 2;
        PlaceAvatarHandle(AvatarHandleTL, -half, -half);
        PlaceAvatarHandle(AvatarHandleTR, width - half, -half);
        PlaceAvatarHandle(AvatarHandleBL, -half, height - half);
        PlaceAvatarHandle(AvatarHandleBR, width - half, height - half);

        double gizmoHalf = AvatarRotateGizmoSize / 2;
        double gizmoCenterY = -AvatarRotateGizmoOffset;
        AvatarRotateGizmoLine.X1 = width / 2;
        AvatarRotateGizmoLine.Y1 = 0;
        AvatarRotateGizmoLine.X2 = width / 2;
        AvatarRotateGizmoLine.Y2 = gizmoCenterY + gizmoHalf;
        PlaceAvatarHandle(AvatarRotateGizmoHandle, width / 2 - gizmoHalf, gizmoCenterY - gizmoHalf);

        AvatarHandlesLayer.Visibility = Visibility.Visible;
    }

    private static void PlaceAvatarHandle(UIElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    // ---- アバター配置モード: 4 corner resize handles (aspect ratio locked,
    //      rotation-aware) + a rotate gizmo, mirroring OverlayWindow's own
    //      Align-mode handle system (Handle_MouseMove/RotateGizmo_MouseMove)
    //      but working in PreviewBorder-scaled, crop-aware coordinates
    //      instead of 1:1 screen pixels, since Composite mode's preview is a
    //      scaled-down view of the photo rather than a true full-screen
    //      overlay. Reuses CropHandleCorner (TopLeft/TopRight/BottomLeft/
    //      BottomRight) -- same 4 corners, no need for a second enum. ----

    private bool _isDraggingAvatarHandle;
    private CropHandleCorner _avatarDragHandle;
    private Point _avatarHandleDragStartMouse;
    private double _avatarHandleStartX, _avatarHandleStartY, _avatarHandleStartWidth, _avatarHandleStartHeight;

    private void AvatarHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_photoPixelBuffer is null) return;
        var element = (FrameworkElement)sender;
        _avatarDragHandle = Enum.Parse<CropHandleCorner>((string)element.Tag);
        _isDraggingAvatarHandle = true;
        _avatarHandleDragStartMouse = e.GetPosition(PreviewBorder);
        _avatarHandleStartX = _compositePlaceX;
        _avatarHandleStartY = _compositePlaceY;
        _avatarHandleStartWidth = _compositePlaceWidth;
        _avatarHandleStartHeight = _compositePlaceHeight;
        _undo.BeginChange();
        element.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Locked-aspect corner resize, rotation-aware: the screen-space
    /// drag delta is un-rotated into the avatar's own local axes first (same
    /// technique as OverlayWindow's Handle_MouseMove), then projected onto
    /// the dragged corner's own diagonal for a single continuous scale
    /// factor -- avoids the width-vs-height flip-flop a naive per-axis
    /// comparison has near the diagonal, the natural drag direction for a
    /// corner.</summary>
    private void AvatarHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAvatarHandle || _photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width * _previewZoom;
        if (scale <= 0) return;

        var current = e.GetPosition(PreviewBorder);
        double screenDx = (current.X - _avatarHandleDragStartMouse.X) / scale;
        double screenDy = (current.Y - _avatarHandleDragStartMouse.Y) / scale;

        double rad = -_compositeRotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double localDx = screenDx * cos - screenDy * sin;
        double localDy = screenDx * sin + screenDy * cos;

        bool left = _avatarDragHandle is CropHandleCorner.TopLeft or CropHandleCorner.BottomLeft;
        bool top = _avatarDragHandle is CropHandleCorner.TopLeft or CropHandleCorner.TopRight;

        double halfW0 = _avatarHandleStartWidth / 2;
        double halfH0 = _avatarHandleStartHeight / 2;
        double cornerDist0 = Math.Sqrt(halfW0 * halfW0 + halfH0 * halfH0);
        if (cornerDist0 <= 0) return;

        double dirX = (left ? -halfW0 : halfW0) / cornerDist0;
        double dirY = (top ? -halfH0 : halfH0) / cornerDist0;
        double projected = localDx * dirX + localDy * dirY;

        double dragScale = (cornerDist0 + projected) / cornerDist0;
        if (dragScale <= 0) return; // dragged past center; ignore rather than invert

        // Lock to the loaded PNG's own native aspect ratio when available --
        // more robust than the box's current W/H, which could have drifted
        // from the image's true ratio (rounding, or an earlier manual edit).
        double aspect = _overlayWindow.ImageNativeSize is { Width: > 0, Height: > 0 } native
            ? native.Width / native.Height
            : _avatarHandleStartWidth / _avatarHandleStartHeight;

        double newWidth = _avatarHandleStartWidth * dragScale;
        double newHeight = newWidth / aspect;
        if (newWidth < 20 || newHeight < 20) return;

        double centerX = _avatarHandleStartX + _avatarHandleStartWidth / 2;
        double centerY = _avatarHandleStartY + _avatarHandleStartHeight / 2;
        _compositePlaceWidth = newWidth;
        _compositePlaceHeight = newHeight;
        _compositePlaceX = centerX - newWidth / 2;
        _compositePlaceY = centerY - newHeight / 2;

        UpdateAvatarPlacementHighlight();
        ScheduleCompositeRender();
    }

    private void AvatarHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingAvatarHandle = false;
        _undo.CommitChange();
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool _isDraggingAvatarRotateGizmo;
    private double _avatarRotateGizmoStartAngle;
    private double _avatarRotateGizmoStartRotation;

    private void AvatarRotateGizmo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_photoPixelBuffer is not { } photo) return;
        _undo.BeginChange();
        _isDraggingAvatarRotateGizmo = true;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;
        double centerX = (_compositePlaceX + _compositePlaceWidth / 2 - crop.Left) * scale;
        double centerY = (_compositePlaceY + _compositePlaceHeight / 2 - crop.Top) * scale;
        var mouse = e.GetPosition(PreviewBorder);
        _avatarRotateGizmoStartAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        _avatarRotateGizmoStartRotation = _compositeRotation;
        AvatarRotateGizmoHandle.CaptureMouse();
        e.Handled = true;
    }

    private void AvatarRotateGizmo_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAvatarRotateGizmo || _photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;
        double centerX = (_compositePlaceX + _compositePlaceWidth / 2 - crop.Left) * scale;
        double centerY = (_compositePlaceY + _compositePlaceHeight / 2 - crop.Top) * scale;
        var mouse = e.GetPosition(PreviewBorder);
        double currentAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        double newRotation = _avatarRotateGizmoStartRotation + (currentAngle - _avatarRotateGizmoStartAngle);
        _compositeRotation = SoftSnap(newRotation, 5, -180, -90, 0, 90, 180);

        UpdateAvatarPlacementHighlight();
        ScheduleCompositeRender();
    }

    private void AvatarRotateGizmo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingAvatarRotateGizmo = false;
        _undo.CommitChange();
        AvatarRotateGizmoHandle.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Shows/hides and positions the crop-boundary dim+outline+
    /// corner-handle overlay -- visible while <see cref="_isCropModeActive"/>
    /// (the 切り抜きモード toggle) is on. PreviewImage.Source itself
    /// switches to the UNCROPPED composite for the same duration
    /// (RenderCompositePreview skips ApplyCanvasCrop while it's true), so
    /// PreviewBorder.Width maps to the FULL photo's width here, not
    /// crop-space -- the opposite of GetCanvasCropRect's other callers
    /// (UpdateAvatarPlacementHighlight, the Shift-drag handler), which is
    /// why the scale below reads photo.Width, not crop.Width.</summary>
    private void UpdateCanvasCropBoundary()
    {
        if (!_isCropModeActive || _photoPixelBuffer is not { } photo
            || double.IsNaN(PreviewBorder.Width) || PreviewBorder.Width <= 0)
        {
            CanvasCropDimOverlay.Visibility = Visibility.Collapsed;
            CanvasCropBoundaryOutline.Visibility = Visibility.Collapsed;
            CanvasCropHandleTopLeft.Visibility = Visibility.Collapsed;
            CanvasCropHandleTopRight.Visibility = Visibility.Collapsed;
            CanvasCropHandleBottomLeft.Visibility = Visibility.Collapsed;
            CanvasCropHandleBottomRight.Visibility = Visibility.Collapsed;
            return;
        }

        var crop = GetCanvasCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / photo.Width;
        double left = crop.Left * scale, top = crop.Top * scale;
        double width = crop.Width * scale, height = crop.Height * scale;
        double fullWidth = photo.Width * scale, fullHeight = photo.Height * scale;

        var outer = new RectangleGeometry(new Rect(0, 0, fullWidth, fullHeight));
        var inner = new RectangleGeometry(new Rect(left, top, width, height));
        CanvasCropDimOverlay.Data = new CombinedGeometry(GeometryCombineMode.Xor, outer, inner);
        CanvasCropDimOverlay.Visibility = Visibility.Visible;

        CanvasCropBoundaryOutline.Width = width;
        CanvasCropBoundaryOutline.Height = height;
        CanvasCropBoundaryOutline.Margin = new Thickness(left, top, 0, 0);
        CanvasCropBoundaryOutline.Visibility = Visibility.Visible;

        // Corner handles only make sense (and only need to be draggable) in
        // the persistent 切り抜きモード, not while just dragging a slider --
        // but leaving them collapsed there is a visibility-only difference,
        // so it's simplest to gate all four on the same flag here rather
        // than threading a second condition through every call site.
        double handleSize = CanvasCropHandleTopLeft.Width;
        var handleVisibility = _isCropModeActive ? Visibility.Visible : Visibility.Collapsed;
        CanvasCropHandleTopLeft.Margin = new Thickness(left - handleSize / 2, top - handleSize / 2, 0, 0);
        CanvasCropHandleTopLeft.Visibility = handleVisibility;
        CanvasCropHandleTopRight.Margin = new Thickness(left + width - handleSize / 2, top - handleSize / 2, 0, 0);
        CanvasCropHandleTopRight.Visibility = handleVisibility;
        CanvasCropHandleBottomLeft.Margin = new Thickness(left - handleSize / 2, top + height - handleSize / 2, 0, 0);
        CanvasCropHandleBottomLeft.Visibility = handleVisibility;
        CanvasCropHandleBottomRight.Margin = new Thickness(left + width - handleSize / 2, top + height - handleSize / 2, 0, 0);
        CanvasCropHandleBottomRight.Visibility = handleVisibility;
    }

    /// <summary>Window-wide, not scoped to the preview: Shift's own state
    /// needs to be known instantly on press/release regardless of where the
    /// cursor happens to be (moving the mouse onto the preview is often
    /// what happens AFTER pressing Shift, not before). PreviewKeyDown/Up
    /// (not KeyDown/Up) so this still fires even if a TextBox or other
    /// child control currently has focus.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => UpdateAvatarPlacementHighlight();

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => UpdateAvatarPlacementHighlight();

    // ---- Preview-only viewport zoom/pan: wheel zooms in/out, drag pans --
    //      purely for inspecting detail. Implemented as a RenderTransform on
    //      PreviewImage itself, so it never touches _compositePlace*/the
    //      actual composite bitmap/what gets saved -- it's exactly like
    //      zooming into a photo viewer, not a way to move or resize the
    //      avatar (see the CompositeX/Y/Width sliders in the "配置" card for
    //      that instead). ----

    private double _previewZoom = 1.0;
    private double _previewPanX, _previewPanY;
    private bool _isPanningPreview;
    private System.Windows.Point _panDragStartMouse;
    private double _panDragStartPanX, _panDragStartPanY;

    private void UpdatePreviewViewportTransform()
    {
        PreviewImageScale.ScaleX = _previewZoom;
        PreviewImageScale.ScaleY = _previewZoom;
        PreviewImageTranslate.X = _previewPanX;
        PreviewImageTranslate.Y = _previewPanY;
    }

    private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        bool zoomingIn = e.Delta > 0;
        double oldZoom = _previewZoom;
        double factor = zoomingIn ? 1.15 : 1.0 / 1.15;
        _previewZoom = Math.Clamp(_previewZoom * factor, 1.0, 8.0);
        if (_previewZoom <= 1.001)
        {
            _previewZoom = 1.0;
            _previewPanX = 0;
            _previewPanY = 0;
        }
        else if (zoomingIn)
        {
            // Keep the pixel under the cursor fixed on screen instead of
            // always zooming around the image's center: with
            // RenderTransformOrigin(0.5,0.5), a local point P maps to screen
            // position O + zoom*(P-O) + Pan (O = the image's own center, in
            // the same untransformed local frame GetPosition(PreviewBorder)
            // reports -- see the comment on PreviewImage_MouseLeftButtonDown
            // for why PreviewBorder, not PreviewImage, is measured against).
            // Solving "mouse's screen position stays the same before and
            // after the zoom changes" for the new Pan gives this update.
            // Zooming OUT deliberately skips this and leaves Pan untouched
            // instead (shrinking around the image's own center via
            // RenderTransformOrigin, the pre-cursor-anchoring behavior), per
            // explicit request to revert only the zoom-out direction.
            var mouse = e.GetPosition(PreviewBorder);
            double originX = PreviewImage.ActualWidth / 2.0;
            double originY = PreviewImage.ActualHeight / 2.0;
            _previewPanX += (oldZoom - _previewZoom) * (mouse.X - originX);
            _previewPanY += (oldZoom - _previewZoom) * (mouse.Y - originY);
        }
        else
        {
            // Zooming out while still above 1x: shrink Pan by the same
            // ratio as the zoom itself, so a view that had drifted out of
            // the visible bounds while zoomed in eases back toward center
            // continuously as you keep scrolling out, rather than staying
            // put (possibly still off-screen) until the hard snap-to-center
            // above finally fires right at 1x.
            _previewPanX *= _previewZoom / oldZoom;
            _previewPanY *= _previewZoom / oldZoom;
        }
        UpdatePreviewViewportTransform();
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_colorPickTarget != ColorPickTarget.None)
        {
            TryPickColorAtClick(e);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _isAvatarPlacementModeActive) && !_compositeSkipAvatar
            && _overlayWindow.AdjustedPngSource is not null && _photoPixelBuffer is not null)
        {
            _isDraggingAvatarPlacement = true;
            _avatarDragStartMouse = e.GetPosition(PreviewBorder);
            _avatarDragStartPlaceX = _compositePlaceX;
            _avatarDragStartPlaceY = _compositePlaceY;
            _isCompositeDragging = true;
            _undo.BeginChange();
            PreviewImage.CaptureMouse();
            return;
        }

        _isPanningPreview = true;
        // Measured relative to PreviewBorder (which has no RenderTransform of
        // its own), not PreviewImage itself -- GetPosition on the SAME
        // element that owns the zoom's RenderTransform divides the result by
        // the current zoom (it reports the point in the element's own pre-
        // scale local space), while the translate below is applied in
        // already-scaled/screen space (Scale before Translate in the
        // TransformGroup). Measuring against PreviewImage made the pan lag
        // further behind the cursor the more zoomed in it was; measuring
        // against an untransformed ancestor keeps it 1:1 with the mouse at
        // any zoom level.
        _panDragStartMouse = e.GetPosition(PreviewBorder);
        _panDragStartPanX = _previewPanX;
        _panDragStartPanY = _previewPanY;
        PreviewImage.CaptureMouse();
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_colorPickTarget != ColorPickTarget.None)
        {
            UpdateColorPickMagnifier(e.GetPosition(PreviewBorder));
            return;
        }

        if (_isDraggingAvatarPlacement)
        {
            if (_photoPixelBuffer is not { } photo) return;
            var crop = GetDisplayedCropRect(photo.Width, photo.Height);
            // *_previewZoom (not /): a raw screen-DIP mouse delta measured
            // against PreviewBorder is already 1:1 with real on-screen
            // movement regardless of zoom (see the MouseDown comment on the
            // pan branch below for why), but at zoom z, that many screen
            // pixels correspond to fewer PLACEMENT pixels the more zoomed in
            // the view is -- z belongs in the denominator alongside the
            // unzoomed display scale so the avatar keeps tracking the
            // cursor's actual on-screen position at any zoom level, not just
            // at 1x.
            double scale = PreviewBorder.Width / crop.Width * _previewZoom;
            var current = e.GetPosition(PreviewBorder);
            _compositePlaceX = _avatarDragStartPlaceX + (current.X - _avatarDragStartMouse.X) / scale;
            _compositePlaceY = _avatarDragStartPlaceY + (current.Y - _avatarDragStartMouse.Y) / scale;
            RefreshCompositePlacementUI();
            ScheduleCompositeRender();
            return;
        }

        if (!_isPanningPreview) return;
        var currentPan = e.GetPosition(PreviewBorder);
        _previewPanX = _panDragStartPanX + (currentPan.X - _panDragStartMouse.X);
        _previewPanY = _panDragStartPanY + (currentPan.Y - _panDragStartMouse.Y);
        UpdatePreviewViewportTransform();
    }

    private void PreviewImage_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_colorPickTarget != ColorPickTarget.None) HideColorPickMagnifier();
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingAvatarPlacement)
        {
            _isDraggingAvatarPlacement = false;
            _isCompositeDragging = false;
            PreviewImage.ReleaseMouseCapture();
            ScheduleCompositeRender();
            _undo.CommitChange();
            return;
        }

        _isPanningPreview = false;
        PreviewImage.ReleaseMouseCapture();
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => SizePreviewToImage();

    /// <summary>Shrinks the preview box itself to match whatever's currently
    /// loaded in it, aspect-locked, instead of a fixed box that letterboxes a
    /// differently-shaped image inside it. Falls back to filling the whole
    /// preview area (for the placeholder status text) when nothing's loaded
    /// yet, or does nothing yet if the host hasn't been laid out (no size to
    /// fit within) -- SizeChanged retries once it has.</summary>
    private void SizePreviewToImage()
    {
        if (PreviewImage.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
        {
            PreviewBorder.Width = double.NaN;
            PreviewBorder.Height = double.NaN;
            PreviewBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewBorder.VerticalAlignment = VerticalAlignment.Stretch;
            CompareSlider.Width = double.NaN;
            UpdateCompareSplitLine();
            return;
        }

        double maxWidth = PreviewHost.ActualWidth;
        double maxHeight = PreviewHost.ActualHeight;
        if (maxWidth <= 0 || maxHeight <= 0) return;

        // *0.96 rather than an exact edge-to-edge fit: a small deliberate
        // margin around the image at max zoom-out so it doesn't touch
        // PreviewHost's own bounds exactly.
        double scale = Math.Min(maxWidth / bmp.PixelWidth, maxHeight / bmp.PixelHeight) * 0.96;
        PreviewBorder.HorizontalAlignment = HorizontalAlignment.Center;
        PreviewBorder.VerticalAlignment = VerticalAlignment.Center;
        PreviewBorder.Width = bmp.PixelWidth * scale;
        PreviewBorder.Height = bmp.PixelHeight * scale;
        // CompareSlider is made WIDER than the image, not equal to it: WPF's
        // Track always insets the round 16px Thumb by half its own width at
        // each end, so a Slider exactly as wide as the image would leave the
        // thumb's center 8px short of the image's actual edges. Padding the
        // slider's own width by that same 16px (it's centered on the same
        // point as the image, see the XAML comment on CompareSlider) means
        // Track's internal inset lands the thumb's center EXACTLY on the
        // image's true left/right edges at Value 0/100, matching the plain
        // Value/100 fraction UpdateCompareSplitLine and the actual merge
        // split both already use, everywhere along the range, not just the
        // middle.
        CompareSlider.Width = PreviewBorder.Width + CompareThumbDiameter;
        UpdateCompareSplitLine();
    }

    private const double CompareThumbDiameter = 16.0;

    // ---- Throttle composite re-rendering while a photo-look slider is being
    //      dragged, same reasoning/pattern as OverlayWindow's PNG-adjustment
    //      throttle: a full photo-sized recomposite (a VRChat screenshot can be
    //      several megapixels) on every single tick would visibly lag. ----

    private static readonly TimeSpan CompositeRenderThrottle = TimeSpan.FromMilliseconds(80);
    private DateTime _lastCompositeRender = DateTime.MinValue;
    private DispatcherTimer? _pendingCompositeRenderTimer;

    private void ScheduleCompositeRender()
    {
        if (CompositePanel.Visibility != Visibility.Visible) return;

        var elapsed = DateTime.UtcNow - _lastCompositeRender;
        if (elapsed >= CompositeRenderThrottle)
        {
            _pendingCompositeRenderTimer?.Stop();
            _lastCompositeRender = DateTime.UtcNow;
            _ = RenderCompositePreview();
            return;
        }

        _pendingCompositeRenderTimer ??= new DispatcherTimer();
        _pendingCompositeRenderTimer.Stop();
        _pendingCompositeRenderTimer.Interval = CompositeRenderThrottle - elapsed;
        _pendingCompositeRenderTimer.Tick -= OnPendingCompositeRenderTick;
        _pendingCompositeRenderTimer.Tick += OnPendingCompositeRenderTick;
        _pendingCompositeRenderTimer.Start();
    }

    private void OnPendingCompositeRenderTick(object? sender, EventArgs e)
    {
        _pendingCompositeRenderTimer!.Stop();
        _lastCompositeRender = DateTime.UtcNow;
        _ = RenderCompositePreview();
    }

    private void RefreshPhotoLookUI()
    {
        _suppressEvents = true;
        PhotoBrightnessBox.Text = _photoBrightness.ToString("F0", CultureInfo.InvariantCulture);
        PhotoBrightnessSlider.Value = _photoBrightness;
        PhotoContrastBox.Text = _photoContrast.ToString("F0", CultureInfo.InvariantCulture);
        PhotoContrastSlider.Value = _photoContrast;
        PhotoSaturationBox.Text = _photoSaturation.ToString("F0", CultureInfo.InvariantCulture);
        PhotoSaturationSlider.Value = _photoSaturation;
        PhotoVibranceBox.Text = _photoVibrance.ToString("F0", CultureInfo.InvariantCulture);
        PhotoVibranceSlider.Value = _photoVibrance;
        PhotoTemperatureBox.Text = _photoTemperature.ToString("F0", CultureInfo.InvariantCulture);
        PhotoTemperatureSlider.Value = _photoTemperature;
        PhotoTintBox.Text = _photoTint.ToString("F0", CultureInfo.InvariantCulture);
        PhotoTintSlider.Value = _photoTint;
        PhotoHueBox.Text = _photoHue.ToString("F0", CultureInfo.InvariantCulture);
        PhotoHueSlider.Value = _photoHue;
        PhotoHighlightsBox.Text = _photoHighlights.ToString("F0", CultureInfo.InvariantCulture);
        PhotoHighlightsSlider.Value = _photoHighlights;
        PhotoShadowsBox.Text = _photoShadows.ToString("F0", CultureInfo.InvariantCulture);
        PhotoShadowsSlider.Value = _photoShadows;
        PhotoWhitesBox.Text = _photoWhites.ToString("F0", CultureInfo.InvariantCulture);
        PhotoWhitesSlider.Value = _photoWhites;
        PhotoBlacksBox.Text = _photoBlacks.ToString("F0", CultureInfo.InvariantCulture);
        PhotoBlacksSlider.Value = _photoBlacks;
        PhotoColorTintStrengthBox.Text = _photoColorTintStrength.ToString("F0", CultureInfo.InvariantCulture);
        PhotoColorTintStrengthSlider.Value = _photoColorTintStrength;
        PhotoColorTintSwatch.Background = new SolidColorBrush(Color.FromRgb(_photoColorTintR, _photoColorTintG, _photoColorTintB));
        PhotoColorTintHexBox.Text = ToHexColor(_photoColorTintR, _photoColorTintG, _photoColorTintB);
        PhotoBlurBox.Text = _photoBlurAmount.ToString("F0", CultureInfo.InvariantCulture);
        PhotoBlurSlider.Value = _photoBlurAmount;
        _suppressEvents = false;
    }

    private void ResetPhotoLookButton_Click(object sender, RoutedEventArgs e)
    {
        _undo.BeginChange();
        _photoBrightness = _photoContrast = _photoSaturation = 0;
        _photoVibrance = _photoTemperature = _photoTint = _photoHue = 0;
        _photoHighlights = _photoShadows = _photoWhites = _photoBlacks = 0;
        _photoColorTintStrength = 0;
        _photoColorTintR = _photoColorTintG = _photoColorTintB = 255;
        _photoBlurAmount = 0;
        RefreshPhotoLookUI();
        ScheduleCompositeRender();
        _undo.CommitChange();
    }

    private void PhotoBrightnessBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoBrightnessBox.Text, out var v)) return;
        double delta = v - _photoBrightness;
        _photoBrightness = v;
        if (_lookLinked && delta != 0) _state.Brightness = Math.Clamp(_state.Brightness + delta, -100, 100);
        _suppressEvents = true;
        PhotoBrightnessSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoBrightnessSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoBrightnessSlider.Value = snapped;
        PhotoBrightnessBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoBrightness) return;
        double delta = rounded - _photoBrightness;
        _photoBrightness = rounded;
        if (_lookLinked) _state.Brightness = Math.Clamp(_state.Brightness + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoContrastBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoContrastBox.Text, out var v)) return;
        double delta = v - _photoContrast;
        _photoContrast = v;
        if (_lookLinked && delta != 0) _state.Contrast = Math.Clamp(_state.Contrast + delta, -100, 100);
        _suppressEvents = true;
        PhotoContrastSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoContrastSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoContrastSlider.Value = snapped;
        PhotoContrastBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoContrast) return;
        double delta = rounded - _photoContrast;
        _photoContrast = rounded;
        if (_lookLinked) _state.Contrast = Math.Clamp(_state.Contrast + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoSaturationBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoSaturationBox.Text, out var v)) return;
        double delta = v - _photoSaturation;
        _photoSaturation = v;
        if (_lookLinked && delta != 0) _state.Saturation = Math.Clamp(_state.Saturation + delta, -100, 100);
        _suppressEvents = true;
        PhotoSaturationSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoSaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoSaturationSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoSaturationSlider.Value = snapped;
        PhotoSaturationBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoSaturation) return;
        double delta = rounded - _photoSaturation;
        _photoSaturation = rounded;
        if (_lookLinked) _state.Saturation = Math.Clamp(_state.Saturation + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoVibranceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoVibranceBox.Text, out var v)) return;
        double delta = v - _photoVibrance;
        _photoVibrance = v;
        if (_lookLinked && delta != 0) _state.Vibrance = Math.Clamp(_state.Vibrance + delta, -100, 100);
        _suppressEvents = true;
        PhotoVibranceSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoVibranceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoVibranceSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoVibranceSlider.Value = snapped;
        PhotoVibranceBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoVibrance) return;
        double delta = rounded - _photoVibrance;
        _photoVibrance = rounded;
        if (_lookLinked) _state.Vibrance = Math.Clamp(_state.Vibrance + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoTemperatureBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoTemperatureBox.Text, out var v)) return;
        double delta = v - _photoTemperature;
        _photoTemperature = v;
        if (_lookLinked && delta != 0) _state.Temperature = Math.Clamp(_state.Temperature + delta, -100, 100);
        _suppressEvents = true;
        PhotoTemperatureSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoTemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoTemperatureSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoTemperatureSlider.Value = snapped;
        PhotoTemperatureBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoTemperature) return;
        double delta = rounded - _photoTemperature;
        _photoTemperature = rounded;
        if (_lookLinked) _state.Temperature = Math.Clamp(_state.Temperature + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoTintBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoTintBox.Text, out var v)) return;
        double delta = v - _photoTint;
        _photoTint = v;
        if (_lookLinked && delta != 0) _state.Tint = Math.Clamp(_state.Tint + delta, -100, 100);
        _suppressEvents = true;
        PhotoTintSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoTintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoTintSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoTintSlider.Value = snapped;
        PhotoTintBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoTint) return;
        double delta = rounded - _photoTint;
        _photoTint = rounded;
        if (_lookLinked) _state.Tint = Math.Clamp(_state.Tint + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoHueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoHueBox.Text, out var v)) return;
        double delta = v - _photoHue;
        _photoHue = v;
        if (_lookLinked && delta != 0) _state.Hue = Math.Clamp(_state.Hue + delta, -180, 180);
        _suppressEvents = true;
        PhotoHueSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoHueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoHueSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoHueSlider.Value = snapped;
        PhotoHueBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoHue) return;
        double delta = rounded - _photoHue;
        _photoHue = rounded;
        if (_lookLinked) _state.Hue = Math.Clamp(_state.Hue + delta, -180, 180);
        ScheduleCompositeRender();
    }

    private void PhotoHighlightsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoHighlightsBox.Text, out var v)) return;
        double delta = v - _photoHighlights;
        _photoHighlights = v;
        if (_lookLinked && delta != 0) _state.Highlights = Math.Clamp(_state.Highlights + delta, -100, 100);
        _suppressEvents = true;
        PhotoHighlightsSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoHighlightsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoHighlightsSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoHighlightsSlider.Value = snapped;
        PhotoHighlightsBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoHighlights) return;
        double delta = rounded - _photoHighlights;
        _photoHighlights = rounded;
        if (_lookLinked) _state.Highlights = Math.Clamp(_state.Highlights + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoShadowsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoShadowsBox.Text, out var v)) return;
        double delta = v - _photoShadows;
        _photoShadows = v;
        if (_lookLinked && delta != 0) _state.Shadows = Math.Clamp(_state.Shadows + delta, -100, 100);
        _suppressEvents = true;
        PhotoShadowsSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoShadowsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoShadowsSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoShadowsSlider.Value = snapped;
        PhotoShadowsBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoShadows) return;
        double delta = rounded - _photoShadows;
        _photoShadows = rounded;
        if (_lookLinked) _state.Shadows = Math.Clamp(_state.Shadows + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoWhitesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoWhitesBox.Text, out var v)) return;
        double delta = v - _photoWhites;
        _photoWhites = v;
        if (_lookLinked && delta != 0) _state.Whites = Math.Clamp(_state.Whites + delta, -100, 100);
        _suppressEvents = true;
        PhotoWhitesSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoWhitesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoWhitesSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoWhitesSlider.Value = snapped;
        PhotoWhitesBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoWhites) return;
        double delta = rounded - _photoWhites;
        _photoWhites = rounded;
        if (_lookLinked) _state.Whites = Math.Clamp(_state.Whites + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoBlacksBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoBlacksBox.Text, out var v)) return;
        double delta = v - _photoBlacks;
        _photoBlacks = v;
        if (_lookLinked && delta != 0) _state.Blacks = Math.Clamp(_state.Blacks + delta, -100, 100);
        _suppressEvents = true;
        PhotoBlacksSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoBlacksSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoBlacksSlider.Value, 3, 0);
        _suppressEvents = true;
        PhotoBlacksSlider.Value = snapped;
        PhotoBlacksBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        double rounded = Math.Round(snapped);
        if (rounded == _photoBlacks) return;
        double delta = rounded - _photoBlacks;
        _photoBlacks = rounded;
        if (_lookLinked) _state.Blacks = Math.Clamp(_state.Blacks + delta, -100, 100);
        ScheduleCompositeRender();
    }

    private void PhotoBlurBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoBlurBox.Text, out var v) || v < 0) return;
        _photoBlurAmount = v;
        _suppressEvents = true;
        PhotoBlurSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(PhotoBlurSlider.Value);
        _suppressEvents = true;
        PhotoBlurBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _photoBlurAmount) return;
        _photoBlurAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- Finishing effects: film grain + vignette, applied once to the
    //      final composite result only (see CompositeOverlayOntoPhoto's
    //      grainAmount/vignetteAmount params) -- not shared with the avatar
    //      or photo look, and not undo-tracked, same treatment as the photo
    //      look above. ----

    private void RefreshFinishUI()
    {
        _suppressEvents = true;
        GrainBox.Text = _grainAmount.ToString("F0", CultureInfo.InvariantCulture);
        GrainSlider.Value = _grainAmount;
        VignetteBox.Text = _vignetteAmount.ToString("F0", CultureInfo.InvariantCulture);
        VignetteSlider.Value = _vignetteAmount;
        SoftnessBox.Text = _softnessAmount.ToString("F0", CultureInfo.InvariantCulture);
        SoftnessSlider.Value = _softnessAmount;
        SharpnessBox.Text = _sharpnessAmount.ToString("F0", CultureInfo.InvariantCulture);
        SharpnessSlider.Value = _sharpnessAmount;
        FadeBox.Text = _fadeAmount.ToString("F0", CultureInfo.InvariantCulture);
        FadeSlider.Value = _fadeAmount;
        GlowBox.Text = _glowAmount.ToString("F0", CultureInfo.InvariantCulture);
        GlowSlider.Value = _glowAmount;
        ChromaticAberrationBox.Text = _chromaticAberrationAmount.ToString("F0", CultureInfo.InvariantCulture);
        ChromaticAberrationSlider.Value = _chromaticAberrationAmount;
        ColorBleedBox.Text = _colorBleedAmount.ToString("F0", CultureInfo.InvariantCulture);
        ColorBleedSlider.Value = _colorBleedAmount;
        ScanlineBox.Text = _scanlineAmount.ToString("F0", CultureInfo.InvariantCulture);
        ScanlineSlider.Value = _scanlineAmount;
        ClarityBox.Text = _clarityAmount.ToString("F0", CultureInfo.InvariantCulture);
        ClaritySlider.Value = _clarityAmount;
        LightLeakBox.Text = _lightLeakAmount.ToString("F0", CultureInfo.InvariantCulture);
        LightLeakSlider.Value = _lightLeakAmount;
        LightLeakColorSwatch.Background = new SolidColorBrush(Color.FromRgb(_lightLeakColorR, _lightLeakColorG, _lightLeakColorB));
        LightLeakColorHexBox.Text = ToHexColor(_lightLeakColorR, _lightLeakColorG, _lightLeakColorB);
        LightLeakDirectionBox.Text = _lightLeakAngle.ToString("F0", CultureInfo.InvariantCulture);
        LightLeakDirectionSlider.Value = _lightLeakAngle;
        ToneGradientBox.Text = _toneGradientAmount.ToString("F0", CultureInfo.InvariantCulture);
        ToneGradientSlider.Value = _toneGradientAmount;
        ToneGradientDirectionBox.Text = _toneGradientRotation.ToString("F0", CultureInfo.InvariantCulture);
        ToneGradientDirectionSlider.Value = _toneGradientRotation;
        ToneGradientLightSwatch.Background = new SolidColorBrush(Color.FromRgb(_toneGradientLightR, _toneGradientLightG, _toneGradientLightB));
        ToneGradientLightHexBox.Text = ToHexColor(_toneGradientLightR, _toneGradientLightG, _toneGradientLightB);
        ToneGradientDarkSwatch.Background = new SolidColorBrush(Color.FromRgb(_toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB));
        ToneGradientDarkHexBox.Text = ToHexColor(_toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB);
        DropShadowBox.Text = _dropShadowAmount.ToString("F0", CultureInfo.InvariantCulture);
        DropShadowSlider.Value = _dropShadowAmount;
        DropShadowDirectionBox.Text = _dropShadowDirection.ToString("F0", CultureInfo.InvariantCulture);
        DropShadowDirectionSlider.Value = _dropShadowDirection;
        DropShadowDistanceBox.Text = _dropShadowDistance.ToString("F0", CultureInfo.InvariantCulture);
        DropShadowDistanceSlider.Value = _dropShadowDistance;
        DropShadowBlurBox.Text = _dropShadowBlur.ToString("F0", CultureInfo.InvariantCulture);
        DropShadowBlurSlider.Value = _dropShadowBlur;
        DropShadowColorSwatch.Background = new SolidColorBrush(Color.FromRgb(_dropShadowColorR, _dropShadowColorG, _dropShadowColorB));
        DropShadowColorHexBox.Text = ToHexColor(_dropShadowColorR, _dropShadowColorG, _dropShadowColorB);
        DropShadowBlendModeCombo.SelectedIndex = _dropShadowBlendMode switch
        {
            ImageAdjustment.DropShadowBlendMode.Normal => 1,
            ImageAdjustment.DropShadowBlendMode.Additive => 2,
            _ => 0,
        };
        _suppressEvents = false;
    }

    private void ResetFinishButton_Click(object sender, RoutedEventArgs e)
    {
        _undo.BeginChange();
        _grainAmount = _vignetteAmount = 0;
        _softnessAmount = _sharpnessAmount = 0;
        _fadeAmount = _glowAmount = 0;
        _chromaticAberrationAmount = _colorBleedAmount = _scanlineAmount = 0;
        _clarityAmount = _lightLeakAmount = 0;
        _lightLeakAngle = 225;
        _lightLeakDistance = 1.0;
        _lightLeakColorB = 60; _lightLeakColorG = 160; _lightLeakColorR = 255;
        _toneGradientAmount = 0;
        _toneGradientRotation = 180;
        _toneGradientLightR = _toneGradientLightG = _toneGradientLightB = 255;
        _toneGradientDarkR = _toneGradientDarkG = _toneGradientDarkB = 0;
        _dropShadowAmount = 0;
        _dropShadowDirection = 0;
        _dropShadowDistance = 100;
        _dropShadowBlur = 10;
        _dropShadowColorB = _dropShadowColorG = _dropShadowColorR = 0;
        _dropShadowBlendMode = ImageAdjustment.DropShadowBlendMode.Multiply;
        RefreshFinishUI();
        ScheduleCompositeRender();
        _undo.CommitChange();
    }

    private void GrainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GrainBox.Text, out var v) || v < 0) return;
        _grainAmount = v;
        _suppressEvents = true;
        GrainSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void GrainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(GrainSlider.Value);
        _suppressEvents = true;
        GrainBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _grainAmount) return;
        _grainAmount = rounded;
        ScheduleCompositeRender();
    }

    private void VignetteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(VignetteBox.Text, out var v) || v < 0) return;
        _vignetteAmount = v;
        _suppressEvents = true;
        VignetteSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void VignetteSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(VignetteSlider.Value);
        _suppressEvents = true;
        VignetteBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _vignetteAmount) return;
        _vignetteAmount = rounded;
        ScheduleCompositeRender();
    }

    private void SoftnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(SoftnessBox.Text, out var v) || v < 0) return;
        _softnessAmount = v;
        _suppressEvents = true;
        SoftnessSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void SoftnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(SoftnessSlider.Value);
        _suppressEvents = true;
        SoftnessBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _softnessAmount) return;
        _softnessAmount = rounded;
        ScheduleCompositeRender();
    }

    private void SharpnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(SharpnessBox.Text, out var v) || v < 0) return;
        _sharpnessAmount = v;
        _suppressEvents = true;
        SharpnessSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void SharpnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(SharpnessSlider.Value);
        _suppressEvents = true;
        SharpnessBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _sharpnessAmount) return;
        _sharpnessAmount = rounded;
        ScheduleCompositeRender();
    }

    private void FadeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(FadeBox.Text, out var v) || v < 0) return;
        _fadeAmount = v;
        _suppressEvents = true;
        FadeSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void FadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(FadeSlider.Value);
        _suppressEvents = true;
        FadeBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _fadeAmount) return;
        _fadeAmount = rounded;
        ScheduleCompositeRender();
    }

    private void GlowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GlowBox.Text, out var v) || v < 0) return;
        _glowAmount = v;
        _suppressEvents = true;
        GlowSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void GlowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(GlowSlider.Value);
        _suppressEvents = true;
        GlowBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _glowAmount) return;
        _glowAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ChromaticAberrationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ChromaticAberrationBox.Text, out var v) || v < 0) return;
        _chromaticAberrationAmount = v;
        _suppressEvents = true;
        ChromaticAberrationSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ChromaticAberrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ChromaticAberrationSlider.Value);
        _suppressEvents = true;
        ChromaticAberrationBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _chromaticAberrationAmount) return;
        _chromaticAberrationAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ColorBleedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ColorBleedBox.Text, out var v) || v < 0) return;
        _colorBleedAmount = v;
        _suppressEvents = true;
        ColorBleedSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ColorBleedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ColorBleedSlider.Value);
        _suppressEvents = true;
        ColorBleedBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _colorBleedAmount) return;
        _colorBleedAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ScanlineBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ScanlineBox.Text, out var v) || v < 0) return;
        _scanlineAmount = v;
        _suppressEvents = true;
        ScanlineSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ScanlineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ScanlineSlider.Value);
        _suppressEvents = true;
        ScanlineBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _scanlineAmount) return;
        _scanlineAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ClarityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ClarityBox.Text, out var v) || v < 0) return;
        _clarityAmount = v;
        _suppressEvents = true;
        ClaritySlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ClaritySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ClaritySlider.Value);
        _suppressEvents = true;
        ClarityBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _clarityAmount) return;
        _clarityAmount = rounded;
        ScheduleCompositeRender();
    }

    private void LightLeakBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakBox.Text, out var v) || v < 0) return;
        _lightLeakAmount = v;
        _suppressEvents = true;
        LightLeakSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void LightLeakSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(LightLeakSlider.Value);
        _suppressEvents = true;
        LightLeakBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _lightLeakAmount) return;
        _lightLeakAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- ライトリーク direction: plain slider, like every other row (the
    //      direction dial was removed app-wide in favor of sliders). This
    //      also drops the dial's old distance affordance -- _lightLeakDistance
    //      just stays fixed at 1.0 now (see its own doc comment), same as
    //      グラデーション/ドロップシャドウ's own direction always was. ----

    private void LightLeakDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakDirectionBox.Text, out var v)) return;
        _lightLeakAngle = Math.Clamp(v, 0, 360);
        _suppressEvents = true;
        LightLeakDirectionSlider.Value = _lightLeakAngle;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void LightLeakDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(LightLeakDirectionSlider.Value);
        _suppressEvents = true;
        LightLeakDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _lightLeakAngle) return;
        _lightLeakAngle = rounded;
        ScheduleCompositeRender();
    }

    // ---- ライトリーク color: same custom wheel+RGB popup as ドロップシャドウ
    //      (see GetColorWheelBitmap/RgbToHsv/HsvToRgb/PositionColorWheelCursor
    //      above, all shared), just its own presets (暖色/寒色/白) and its
    //      own popup/wheel/slider elements. ----

    private void LightLeakColorButton_Click(object sender, RoutedEventArgs e)
    {
        LightLeakColorWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncLightLeakColorUI(_lightLeakColorR, _lightLeakColorG, _lightLeakColorB);
        _suppressEvents = false;
        LightLeakColorPopup.IsOpen = true;
    }

    private void LightLeakColorPreset_Click(object sender, RoutedEventArgs e)
    {
        var brush = (SolidColorBrush)((Button)sender).Background;
        SetLightLeakColor(brush.Color.R, brush.Color.G, brush.Color.B);
    }

    private bool _isDraggingLightLeakColorWheel;

    private void LightLeakColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingLightLeakColorWheel = true;
        LightLeakColorWheel.CaptureMouse();
        UpdateLightLeakColorFromWheelPosition(e.GetPosition(LightLeakColorWheel));
    }

    private void LightLeakColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLightLeakColorWheel) return;
        UpdateLightLeakColorFromWheelPosition(e.GetPosition(LightLeakColorWheel));
    }

    private void LightLeakColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingLightLeakColorWheel = false;
        LightLeakColorWheel.ReleaseMouseCapture();
    }

    private void UpdateLightLeakColorFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _lightLeakHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _lightLeakSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_lightLeakHue, _lightLeakSat, LightLeakColorValueSlider.Value / 100.0);
        SetLightLeakColor(r, g, b);
    }

    private void LightLeakColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_lightLeakHue, _lightLeakSat, LightLeakColorValueSlider.Value / 100.0);
        SetLightLeakColor(r, g, b);
    }

    private void LightLeakColorRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetLightLeakColor((byte)Math.Round(LightLeakColorRSlider.Value), _lightLeakColorG, _lightLeakColorB);
    }

    private void LightLeakColorGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetLightLeakColor(_lightLeakColorR, (byte)Math.Round(LightLeakColorGSlider.Value), _lightLeakColorB);
    }

    private void LightLeakColorBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetLightLeakColor(_lightLeakColorR, _lightLeakColorG, (byte)Math.Round(LightLeakColorBSlider.Value));
    }

    private void LightLeakColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakColorRBox.Text, out var v)) return;
        SetLightLeakColor((byte)Math.Clamp(v, 0, 255), _lightLeakColorG, _lightLeakColorB);
    }

    private void LightLeakColorGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakColorGBox.Text, out var v)) return;
        SetLightLeakColor(_lightLeakColorR, (byte)Math.Clamp(v, 0, 255), _lightLeakColorB);
    }

    private void LightLeakColorBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakColorBBox.Text, out var v)) return;
        SetLightLeakColor(_lightLeakColorR, _lightLeakColorG, (byte)Math.Clamp(v, 0, 255));
    }

    private void LightLeakColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(LightLeakColorHexBox.Text, out var r, out var g, out var b)) return;
        SetLightLeakColor(r, g, b);
    }

    private void SyncLightLeakColorUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _lightLeakSat = s;
        if (s > 0.001) _lightLeakHue = h;

        LightLeakColorRSlider.Value = r;
        LightLeakColorRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        LightLeakColorGSlider.Value = g;
        LightLeakColorGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        LightLeakColorBSlider.Value = b;
        LightLeakColorBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        LightLeakColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(LightLeakColorWheelCursor, _lightLeakHue, _lightLeakSat);
        LightLeakColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        LightLeakColorHexBox.Text = ToHexColor(r, g, b);
    }

    private void SetLightLeakColor(byte r, byte g, byte b)
    {
        _lightLeakColorR = r;
        _lightLeakColorG = g;
        _lightLeakColorB = b;

        _suppressEvents = true;
        SyncLightLeakColorUI(r, g, b);
        _suppressEvents = false;

        LightLeakColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    private void ToneGradientBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientBox.Text, out var v) || v < 0) return;
        _toneGradientAmount = v;
        _suppressEvents = true;
        ToneGradientSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ToneGradientSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ToneGradientSlider.Value);
        _suppressEvents = true;
        ToneGradientBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _toneGradientAmount) return;
        _toneGradientAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- グラデーション direction: plain slider, like every other row (the
    //      direction dial was removed app-wide in favor of sliders). ----

    private void ToneGradientDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientDirectionBox.Text, out var v)) return;
        _toneGradientRotation = Math.Clamp(v, 0, 360);
        _suppressEvents = true;
        ToneGradientDirectionSlider.Value = _toneGradientRotation;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void ToneGradientDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ToneGradientDirectionSlider.Value);
        _suppressEvents = true;
        ToneGradientDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _toneGradientRotation) return;
        _toneGradientRotation = rounded;
        ScheduleCompositeRender();
    }

    // ---- Drop shadow: duplicates the avatar's own silhouette, offset in a
    //      direction and blurred/tinted (multiply blend) -- see
    //      ImageAdjustment.ApplyDropShadow. Direction is a plain slider,
    //      like every other row (the direction dial was removed app-wide in
    //      favor of sliders); 幅(distance) stays its own separate
    //      DropShadowDistanceSlider/Box. ----

    private void DropShadowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowBox.Text, out var v) || v < 0) return;
        _dropShadowAmount = v;
        _suppressEvents = true;
        DropShadowSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void DropShadowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowSlider.Value);
        _suppressEvents = true;
        DropShadowBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _dropShadowAmount) return;
        _dropShadowAmount = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowDirectionBox.Text, out var v)) return;
        _dropShadowDirection = Math.Clamp(v, 0, 360);
        _suppressEvents = true;
        DropShadowDirectionSlider.Value = _dropShadowDirection;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void DropShadowDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowDirectionSlider.Value);
        _suppressEvents = true;
        DropShadowDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _dropShadowDirection) return;
        _dropShadowDirection = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowDistanceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowDistanceBox.Text, out var v) || v < 0) return;
        _dropShadowDistance = v;
        _suppressEvents = true;
        DropShadowDistanceSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void DropShadowDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowDistanceSlider.Value);
        _suppressEvents = true;
        DropShadowDistanceBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _dropShadowDistance) return;
        _dropShadowDistance = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowBlurBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowBlurBox.Text, out var v) || v < 0) return;
        _dropShadowBlur = v;
        _suppressEvents = true;
        DropShadowBlurSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void DropShadowBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowBlurSlider.Value);
        _suppressEvents = true;
        DropShadowBlurBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _dropShadowBlur) return;
        _dropShadowBlur = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowBlendModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (DropShadowBlendModeCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        _dropShadowBlendMode = tag switch
        {
            "additive" => ImageAdjustment.DropShadowBlendMode.Additive,
            "normal" => ImageAdjustment.DropShadowBlendMode.Normal,
            _ => ImageAdjustment.DropShadowBlendMode.Multiply,
        };
        ScheduleCompositeRender();
    }

    /// <summary>Opens DropShadowColorPopup (a custom in-app picker styled
    /// like the rest of AvaSnap -- Card, rounded swatches, the same slider
    /// row look) instead of the native OS color dialog, seeding the wheel/
    /// 明度/R-G-B controls from the current color.</summary>
    private void DropShadowColorButton_Click(object sender, RoutedEventArgs e)
    {
        DropShadowColorWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncColorPickerUI(_dropShadowColorR, _dropShadowColorG, _dropShadowColorB);
        _suppressEvents = false;
        DropShadowColorPopup.IsOpen = true;
    }

    // ---- Color wheel: angle = hue, distance from center = saturation, a
    //      140x140 bitmap built once (value fixed at 1 -- see
    //      GetColorWheelBitmap) since 明度 (value/brightness) is handled by
    //      a separate slider that just rescales the picked hue/saturation's
    //      RGB, rather than needing the wheel bitmap itself regenerated
    //      every time it changes. ----

    private const int ColorWheelSize = 140;
    private WriteableBitmap? _colorWheelBitmap;
    private bool _isDraggingColorWheel;

    /// <summary>Last meaningfully-picked hue/saturation (0..360 / 0..1),
    /// cached separately from the RGB fields: RGB alone can't represent
    /// hue when saturation is 0 (gray/black), so without this cache,
    /// dragging 明度 up from black would lose whatever hue the wheel was
    /// last set to instead of returning to it.</summary>
    private double _dropShadowHue, _dropShadowSat;

    private WriteableBitmap GetColorWheelBitmap()
    {
        if (_colorWheelBitmap is not null) return _colorWheelBitmap;
        var pixels = new byte[ColorWheelSize * ColorWheelSize * 4];
        double center = (ColorWheelSize - 1) / 2.0;
        for (int y = 0; y < ColorWheelSize; y++)
        {
            for (int x = 0; x < ColorWheelSize; x++)
            {
                double dx = x - center, dy = y - center;
                double dist = Math.Sqrt(dx * dx + dy * dy) / center;
                if (dist > 1.0) continue; // leave transparent outside the circle
                double hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
                double sat = Math.Min(dist, 1.0);
                var (r, g, b) = HsvToRgb(hue, sat, 1.0);
                int i = (y * ColorWheelSize + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
        var bmp = new WriteableBitmap(ColorWheelSize, ColorWheelSize, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, ColorWheelSize, ColorWheelSize), pixels, ColorWheelSize * 4, 0);
        bmp.Freeze();
        _colorWheelBitmap = bmp;
        return bmp;
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double h = 0;
        if (delta > 1e-9)
        {
            if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
            else h = 60 * ((rf - gf) / delta + 4);
        }
        if (h < 0) h += 360;
        double s = max <= 1e-9 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        var (rf, gf, bf) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((rf + m) * 255), (byte)Math.Round((gf + m) * 255), (byte)Math.Round((bf + m) * 255));
    }

    private static string ToHexColor(byte r, byte g, byte b) =>
        "#" + r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture) + b.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Accepts "#RRGGBB" or "RRGGBB" (leading "#" optional, matching
    /// what a user might paste from elsewhere); anything else (including a
    /// still-in-progress partial edit) just fails silently so typing a hex
    /// code character by character doesn't fight the field.</summary>
    private static bool TryParseHexColor(string text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static void PositionColorWheelCursor(Border cursor, double hue, double sat)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double rad = hue * Math.PI / 180.0;
        double r = Math.Clamp(sat, 0, 1) * center;
        double x = center + Math.Cos(rad) * r;
        double y = center + Math.Sin(rad) * r;
        cursor.Margin = new Thickness(x - cursor.Width / 2, y - cursor.Height / 2, 0, 0);
    }

    private void DropShadowColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingColorWheel = true;
        DropShadowColorWheel.CaptureMouse();
        UpdateColorFromWheelPosition(e.GetPosition(DropShadowColorWheel));
    }

    private void DropShadowColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingColorWheel) return;
        UpdateColorFromWheelPosition(e.GetPosition(DropShadowColorWheel));
    }

    private void DropShadowColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingColorWheel = false;
        DropShadowColorWheel.ReleaseMouseCapture();
    }

    private void UpdateColorFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _dropShadowHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _dropShadowSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_dropShadowHue, _dropShadowSat, DropShadowColorValueSlider.Value / 100.0);
        SetDropShadowColor(r, g, b);
    }

    private void DropShadowColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_dropShadowHue, _dropShadowSat, DropShadowColorValueSlider.Value / 100.0);
        SetDropShadowColor(r, g, b);
    }

    private void DropShadowColorRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetDropShadowColor((byte)Math.Round(DropShadowColorRSlider.Value), _dropShadowColorG, _dropShadowColorB);
    }

    private void DropShadowColorGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetDropShadowColor(_dropShadowColorR, (byte)Math.Round(DropShadowColorGSlider.Value), _dropShadowColorB);
    }

    private void DropShadowColorBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetDropShadowColor(_dropShadowColorR, _dropShadowColorG, (byte)Math.Round(DropShadowColorBSlider.Value));
    }

    private void DropShadowColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowColorRBox.Text, out var v)) return;
        SetDropShadowColor((byte)Math.Clamp(v, 0, 255), _dropShadowColorG, _dropShadowColorB);
    }

    private void DropShadowColorGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowColorGBox.Text, out var v)) return;
        SetDropShadowColor(_dropShadowColorR, (byte)Math.Clamp(v, 0, 255), _dropShadowColorB);
    }

    private void DropShadowColorBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowColorBBox.Text, out var v)) return;
        SetDropShadowColor(_dropShadowColorR, _dropShadowColorG, (byte)Math.Clamp(v, 0, 255));
    }

    private void DropShadowColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(DropShadowColorHexBox.Text, out var r, out var g, out var b)) return;
        SetDropShadowColor(r, g, b);
    }

    /// <summary>Pure UI sync (wheel cursor, 明度/R/G/B sliders+boxes,
    /// preview swatch) from an RGB triple -- no field writes, no render.
    /// Shared by SetDropShadowColor (the real "apply" path) and
    /// DropShadowColorButton_Click (just seeding the popup on open, where
    /// re-triggering a render would be pure waste since the color hasn't
    /// actually changed).</summary>
    private void SyncColorPickerUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _dropShadowSat = s;
        // Hue is undefined at s=0 (gray/black) -- keep whatever hue was
        // last meaningful instead of snapping to 0 (red), so the wheel
        // cursor doesn't jump around while dragging 明度 down through gray.
        if (s > 0.001) _dropShadowHue = h;

        DropShadowColorRSlider.Value = r;
        DropShadowColorRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        DropShadowColorGSlider.Value = g;
        DropShadowColorGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        DropShadowColorBSlider.Value = b;
        DropShadowColorBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        DropShadowColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(DropShadowColorWheelCursor, _dropShadowHue, _dropShadowSat);
        DropShadowColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        DropShadowColorHexBox.Text = ToHexColor(r, g, b);
    }

    /// <summary>Single choke point for every way the shadow color can
    /// change (preset click, wheel drag, 明度, or any of the 3 R/G/B
    /// sliders/boxes) -- keeps the popup's controls AND the main button's
    /// own small swatch all in sync with each other and with the
    /// underlying _dropShadowColor* fields, regardless of which control
    /// triggered it.</summary>
    private void SetDropShadowColor(byte r, byte g, byte b)
    {
        _dropShadowColorR = r;
        _dropShadowColorG = g;
        _dropShadowColorB = b;

        _suppressEvents = true;
        SyncColorPickerUI(r, g, b);
        _suppressEvents = false;

        DropShadowColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    // ---- グラデーション 明色/暗色: same custom wheel+RGB popup as
    //      ドロップシャドウ's own, just manual now instead of always
    //      auto-computed -- see ToneGradientAutoDetectButton_Click for the
    //      one-shot re-detect path. ----

    private void ToneGradientLightColorButton_Click(object sender, RoutedEventArgs e)
    {
        ToneGradientLightColorWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncToneGradientLightColorUI(_toneGradientLightR, _toneGradientLightG, _toneGradientLightB);
        _suppressEvents = false;
        ToneGradientLightColorPopup.IsOpen = true;
    }

    private bool _isDraggingToneGradientLightWheel;

    private void ToneGradientLightColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingToneGradientLightWheel = true;
        ToneGradientLightColorWheel.CaptureMouse();
        UpdateToneGradientLightColorFromWheelPosition(e.GetPosition(ToneGradientLightColorWheel));
    }

    private void ToneGradientLightColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingToneGradientLightWheel) return;
        UpdateToneGradientLightColorFromWheelPosition(e.GetPosition(ToneGradientLightColorWheel));
    }

    private void ToneGradientLightColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingToneGradientLightWheel = false;
        ToneGradientLightColorWheel.ReleaseMouseCapture();
    }

    private void UpdateToneGradientLightColorFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _toneGradientLightHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _toneGradientLightSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_toneGradientLightHue, _toneGradientLightSat, ToneGradientLightColorValueSlider.Value / 100.0);
        SetToneGradientLightColor(r, g, b);
    }

    private void ToneGradientLightColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_toneGradientLightHue, _toneGradientLightSat, ToneGradientLightColorValueSlider.Value / 100.0);
        SetToneGradientLightColor(r, g, b);
    }

    private void ToneGradientLightColorRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientLightColor((byte)Math.Round(ToneGradientLightColorRSlider.Value), _toneGradientLightG, _toneGradientLightB);
    }

    private void ToneGradientLightColorGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientLightColor(_toneGradientLightR, (byte)Math.Round(ToneGradientLightColorGSlider.Value), _toneGradientLightB);
    }

    private void ToneGradientLightColorBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientLightColor(_toneGradientLightR, _toneGradientLightG, (byte)Math.Round(ToneGradientLightColorBSlider.Value));
    }

    private void ToneGradientLightColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientLightColorRBox.Text, out var v)) return;
        SetToneGradientLightColor((byte)Math.Clamp(v, 0, 255), _toneGradientLightG, _toneGradientLightB);
    }

    private void ToneGradientLightColorGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientLightColorGBox.Text, out var v)) return;
        SetToneGradientLightColor(_toneGradientLightR, (byte)Math.Clamp(v, 0, 255), _toneGradientLightB);
    }

    private void ToneGradientLightColorBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientLightColorBBox.Text, out var v)) return;
        SetToneGradientLightColor(_toneGradientLightR, _toneGradientLightG, (byte)Math.Clamp(v, 0, 255));
    }

    private void ToneGradientLightHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(ToneGradientLightHexBox.Text, out var r, out var g, out var b)) return;
        SetToneGradientLightColor(r, g, b);
    }

    private void SyncToneGradientLightColorUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _toneGradientLightSat = s;
        if (s > 0.001) _toneGradientLightHue = h;

        ToneGradientLightColorRSlider.Value = r;
        ToneGradientLightColorRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        ToneGradientLightColorGSlider.Value = g;
        ToneGradientLightColorGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        ToneGradientLightColorBSlider.Value = b;
        ToneGradientLightColorBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        ToneGradientLightColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(ToneGradientLightColorWheelCursor, _toneGradientLightHue, _toneGradientLightSat);
        ToneGradientLightColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ToneGradientLightHexBox.Text = ToHexColor(r, g, b);
    }

    private void SetToneGradientLightColor(byte r, byte g, byte b)
    {
        _toneGradientLightR = r;
        _toneGradientLightG = g;
        _toneGradientLightB = b;

        _suppressEvents = true;
        SyncToneGradientLightColorUI(r, g, b);
        _suppressEvents = false;

        ToneGradientLightSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    private void ToneGradientDarkColorButton_Click(object sender, RoutedEventArgs e)
    {
        ToneGradientDarkColorWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncToneGradientDarkColorUI(_toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB);
        _suppressEvents = false;
        ToneGradientDarkColorPopup.IsOpen = true;
    }

    private bool _isDraggingToneGradientDarkWheel;

    private void ToneGradientDarkColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingToneGradientDarkWheel = true;
        ToneGradientDarkColorWheel.CaptureMouse();
        UpdateToneGradientDarkColorFromWheelPosition(e.GetPosition(ToneGradientDarkColorWheel));
    }

    private void ToneGradientDarkColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingToneGradientDarkWheel) return;
        UpdateToneGradientDarkColorFromWheelPosition(e.GetPosition(ToneGradientDarkColorWheel));
    }

    private void ToneGradientDarkColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingToneGradientDarkWheel = false;
        ToneGradientDarkColorWheel.ReleaseMouseCapture();
    }

    private void UpdateToneGradientDarkColorFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _toneGradientDarkHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _toneGradientDarkSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_toneGradientDarkHue, _toneGradientDarkSat, ToneGradientDarkColorValueSlider.Value / 100.0);
        SetToneGradientDarkColor(r, g, b);
    }

    private void ToneGradientDarkColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_toneGradientDarkHue, _toneGradientDarkSat, ToneGradientDarkColorValueSlider.Value / 100.0);
        SetToneGradientDarkColor(r, g, b);
    }

    private void ToneGradientDarkColorRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientDarkColor((byte)Math.Round(ToneGradientDarkColorRSlider.Value), _toneGradientDarkG, _toneGradientDarkB);
    }

    private void ToneGradientDarkColorGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientDarkColor(_toneGradientDarkR, (byte)Math.Round(ToneGradientDarkColorGSlider.Value), _toneGradientDarkB);
    }

    private void ToneGradientDarkColorBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetToneGradientDarkColor(_toneGradientDarkR, _toneGradientDarkG, (byte)Math.Round(ToneGradientDarkColorBSlider.Value));
    }

    private void ToneGradientDarkColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientDarkColorRBox.Text, out var v)) return;
        SetToneGradientDarkColor((byte)Math.Clamp(v, 0, 255), _toneGradientDarkG, _toneGradientDarkB);
    }

    private void ToneGradientDarkColorGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientDarkColorGBox.Text, out var v)) return;
        SetToneGradientDarkColor(_toneGradientDarkR, (byte)Math.Clamp(v, 0, 255), _toneGradientDarkB);
    }

    private void ToneGradientDarkColorBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientDarkColorBBox.Text, out var v)) return;
        SetToneGradientDarkColor(_toneGradientDarkR, _toneGradientDarkG, (byte)Math.Clamp(v, 0, 255));
    }

    private void ToneGradientDarkHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(ToneGradientDarkHexBox.Text, out var r, out var g, out var b)) return;
        SetToneGradientDarkColor(r, g, b);
    }

    private void SyncToneGradientDarkColorUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _toneGradientDarkSat = s;
        if (s > 0.001) _toneGradientDarkHue = h;

        ToneGradientDarkColorRSlider.Value = r;
        ToneGradientDarkColorRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        ToneGradientDarkColorGSlider.Value = g;
        ToneGradientDarkColorGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        ToneGradientDarkColorBSlider.Value = b;
        ToneGradientDarkColorBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        ToneGradientDarkColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(ToneGradientDarkColorWheelCursor, _toneGradientDarkHue, _toneGradientDarkSat);
        ToneGradientDarkColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ToneGradientDarkHexBox.Text = ToHexColor(r, g, b);
    }

    private void SetToneGradientDarkColor(byte r, byte g, byte b)
    {
        _toneGradientDarkR = r;
        _toneGradientDarkG = g;
        _toneGradientDarkB = b;

        _suppressEvents = true;
        SyncToneGradientDarkColorUI(r, g, b);
        _suppressEvents = false;

        ToneGradientDarkSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    /// <summary>Re-runs the same weighted whole-image extraction that used
    /// to happen automatically on every render (see GpuToneGradient's own
    /// doc comment) as a one-shot action instead, overwriting whatever
    /// manual 明色/暗色 are currently set. Runs on the CURRENT photo buffer
    /// -- if none is loaded, does nothing (there's nothing to sample).</summary>
    private void ToneGradientAutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_photoPixelBuffer is not { } photo) return;
        if (!GpuToneGradient.TryDetectColors(photo.Pixels, photo.Stride, photo.Width, photo.Height,
                out var lightR, out var lightG, out var lightB, out var darkR, out var darkG, out var darkB))
        {
            return;
        }
        _undo.BeginChange();
        SetToneGradientLightColor(lightR, lightG, lightB);
        SetToneGradientDarkColor(darkR, darkG, darkB);
        _undo.CommitChange();
    }

    // ---- Eyedropper: click one of the 4 color rows' pipette buttons, then
    //      click anywhere on the preview to sample that pixel and apply it
    //      to whichever row's button was clicked. Only samples from the
    //      in-app preview image (not the whole screen) -- simplest to build
    //      and needs no OS-level screen-capture permissions. ----

    private enum ColorPickTarget { None, DropShadow, LightLeak, AvatarTint, PhotoTint, ToneGradientLight, ToneGradientDark }

    private ColorPickTarget _colorPickTarget = ColorPickTarget.None;

    /// <summary>Clicking the same row's eyedropper again cancels instead of
    /// re-arming it -- otherwise there'd be no way to back out short of
    /// clicking the preview and picking an unwanted color.</summary>
    private void BeginColorPick(ColorPickTarget target)
    {
        _colorPickTarget = _colorPickTarget == target ? ColorPickTarget.None : target;
        PreviewImage.Cursor = _colorPickTarget == ColorPickTarget.None ? Cursors.SizeAll : Cursors.Cross;
        if (_colorPickTarget == ColorPickTarget.None) HideColorPickMagnifier();
    }

    private void DropShadowEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.DropShadow);
    private void LightLeakEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.LightLeak);
    private void CompositeColorTintEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.AvatarTint);
    private void PhotoColorTintEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.PhotoTint);
    private void ToneGradientLightEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.ToneGradientLight);
    private void ToneGradientDarkEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.ToneGradientDark);

    /// <summary>Converts a screen position (relative to PreviewBorder) into a
    /// pixel coordinate on the actual source bitmap, inverting the same
    /// zoom/pan RenderTransform PreviewImage_MouseWheel's own comment derives
    /// ("a local point P maps to screen position O + zoom*(P-O) + Pan"):
    /// P = O + (screen - O - Pan) / zoom, then P (still in the unzoomed
    /// display-scaled space PreviewBorder.Width/Height live in) is divided by
    /// the display scale to land on a raw image pixel. Shared by the actual
    /// pick (TryPickColorAtClick) and the magnifier preview that tracks the
    /// cursor before the click happens.</summary>
    private bool TryImagePixelFromScreen(Point screen, out BitmapSource bmp, out int px, out int py)
    {
        px = py = 0;
        if (PreviewImage.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            bmp = null!;
            return false;
        }
        bmp = source;
        if (double.IsNaN(PreviewBorder.Width) || PreviewBorder.Width <= 0) return false;

        double originX = PreviewImage.ActualWidth / 2.0;
        double originY = PreviewImage.ActualHeight / 2.0;
        double localX = originX + (screen.X - originX - _previewPanX) / _previewZoom;
        double localY = originY + (screen.Y - originY - _previewPanY) / _previewZoom;

        double scale = PreviewBorder.Width / bmp.PixelWidth;
        px = (int)(localX / scale);
        py = (int)(localY / scale);
        return true;
    }

    private void TryPickColorAtClick(MouseButtonEventArgs e)
    {
        var target = _colorPickTarget;
        _colorPickTarget = ColorPickTarget.None;
        PreviewImage.Cursor = Cursors.SizeAll;
        HideColorPickMagnifier();

        if (!TryImagePixelFromScreen(e.GetPosition(PreviewBorder), out var bmp, out var px, out var py)) return;
        if (!TryGetPixelColor(bmp, px, py, out var r, out var g, out var b)) return;

        switch (target)
        {
            case ColorPickTarget.DropShadow: SetDropShadowColor(r, g, b); break;
            case ColorPickTarget.LightLeak: SetLightLeakColor(r, g, b); break;
            case ColorPickTarget.AvatarTint: SetCompositeColorTint(r, g, b); break;
            case ColorPickTarget.PhotoTint: SetPhotoColorTint(r, g, b); break;
            case ColorPickTarget.ToneGradientLight: SetToneGradientLightColor(r, g, b); break;
            case ColorPickTarget.ToneGradientDark: SetToneGradientDarkColor(r, g, b); break;
        }
    }

    private static bool TryGetPixelColor(BitmapSource source, int x, int y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight) return false;
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        b = pixel[0];
        g = pixel[1];
        r = pixel[2];
        return true;
    }

    // ---- Magnifier: a small zoomed-in loupe that follows the cursor while a
    //      color pick is armed, so the user can see exactly which pixel
    //      they're about to sample before clicking. An Adorner (not a Popup),
    //      same reasoning as ConnectorAdorner above -- stays confined to this
    //      window and isn't affected by any card's DropShadowEffect z-order
    //      quirk. Attached to PreviewBorder specifically (not PreviewImage):
    //      PreviewBorder has no RenderTransform of its own, so its adorner
    //      coordinate space matches e.GetPosition(PreviewBorder) directly,
    //      the same untransformed frame every other preview mouse handler
    //      already measures against. ----

    private const int MagnifierSourcePixels = 9; // odd: gives a true center pixel
    private const double MagnifierCellSize = 12; // each sampled pixel rendered this many DIPs wide
    private const double MagnifierDisplaySize = MagnifierSourcePixels * MagnifierCellSize;

    private Adorner? _colorPickMagnifierAdorner;
    private Border? _colorPickMagnifierRoot;
    private Image? _colorPickMagnifierImage;
    private TextBlock? _colorPickMagnifierHexText;

    private void EnsureColorPickMagnifier()
    {
        if (_colorPickMagnifierAdorner is not null) return;
        var layer = AdornerLayer.GetAdornerLayer(PreviewBorder);
        if (layer is null) return;

        _colorPickMagnifierImage = new Image
        {
            Width = MagnifierDisplaySize,
            Height = MagnifierDisplaySize,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_colorPickMagnifierImage, BitmapScalingMode.NearestNeighbor);

        // Outlines the exact center cell (the pixel that'll actually be
        // sampled) so "zoomed in enough to see individual pixels" doesn't
        // leave the user guessing which one of them is the real target.
        var centerHighlight = new Border
        {
            Width = MagnifierCellSize,
            Height = MagnifierCellSize,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(centerHighlight, (MagnifierDisplaySize - MagnifierCellSize) / 2.0);
        Canvas.SetTop(centerHighlight, (MagnifierDisplaySize - MagnifierCellSize) / 2.0);

        var imageCanvas = new Canvas { Width = MagnifierDisplaySize, Height = MagnifierDisplaySize, ClipToBounds = true };
        imageCanvas.Children.Add(_colorPickMagnifierImage);
        imageCanvas.Children.Add(centerHighlight);

        _colorPickMagnifierHexText = new TextBlock
        {
            Text = "#------",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(imageCanvas);
        stack.Children.Add(_colorPickMagnifierHexText);

        _colorPickMagnifierRoot = new Border
        {
            Padding = new Thickness(6),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        var canvas = new Canvas();
        canvas.Children.Add(_colorPickMagnifierRoot);

        _colorPickMagnifierAdorner = new ConnectorAdorner(PreviewBorder, canvas);
        layer.Add(_colorPickMagnifierAdorner);
    }

    /// <summary>A generous fallback for the loupe's own height before its
    /// first layout pass has run (ActualHeight still 0) -- padding(12) +
    /// image(108) + hex text row(~22) rounded up with headroom.</summary>
    private const double MagnifierEstimatedHeight = 150;

    /// <summary>screen is e.GetPosition(PreviewBorder) -- same frame the
    /// adorner renders in, so it can be used directly for Canvas.Left/Top.
    /// Anchored above-right of the cursor (not below-right) so the loupe
    /// itself never sits under the cursor it's magnifying.</summary>
    private void UpdateColorPickMagnifier(Point screen)
    {
        EnsureColorPickMagnifier();
        if (_colorPickMagnifierRoot is null || _colorPickMagnifierImage is null || _colorPickMagnifierHexText is null) return;

        if (!TryImagePixelFromScreen(screen, out var bmp, out var px, out var py))
        {
            _colorPickMagnifierRoot.Visibility = Visibility.Collapsed;
            return;
        }

        int half = MagnifierSourcePixels / 2;
        int cropX = Math.Clamp(px - half, 0, Math.Max(0, bmp.PixelWidth - MagnifierSourcePixels));
        int cropY = Math.Clamp(py - half, 0, Math.Max(0, bmp.PixelHeight - MagnifierSourcePixels));
        int cropW = Math.Min(MagnifierSourcePixels, bmp.PixelWidth);
        int cropH = Math.Min(MagnifierSourcePixels, bmp.PixelHeight);
        _colorPickMagnifierImage.Source = new CroppedBitmap(bmp, new Int32Rect(cropX, cropY, cropW, cropH));

        _colorPickMagnifierHexText.Text = TryGetPixelColor(bmp, px, py, out var r, out var g, out var b)
            ? ToHexColor(r, g, b)
            : "#------";

        _colorPickMagnifierRoot.Visibility = Visibility.Visible;
        double height = _colorPickMagnifierRoot.ActualHeight > 0 ? _colorPickMagnifierRoot.ActualHeight : MagnifierEstimatedHeight;
        Canvas.SetLeft(_colorPickMagnifierRoot, screen.X + 20);
        Canvas.SetTop(_colorPickMagnifierRoot, screen.Y - height - 20);
    }

    private void HideColorPickMagnifier()
    {
        if (_colorPickMagnifierRoot is not null) _colorPickMagnifierRoot.Visibility = Visibility.Collapsed;
    }

    // ---- ティント (color wash): two independent color pickers, one for the
    //      avatar-image look card and one for the photo look card -- same
    //      wheel/明度/RGB pattern as DropShadowColor above. The avatar side
    //      writes straight into _state.ColorTint* (OverlayState), so
    //      OverlayWindow's own live preview picks it up automatically the
    //      same way Brightness etc. already do; the photo side writes into
    //      local _photoColorTint* fields feeding PhotoAdjustments, same as
    //      every other photo-look slider. ----

    private bool _isDraggingAvatarColorTintWheel;

    private void CompositeColorTintButton_Click(object sender, RoutedEventArgs e)
    {
        CompositeColorTintWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncCompositeColorTintUI(_state.ColorTintR, _state.ColorTintG, _state.ColorTintB);
        _suppressEvents = false;
        CompositeColorTintPopup.IsOpen = true;
    }

    private void CompositeColorTintWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingAvatarColorTintWheel = true;
        CompositeColorTintWheel.CaptureMouse();
        UpdateCompositeColorTintFromWheelPosition(e.GetPosition(CompositeColorTintWheel));
    }

    private void CompositeColorTintWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAvatarColorTintWheel) return;
        UpdateCompositeColorTintFromWheelPosition(e.GetPosition(CompositeColorTintWheel));
    }

    private void CompositeColorTintWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingAvatarColorTintWheel = false;
        CompositeColorTintWheel.ReleaseMouseCapture();
    }

    private void UpdateCompositeColorTintFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _avatarColorTintHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _avatarColorTintSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_avatarColorTintHue, _avatarColorTintSat, CompositeColorTintValueSlider.Value / 100.0);
        SetCompositeColorTint(r, g, b);
    }

    private void CompositeColorTintValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_avatarColorTintHue, _avatarColorTintSat, CompositeColorTintValueSlider.Value / 100.0);
        SetCompositeColorTint(r, g, b);
    }

    private void CompositeColorTintRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetCompositeColorTint((byte)Math.Round(CompositeColorTintRSlider.Value), _state.ColorTintG, _state.ColorTintB);
    }

    private void CompositeColorTintGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetCompositeColorTint(_state.ColorTintR, (byte)Math.Round(CompositeColorTintGSlider.Value), _state.ColorTintB);
    }

    private void CompositeColorTintBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetCompositeColorTint(_state.ColorTintR, _state.ColorTintG, (byte)Math.Round(CompositeColorTintBSlider.Value));
    }

    private void CompositeColorTintRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(CompositeColorTintRBox.Text, out var v)) return;
        SetCompositeColorTint((byte)Math.Clamp(v, 0, 255), _state.ColorTintG, _state.ColorTintB);
    }

    private void CompositeColorTintGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(CompositeColorTintGBox.Text, out var v)) return;
        SetCompositeColorTint(_state.ColorTintR, (byte)Math.Clamp(v, 0, 255), _state.ColorTintB);
    }

    private void CompositeColorTintBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(CompositeColorTintBBox.Text, out var v)) return;
        SetCompositeColorTint(_state.ColorTintR, _state.ColorTintG, (byte)Math.Clamp(v, 0, 255));
    }

    private void SyncCompositeColorTintUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _avatarColorTintSat = s;
        if (s > 0.001) _avatarColorTintHue = h;

        CompositeColorTintRSlider.Value = r;
        CompositeColorTintRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        CompositeColorTintGSlider.Value = g;
        CompositeColorTintGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        CompositeColorTintBSlider.Value = b;
        CompositeColorTintBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        CompositeColorTintValueSlider.Value = v * 100;
        PositionColorWheelCursor(CompositeColorTintWheelCursor, _avatarColorTintHue, _avatarColorTintSat);
        CompositeColorTintPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        CompositeColorTintHexBox.Text = ToHexColor(r, g, b);
    }

    private void CompositeColorTintHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(CompositeColorTintHexBox.Text, out var r, out var g, out var b)) return;
        SetCompositeColorTint(r, g, b);
    }

    /// <summary>Guards SetCompositeColorTint/SetPhotoColorTint's mutual
    /// 一括調整 propagation below against infinite recursion (each one calls
    /// the other while linked) -- set for the duration of whichever call got
    /// there first, so the reciprocal call's own "propagate while linked"
    /// step is a no-op instead of calling back.</summary>
    private bool _suppressColorTintLinkSync;

    /// <summary>Single choke point for every way the avatar's tint color can
    /// change. Writes straight into _state (not a plain field like
    /// SetDropShadowColor's _dropShadowColor*), so no explicit
    /// ScheduleCompositeRender() call is needed here -- the blanket
    /// _state.PropertyChanged subscription already does that (and re-syncs
    /// this swatch/slider/box via RefreshFromState) for every other
    /// _state.* look field. While 一括調整 is on, the COLOR itself (not just
    /// the strength, which already shifts via ShiftPhotoIfLinked) mirrors to
    /// the photo side too, matching what "linked" means for every other
    /// look control.</summary>
    private void SetCompositeColorTint(byte r, byte g, byte b)
    {
        _state.ColorTintR = r;
        _state.ColorTintG = g;
        _state.ColorTintB = b;

        _suppressEvents = true;
        SyncCompositeColorTintUI(r, g, b);
        _suppressEvents = false;

        CompositeColorTintSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

        if (_lookLinked && !_suppressColorTintLinkSync)
        {
            _suppressColorTintLinkSync = true;
            SetPhotoColorTint(r, g, b);
            _suppressColorTintLinkSync = false;
        }
    }

    private void CompositeColorTintStrengthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(CompositeColorTintStrengthBox.Text, out var v)) return;
        v = Math.Clamp(v, 0, 100);
        double delta = v - _state.ColorTintStrength;
        _state.ColorTintStrength = v;
        ShiftPhotoIfLinked(ref _photoColorTintStrength, delta, 0, 100);
    }

    private void CompositeColorTintStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(CompositeColorTintStrengthSlider.Value);
        double delta = rounded - _state.ColorTintStrength;
        _state.ColorTintStrength = rounded;
        ShiftPhotoIfLinked(ref _photoColorTintStrength, delta, 0, 100);
    }

    private bool _isDraggingPhotoColorTintWheel;

    private void PhotoColorTintButton_Click(object sender, RoutedEventArgs e)
    {
        PhotoColorTintWheel.Source = GetColorWheelBitmap();
        _suppressEvents = true;
        SyncPhotoColorTintUI(_photoColorTintR, _photoColorTintG, _photoColorTintB);
        _suppressEvents = false;
        PhotoColorTintPopup.IsOpen = true;
    }

    private void PhotoColorTintWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPhotoColorTintWheel = true;
        PhotoColorTintWheel.CaptureMouse();
        UpdatePhotoColorTintFromWheelPosition(e.GetPosition(PhotoColorTintWheel));
    }

    private void PhotoColorTintWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingPhotoColorTintWheel) return;
        UpdatePhotoColorTintFromWheelPosition(e.GetPosition(PhotoColorTintWheel));
    }

    private void PhotoColorTintWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPhotoColorTintWheel = false;
        PhotoColorTintWheel.ReleaseMouseCapture();
    }

    private void UpdatePhotoColorTintFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _photoColorTintHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _photoColorTintSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_photoColorTintHue, _photoColorTintSat, PhotoColorTintValueSlider.Value / 100.0);
        SetPhotoColorTint(r, g, b);
    }

    private void PhotoColorTintValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_photoColorTintHue, _photoColorTintSat, PhotoColorTintValueSlider.Value / 100.0);
        SetPhotoColorTint(r, g, b);
    }

    private void PhotoColorTintRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetPhotoColorTint((byte)Math.Round(PhotoColorTintRSlider.Value), _photoColorTintG, _photoColorTintB);
    }

    private void PhotoColorTintGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetPhotoColorTint(_photoColorTintR, (byte)Math.Round(PhotoColorTintGSlider.Value), _photoColorTintB);
    }

    private void PhotoColorTintBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetPhotoColorTint(_photoColorTintR, _photoColorTintG, (byte)Math.Round(PhotoColorTintBSlider.Value));
    }

    private void PhotoColorTintRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoColorTintRBox.Text, out var v)) return;
        SetPhotoColorTint((byte)Math.Clamp(v, 0, 255), _photoColorTintG, _photoColorTintB);
    }

    private void PhotoColorTintGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoColorTintGBox.Text, out var v)) return;
        SetPhotoColorTint(_photoColorTintR, (byte)Math.Clamp(v, 0, 255), _photoColorTintB);
    }

    private void PhotoColorTintBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoColorTintBBox.Text, out var v)) return;
        SetPhotoColorTint(_photoColorTintR, _photoColorTintG, (byte)Math.Clamp(v, 0, 255));
    }

    private void SyncPhotoColorTintUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _photoColorTintSat = s;
        if (s > 0.001) _photoColorTintHue = h;

        PhotoColorTintRSlider.Value = r;
        PhotoColorTintRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        PhotoColorTintGSlider.Value = g;
        PhotoColorTintGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        PhotoColorTintBSlider.Value = b;
        PhotoColorTintBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        PhotoColorTintValueSlider.Value = v * 100;
        PositionColorWheelCursor(PhotoColorTintWheelCursor, _photoColorTintHue, _photoColorTintSat);
        PhotoColorTintPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        PhotoColorTintHexBox.Text = ToHexColor(r, g, b);
    }

    private void PhotoColorTintHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(PhotoColorTintHexBox.Text, out var r, out var g, out var b)) return;
        SetPhotoColorTint(r, g, b);
    }

    /// <summary>Single choke point for every way the photo's tint color can
    /// change -- a plain field (not OverlayState), so unlike
    /// SetCompositeColorTint this explicitly renders and does its own UI
    /// sync rather than relying on a PropertyChanged subscription. While
    /// 一括調整 is on, mirrors the color to the avatar side too (see
    /// SetCompositeColorTint's own comment on this same behavior).</summary>
    private void SetPhotoColorTint(byte r, byte g, byte b)
    {
        _photoColorTintR = r;
        _photoColorTintG = g;
        _photoColorTintB = b;

        _suppressEvents = true;
        SyncPhotoColorTintUI(r, g, b);
        _suppressEvents = false;

        PhotoColorTintSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();

        if (_lookLinked && !_suppressColorTintLinkSync)
        {
            _suppressColorTintLinkSync = true;
            SetCompositeColorTint(r, g, b);
            _suppressColorTintLinkSync = false;
        }
    }

    private void PhotoColorTintStrengthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(PhotoColorTintStrengthBox.Text, out var v)) return;
        v = Math.Clamp(v, 0, 100);
        double delta = v - _photoColorTintStrength;
        _photoColorTintStrength = v;
        if (_lookLinked && delta != 0) _state.ColorTintStrength = Math.Clamp(_state.ColorTintStrength + delta, 0, 100);
        _suppressEvents = true;
        PhotoColorTintStrengthSlider.Value = v;
        _suppressEvents = false;
        ScheduleCompositeRender();
    }

    private void PhotoColorTintStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(PhotoColorTintStrengthSlider.Value);
        _suppressEvents = true;
        PhotoColorTintStrengthBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEvents = false;
        if (rounded == _photoColorTintStrength) return;
        double delta = rounded - _photoColorTintStrength;
        _photoColorTintStrength = rounded;
        if (_lookLinked && delta != 0) _state.ColorTintStrength = Math.Clamp(_state.ColorTintStrength + delta, 0, 100);
        ScheduleCompositeRender();
    }

    /// <summary>PNG-encodes and writes a full-resolution VRChat-screenshot-
    /// sized composite -- slow enough (same order of cost as the recompose
    /// itself) that doing it synchronously on the UI thread visibly froze
    /// the window for the save's duration, the exact symptom
    /// RenderCompositePreview's own Task.Run split was meant to eliminate.
    /// Safe to encode off the UI thread because _lastComposite is always
    /// one of CompositeOverlayOntoPhoto/CropToAspect's own outputs, both of
    /// which are frozen right before they're returned (see their own doc
    /// comments) -- BitmapFrame.Create keeps that frozen state rather than
    /// needing the UI thread to re-wrap it.</summary>
    private async void SaveCompositeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastComposite is null) return;

        var defaultName = _photoPath is not null
            ? Path.GetFileNameWithoutExtension(_photoPath) + "_avasnap.png"
            : "avasnap_composite.png";
        var dialog = new SaveFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            FileName = defaultName,
            InitialDirectory = _photoPath is not null ? Path.GetDirectoryName(_photoPath) ?? "" : "",
        };
        if (dialog.ShowDialog() != true) return;

        var composite = _lastComposite;
        string path = dialog.FileName;
        SaveCompositeButton.IsEnabled = false;
        try
        {
            bool saved = await Task.Run(() =>
            {
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(composite));
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            });
            ShowCompositeSaveStatus(
                saved ? "保存しました: " + Path.GetFileName(path) : "保存に失敗しました。",
                success: saved);
        }
        finally
        {
            SaveCompositeButton.IsEnabled = true;
        }
    }

    /// <summary>Both the save result and (via <paramref name="success"/>:
    /// false) the unrelated "background photo failed to load" message share
    /// this one status TextBlock -- routing both through here means both
    /// get the same color-coded (green/rose) treatment instead of the
    /// success and failure cases looking visually identical. A successful
    /// save also gets a fading-in checkmark and auto-clears itself after a
    /// few seconds (failures don't: they stay until the user's next action,
    /// since a failure is something to notice and act on, not a fire-and-
    /// forget confirmation).</summary>
    private DispatcherTimer? _compositeSaveStatusClearTimer;

    private void ShowCompositeSaveStatus(string text, bool success)
    {
        _compositeSaveStatusClearTimer?.Stop();
        CompositeSaveStatusText.Text = text;
        CompositeSaveStatusText.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "AccentDarkBrush");

        if (!success)
        {
            CompositeSaveCheckmark.Visibility = Visibility.Collapsed;
            return;
        }

        CompositeSaveCheckmark.Visibility = Visibility.Visible;
        CompositeSaveCheckmark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

        _compositeSaveStatusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _compositeSaveStatusClearTimer.Tick -= CompositeSaveStatusClearTimer_Tick;
        _compositeSaveStatusClearTimer.Tick += CompositeSaveStatusClearTimer_Tick;
        _compositeSaveStatusClearTimer.Start();
    }

    private void CompositeSaveStatusClearTimer_Tick(object? sender, EventArgs e)
    {
        _compositeSaveStatusClearTimer!.Stop();
        ClearCompositeSaveStatus();
    }

    private void ClearCompositeSaveStatus()
    {
        _compositeSaveStatusClearTimer?.Stop();
        CompositeSaveStatusText.Text = "";
        CompositeSaveCheckmark.Visibility = Visibility.Collapsed;
    }

    // ---- Screenshot-watcher folder: defaults to VRChat's own default save
    //      location, but can be overridden manually. ----

    private void RefreshWatchFolderText()
    {
        WatchFolderText.Text = _screenshotWatcher.IsUsingManualFolder
            ? $"（手動指定）{_screenshotWatcher.ActiveFolder}"
            : $"（自動検出）{_screenshotWatcher.ActiveFolder}";
    }

    private void ChangeWatchFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "スクリーンショットフォルダを選択" };
        if (_screenshotWatcher.IsUsingManualFolder) dialog.FolderName = _screenshotWatcher.ActiveFolder;
        if (dialog.ShowDialog() == true)
        {
            _screenshotWatcher.ManualFolder = dialog.FolderName;
            RefreshWatchFolderText();
            RefreshRecentPhotosUI();
        }
    }

    private void ResetWatchFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _screenshotWatcher.ManualFolder = null;
        RefreshWatchFolderText();
        RefreshRecentPhotosUI();
    }
}
