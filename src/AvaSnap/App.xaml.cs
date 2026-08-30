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

    // Per-Monitor V2 DPI awareness を app.manifest ではなく実行時に要求する
    // (自前 manifest は self-contained/single-file の apphost 起動を壊した)。
    // ウィンドウ/HwndSource 生成前に走る必要があるので OnStartup の先頭。失敗しても
    // 致命的ではない(古い Windows 等。プロセス既定の DPI awareness に落ちるだけ)。
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack の初回起動/更新/アンインストール処理は Program.cs の
        // VelopackApp.Build().Run() 側(この Application より前に走る)。
        try { SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); } catch (EntryPointNotFoundException) { }

        base.OnStartup(e);

        // AvaSnap の画像処理は全て ComputeSharp/DX12 コンピュートシェーダで、CPU
        // フォールバックは無い(VRChat と併用する前提で、VRChat 自体が DX11/12 GPU を
        // 要求するので CPU 経路は実質デッドコードだった)。全エフェクトが無言で無効な
        // UI へ入るより、ウィンドウを開く前にここで明示的に落とす方がよい。
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

        // Unity のエクスポート JSON(FOV/pitch/roll)を監視する。Start() は下の方、
        // OverlayWindow/ControlPanelWindow がイベント購読した「後」に呼ぶ ── Start() は
        // ディスク上の既存内容を同期読みするので、誰も聴いていないうちに DataUpdated が
        // 飛ぶと最初の1回を取りこぼす。
        _unityCameraGuide = new UnityCameraGuideService(Dispatcher);

        var saved = SettingsService.Load();

        // ウィンドウ生成前に適用して、DynamicResource が初フレームから正しい配色に
        // なるようにする(ライト→ダークのちらつき回避)。初回起動(設定ファイル無し)は
        // ダーク既定 ── 写真/動画編集ツールは編集対象の色/露出判断を偏らせないよう
        // ダーク基調が定番で、AvaSnap もその範疇。設定ファイルがあれば saved.IsDarkMode を
        // そのまま使うので、既存ユーザーのテーマが勝手に変わることはない。
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

        // VRChat のスクショフォルダ(または手動指定)を監視し、新規時に控えめなトースト。
        // ファイルピッカー無しで通知から合成を始められる。
        _screenshotWatcher = new ScreenshotWatcherService(Dispatcher);
        _screenshotWatcher.ManualFolder = saved?.ScreenshotFolderPath;
        _screenshotWatcher.Start();
        _screenshotNotifications = new ScreenshotNotificationManager(_screenshotWatcher);

        // 位置/サイズは保存しない ── 前セッションの画面座標もカメラ枠フィットの
        // 幅/高さも、VRChat 窓の移動やカメラ解像度変更で無意味になる。下の
        // PerformReset()(ControlPanelWindow とアバター画像が揃ったあと)が
        // 「位置をリセット」と同じく両方を組み直す。
        var existingVrchat = VRChatWindowService.FindVRChatWindow();

        _overlayWindow = new OverlayWindow(_state, _undoManager, _oscListener);
        _overlayWindow.Show();

        if (!string.IsNullOrEmpty(_state.ImagePath) && File.Exists(_state.ImagePath))
        {
            _overlayWindow.LoadImage(_state.ImagePath);
        }

        // VRChat が既に起動していれば即アタッチして、最初から Z 順で追従させる
        // (Reset ごとにも再アタッチ)。
        if (existingVrchat is not null)
        {
            _overlayWindow.AttachToOwner(existingVrchat.Value);
        }
        _overlayWindow.InitializeCameraVisibility();

        _controlPanelWindow = new ControlPanelWindow(_state, _overlayWindow, _undoManager, _oscListener, _screenshotWatcher, _unityCameraGuide);
        _controlPanelWindow.Closed += (_, _) => Shutdown();
        _controlPanelWindow.Show();
        _screenshotNotifications.PhotoSelected += path => _controlPanelWindow.LoadPhotoForComposite(path);

        // ControlPanelWindow が DataUpdated を購読したので、エクスポートファイルの
        // 読み取り/監視を始めて安全(上の _unityCameraGuide 生成箇所のコメント参照)。
        _unityCameraGuide.Start();

        if (saved?.RecentAvatarPaths is { } recentAvatars)
        {
            _controlPanelWindow.SetRecentAvatarPaths(recentAvatars);
        }

        if (!string.IsNullOrEmpty(saved?.PhotoPath) && File.Exists(saved.PhotoPath))
        {
            _controlPanelWindow.RestorePhotoSilently(saved.PhotoPath);
        }

        // オーバーレイと同じ owned-window の Z 順トリック: VRChat を前面にしたら
        // コントロールパネルも一緒に前面へ。
        if (existingVrchat is not null)
        {
            _controlPanelWindow.AttachToOwner(existingVrchat.Value);
            _controlPanelWindow.PerformReset(); // オーバーレイをカメラ枠にフィット(位置をリセットと同じ)
        }

        // 撃ちっぱなし。両ウィンドウが立った後なので、GitHub が遅くても起動を遅らせない。
        // 確認とダウンロードのみで、無言適用はしない(通知表示 → ユーザーが選んで適用)。
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
