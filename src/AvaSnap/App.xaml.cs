using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AvaSnap.Services;
using AvaSnap.Views;

namespace AvaSnap;

public partial class App : Application
{
    private OverlayState? _state;
    private UndoManager? _undoManager;
    private OverlayWindow? _overlayWindow;
    private ControlPanelWindow? _controlPanelWindow;
    private VrChatOscListener? _oscListener;
    private ScreenshotWatcherService? _screenshotWatcher;
    private ScreenshotNotificationManager? _screenshotNotifications;
    private UnityCameraGuideService? _unityCameraGuide;

    // Per-Monitor V2 DPI awareness, requested at runtime instead of via an
    // embedded app.manifest -- a custom ApplicationManifest broke this
    // project's self-contained/single-file apphost launch ("side-by-side
    // configuration is incorrect"), so this is the safer alternative. Must
    // run before any window/HwndSource is created, hence first line of
    // OnStartup. Non-fatal if it fails (e.g. older Windows) -- the window
    // just falls back to whatever DPI awareness the process already has.
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Handling Velopack's own special first-run/update/uninstall
        // invocations now happens in Program.cs's VelopackApp.Build().Run()
        // call, which runs before this Application even exists -- see its
        // own doc comment.
        try { SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); } catch (EntryPointNotFoundException) { }

        base.OnStartup(e);

        // All of AvaSnap's image processing (color adjustments, blur, drop
        // shadow, finishing effects, ...) runs on ComputeSharp/DX12 compute
        // shaders now -- there's no CPU fallback anymore (removed
        // deliberately: AvaSnap only makes sense running alongside VRChat,
        // which itself requires a DX11/12-capable GPU, so the CPU path was
        // dead code that no real user's machine would ever actually take,
        // while still doubling the maintenance/verification surface for
        // every effect). Failing loudly here, before any window opens, is
        // far better than launching into a UI where every single effect
        // silently does nothing with no indication why.
        if (GpuAvailability.Device is null)
        {
            MessageBox.Show(
                "AvaSnapの画像処理にはDirectX 12に対応したGPU/グラフィックドライバーが必要です。\n" +
                "この環境では利用できないため、起動できません。",
                "AvaSnap", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _state = new OverlayState();
        _undoManager = new UndoManager(_state);
        _oscListener = new VrChatOscListener();
        _oscListener.Start();

        // Watches for the JSON file Unity's own CameraCompositionGuideWindow
        // editor script exports (FOV/pitch/roll of whichever camera is being
        // tested there), so the overlay can draw a composition guide driven
        // by real numbers instead of manual FOV input -- see
        // UnityCameraGuideService's own doc comment. Start() is called
        // further below, AFTER OverlayWindow/ControlPanelWindow (both
        // constructed later) have subscribed to its events: Start() does an
        // immediate synchronous read of whatever's already on disk, and if
        // that fires DataUpdated before anyone's listening yet, that first
        // update is silently lost -- the guide would then sit blank until
        // the next file change, which (once Unity's exporter only writes
        // while focused) might not happen again for a while.
        _unityCameraGuide = new UnityCameraGuideService(Dispatcher);

        var saved = SettingsService.Load();

        // Applied before any window is constructed so DynamicResource
        // lookups inside ControlPanelWindow.xaml resolve to the right
        // palette from the very first frame instead of flashing light-
        // then-dark. Defaults to dark (not light) on a genuinely first-ever
        // launch (saved is null, no settings file yet) -- photo/video
        // editing tools (Lightroom, Photoshop, DaVinci Resolve, ...)
        // default to a dark chrome so it doesn't bias color/exposure
        // judgment against the photo being edited, and AvaSnap is squarely
        // in that category. Only the FIRST-EVER launch is affected: once a
        // settings file exists, saved.IsDarkMode (even if it deserializes
        // to its own false default from before this app had a theme
        // toggle) is used as-is, so no returning user's theme silently
        // flips out from under them.
        ThemeService.Apply(saved?.IsDarkMode ?? true);

        if (saved is not null)
        {
            if (!string.IsNullOrEmpty(saved.ImagePath) && File.Exists(saved.ImagePath))
            {
                _state.ImagePath = saved.ImagePath;
            }
            _state.Width = saved.Width > 0 ? saved.Width : _state.Width;
            _state.Height = saved.Height > 0 ? saved.Height : _state.Height;
            _state.RotationDegrees = saved.RotationDegrees;
            _state.Opacity = saved.Opacity > 0 ? saved.Opacity : _state.Opacity;
        }

        // Watches VRChat's screenshot folder (or a manual override) and pops a
        // non-intrusive toast when a new one appears, so photo compositing can
        // start right from the notification instead of needing a file picker.
        _screenshotWatcher = new ScreenshotWatcherService(Dispatcher);
        _screenshotWatcher.ManualFolder = saved?.ScreenshotFolderPath;
        _screenshotWatcher.Start();
        _screenshotNotifications = new ScreenshotNotificationManager(_screenshotWatcher, _oscListener);

        // Position/size are never persisted -- a screen coordinate (and a
        // camera-frame-fitted width/height) from a previous session are both
        // meaningless once VRChat's window has moved or its camera resolution
        // has changed. PerformReset() (called below, once ControlPanelWindow
        // and the avatar image both exist) re-fits both from scratch, the
        // same as clicking "位置をリセット" would -- an earlier version of
        // this only re-centered using the STALE persisted Width/Height,
        // which is why the image wasn't sized to the camera's width right
        // after opening the app.
        var existingVrchat = VRChatWindowService.FindVRChatWindow();

        _overlayWindow = new OverlayWindow(_state, _undoManager, _oscListener);
        _overlayWindow.Show();

        if (!string.IsNullOrEmpty(_state.ImagePath) && File.Exists(_state.ImagePath))
        {
            _overlayWindow.LoadImage(_state.ImagePath);
        }

        // If VRChat is already running, attach right away so the overlay rides
        // with it in Z-order from the start (also re-attached on every Reset).
        if (existingVrchat is not null)
        {
            _overlayWindow.AttachToOwner(existingVrchat.Value);
        }
        _overlayWindow.InitializeCameraVisibility();

        _controlPanelWindow = new ControlPanelWindow(_state, _overlayWindow, _undoManager, _oscListener, _screenshotWatcher, _unityCameraGuide);
        _controlPanelWindow.Closed += (_, _) => Shutdown();
        _controlPanelWindow.Show();
        _screenshotNotifications.PhotoSelected += (path, wasLandscape) => _controlPanelWindow.LoadPhotoForCompositeFromNotification(path, wasLandscape);

        // Now that ControlPanelWindow has subscribed to DataUpdated (the
        // only remaining subscriber -- OverlayWindow reads GuideManualFov/
        // Pitch/Roll off _state directly instead, see its own doc comment),
        // it's safe to start reading/watching the export file (see the
        // comment where _unityCameraGuide was constructed above).
        _unityCameraGuide.Start();

        if (saved?.RecentAvatarPaths is { } recentAvatars)
        {
            _controlPanelWindow.SetRecentAvatarPaths(recentAvatars);
        }

        if (!string.IsNullOrEmpty(saved?.PhotoPath) && File.Exists(saved.PhotoPath))
        {
            _controlPanelWindow.RestorePhotoSilently(saved.PhotoPath);
        }

        // Same owned-window Z-order trick as the overlay: bringing VRChat
        // forward should bring the control panel forward too, not leave it
        // buried behind the game window.
        if (existingVrchat is not null)
        {
            _controlPanelWindow.AttachToOwner(existingVrchat.Value);
            // Fits the overlay to the camera frame right away, same as
            // clicking "位置をリセット" -- see the comment above where
            // existingVrchat is found.
            _controlPanelWindow.PerformReset();
        }

        // Fire-and-forget, after both windows are already up, so a slow or
        // unreachable GitHub never delays startup. Only checks and (if the
        // user picks a version in AboutPanel's update section) downloads --
        // nothing is applied silently, per the "通知表示 -> ユーザーが選んで
        // 適用" UX this was asked for, unlike the old hand-rolled
        // UpdateService this replaced.
        _ = CheckForUpdateNotificationAsync();
    }

    private async Task CheckForUpdateNotificationAsync()
    {
        var info = await UpdateService.CheckForUpdatesAsync();
        if (info is not null)
        {
            _controlPanelWindow?.ShowUpdateAvailableNotification();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_state is not null)
        {
            SettingsService.Save(_state, _screenshotWatcher?.ManualFolder, _controlPanelWindow?.PhotoPath, _controlPanelWindow?.RecentAvatarPaths, ThemeService.IsDarkMode);
        }
        _oscListener?.Dispose();
        _screenshotWatcher?.Dispose();
        _unityCameraGuide?.Dispose();
        base.OnExit(e);
    }
}
