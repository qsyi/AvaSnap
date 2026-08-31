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

/// <summary>写真ルックカードのスライダー値(明るさ〜黒レベル + 色被せ + 事前ぼかし)。
/// フィールド名は分割前の <see cref="CompositeSnapshot"/> のときと同じなので、
/// 参照側は頭に <c>.PhotoLook.</c> が付くだけ。</summary>
public sealed record CompositePhotoLook(
    double PhotoBrightness, double PhotoContrast, double PhotoSaturation,
    double PhotoVibrance, double PhotoTemperature, double PhotoTint, double PhotoHue,
    double PhotoHighlights, double PhotoShadows, double PhotoWhites, double PhotoBlacks,
    double PhotoColorTintStrength, byte PhotoColorTintR, byte PhotoColorTintG, byte PhotoColorTintB,
    double PhotoBlurAmount);

/// <summary>仕上げ(フィニッシュ)カードの効果量: グレイン/ビネット/ソフト/シャープ/
/// フェード/グロー/色収差/カラーブリード/走査線/明瞭度/ライトリーク/トーングラデ。</summary>
public sealed record CompositeFinish(
    double GrainAmount, double VignetteAmount,
    double SoftnessAmount, double SharpnessAmount,
    double FadeAmount, double GlowAmount,
    double ChromaticAberrationAmount, double ColorBleedAmount, double ScanlineAmount,
    double ClarityAmount, double LightLeakAmount, double LightLeakAngle, double LightLeakDistance,
    byte LightLeakColorB, byte LightLeakColorG, byte LightLeakColorR,
    double ToneGradientAmount, double ToneGradientRotation,
    byte ToneGradientLightR, byte ToneGradientLightG, byte ToneGradientLightB,
    byte ToneGradientDarkR, byte ToneGradientDarkG, byte ToneGradientDarkB);

/// <summary>ドロップシャドウの量/向き/距離/ぼかし/色/ブレンドモード。</summary>
public sealed record CompositeDropShadow(
    double DropShadowAmount, double DropShadowDirection, double DropShadowDistance, double DropShadowBlur,
    byte DropShadowColorB, byte DropShadowColorG, byte DropShadowColorR,
    ImageAdjustment.DropShadowBlendMode DropShadowBlendMode);

/// <summary>キャンバス切り抜き(アス比 + 幅/高さ% + 位置X/Y%)。</summary>
public sealed record CompositeCanvasCrop(
    double? CanvasAspectRatio, double CanvasCropOffsetX, double CanvasCropOffsetY,
    double CanvasCropWidthPercent, double CanvasCropHeightPercent);

/// <summary>アバター(紙立て看板)の配置: 写真ピクセル空間での位置/サイズ + 回転。</summary>
public sealed record CompositePlacement(
    double CompositePlaceX, double CompositePlaceY, double CompositePlaceWidth, double CompositePlaceHeight,
    double CompositeRotation);

/// <summary>「背景なしで作成」キャンバスの単色/グラデーション設定。</summary>
public sealed record CompositeBlankCanvas(
    byte BlankCanvasR, byte BlankCanvasG, byte BlankCanvasB,
    byte BlankCanvasR2, byte BlankCanvasG2, byte BlankCanvasB2,
    bool BlankCanvasGradientEnabled, double BlankCanvasGradientDirection, bool IsBlankCanvasActive);

/// <summary>Undo/Redo タイムラインに乗る「合成モード」全体のスナップショット。
/// 分割前は約70個の位置引数を1レコードに並べていたが、キャプチャ/適用/フラッシュ表の
/// 3箇所での可読性のためカード単位のサブレコードにまとめた。追加フィールドは
/// 該当サブレコード1つを直すだけで済む。<see cref="Decals"/> と
/// <see cref="PhotoBuffer"/> はどのカードにも属さないのでトップレベルのまま。</summary>
public sealed record CompositeSnapshot(
    CompositePhotoLook PhotoLook,
    CompositeFinish Finish,
    CompositeDropShadow DropShadow,
    CompositeCanvasCrop CanvasCrop,
    CompositePlacement Placement,
    EquatableArray<DecalEntrySnapshot> Decals,
    CompositeBlankCanvas BlankCanvas,
    CompositeMasks Masks,
    ImageAdjustment.PixelBuffer? PhotoBuffer);

public partial class ControlPanelWindow : Window
{
    private readonly OverlayState _state;
    private readonly OverlayWindow _overlayWindow;
    private readonly UndoManager _undo;
    private readonly VrChatOscListener _oscListener;
    private readonly ScreenshotWatcherService _screenshotWatcher;
    private readonly UnityCameraGuideService _unityCameraGuide;
    /// <summary>&gt; 0 の間だけ「コード側からコントロールへ値を流し込んでいる」
    /// 区間で、その間に飛ぶ ValueChanged / Checked 等のハンドラは早期 return する。
    /// 単純な bool ではなく深さカウンタ: 値を流し込むメソッドが別の同種メソッドを
    /// 呼び子側が終端で解除しても、外側の区間はまだ抜けていない、という入れ子を
    /// 正しく扱える(OverlayState の _batchDepth と同じ考え方。以前は
    /// SetGuideFovPitchRollDisplay 等でこの入れ子問題を手作業で避けていた)。
    /// 読み取りは <c>_suppressEvents</c> プロパティ、区間は
    /// <c>_suppressEventsDepth++</c> / <c>_suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1)</c>。</summary>
    private int _suppressEventsDepth;
    private bool _suppressEvents => _suppressEventsDepth > 0;

    public ControlPanelWindow(OverlayState state, OverlayWindow overlayWindow, UndoManager undo, VrChatOscListener oscListener, ScreenshotWatcherService screenshotWatcher, UnityCameraGuideService unityCameraGuide)
    {
        _state = state;
        _overlayWindow = overlayWindow;
        _undo = undo;
        _oscListener = oscListener;
        _screenshotWatcher = screenshotWatcher;
        _unityCameraGuide = unityCameraGuide;
        _suppressEventsDepth++;
        InitializeComponent();
        ThemeToggleButton.IsChecked = ThemeService.IsDarkMode;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        _defaultMinWidth = MinWidth;
        _defaultMinHeight = MinHeight;
        RebuildDecalStrip(); // populates just the non-removable アバター marker at startup
        RebuildMaskList();
        RefreshMaskChips();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        TitleBarVersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = 40;

        // FOVガイドの「Unity連携状況」表示。DataUpdated は UnityCameraGuideService の
        // Dispatcher.BeginInvoke 経由で来るのでここでのマーシャリングは不要。取得成功時は
        // GuideManualFov/Pitch/Roll へ直接書く(手入力と同じ)。OverlayWindow は
        // _state.PropertyChanged で自前に拾う。
        _unityCameraGuide.DataUpdated += data =>
        {
            ShowGuideFetchedNotification();
            _state.GuideManualFov = data.Fov;
            _state.GuideManualPitch = data.Pitch;
            _state.GuideManualRoll = data.Roll;
            _suppressEventsDepth++;
            RefreshGuideManualDisplay();
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        };

        _state.PropertyChanged += (_, e) => { RefreshFromState(e.PropertyName); ScheduleCompositeRender(); };
        RefreshFromState();
        RefreshWatchFolderText();
        RefreshPhotoLookUI();
        RefreshFinishUI();
        RefreshSkipAvatarUI();
        RefreshSplitGapRowEnabled();
        PreviewKeyDown += ControlPanelWindow_PreviewKeyDown;

        // XAML の IsChecked="True" ではなくここ(InitializeComponent 後)でセットする:
        // XAML パース中に同期発火すると、後方で宣言される LookLinkConnector が未代入で
        // EnsureLookLinkAdorner が null 参照で落ちた。ここなら全名前付き要素が存在済み。
        // この時点で CompositePanel はまだ Collapsed なのでラベル/アドーナー位置は
        // 無意味だが、ShowComposite の UpdateLinkedRowStyles が初回オープン時に直す。
        LookLinkToggle.IsChecked = true;

        // 合成モード専用フィールドを共有 undo タイムラインに畳み込む
        // (<see cref="CompositeSnapshot"/> 参照)。Ctrl+Z が写真ルック/グレイン/
        // ビネット/配置もカバーする。
        _undo.CaptureExtra = CaptureCompositeSnapshot;
        _undo.ApplyExtra = ApplyCompositeSnapshot;
        _undo.Applied += OnUndoRedoApplied;

        // VRChat 窓のサイズ変更(リサイズ/最大化/復元)や向きの変更で位置推定を
        // 自動再適用する ── 推定は両方に依存するので、どちらか変わった時点で旧位置は
        // 陳腐化する。再アタッチ(Z 順 + WinEventHook)は不要、既知の hwnd/rect で
        // 推定を再適用するだけ。
        _overlayWindow.ClientResized += OnVrChatClientResized;
        _oscListener.OrientationChanged += OnOscOrientationChanged;

        // オーバーレイは VRChat カメラが開いたと確認できるまで隠れている
        // (OverlayWindow.InitializeCameraVisibility/ApplyCameraOpenState 参照)。
        // 事情を知らないと戸惑うので、未確認の間は位置合わせモードで目立つバナーを出す。
        _oscListener.CameraModeChanged += (_) => Dispatcher.Invoke(RefreshAlignBanner);
        RefreshAlignBanner();

        // OSC は「変化時のみ」送信で、VRChat が後から起動するケースもあるため、
        // 位置合わせモード表示中は定期的に検知状況を見直す(ポート競合なら再 bind も試す)。
        _oscHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _oscHintTimer.Tick += OscHintTick;
        _oscHintTimer.Start();

        ShowHome();
    }

    /// <summary>コントロールパネルを VRChat 窓の owned window にする
    /// (<see cref="WindowOwnership"/> 参照)。ゲームに戻ったとき背後に埋もれず
    /// VRChat と一緒に前へ出る。</summary>
    public void AttachToOwner(IntPtr ownerHwnd) => WindowOwnership.SetOwner(this, ownerHwnd);

    private readonly DispatcherTimer _oscHintTimer;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private void OscHintTick(object? sender, EventArgs e)
    {
        if (AlignPanel.Visibility != Visibility.Visible) return; // バナーは位置合わせモードだけ
        if (_oscListener.BindFailed) _oscListener.Start();       // ポートが空いたかもしれない。再試行
        RefreshAlignBanner();
    }

    /// <summary>位置合わせモード上部バナーの3状態切り替え:
    /// (1) OSC 未検知 / ポート競合 → OSC 有効化のヒント + 手順ボタン、
    /// (2) OSC は届いているがカメラ未オープン(またはまだ不明)→「カメラを開いて」、
    /// (3) カメラ オープン → 非表示。どのケースもライブオーバーレイは隠れている。</summary>
    private void RefreshAlignBanner()
    {
        if (_oscListener.IsCameraOpen == true)
        {
            CameraClosedBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool vrchatRunning = VRChatWindowService.FindVRChatWindow() is not null;
        // BindFailed は即断。「無反応」は起動直後の取りこぼしで空振りしないよう数秒待つ。
        bool oscUndetected = _oscListener.BindFailed
            || (vrchatRunning && !_oscListener.HasReceivedAnyMessage && (DateTime.UtcNow - _startedUtc).TotalSeconds > 6);

        CameraClosedBannerText.Text = oscUndetected
            ? (_oscListener.BindFailed ? "OSCポートを他のアプリが使用中です" : "VRChatのOSCが検知されません")
            : "VRChatのカメラを開いてください";
        OscSetupGuideButton.Visibility = oscUndetected ? Visibility.Visible : Visibility.Collapsed;
        CameraClosedBanner.Visibility = Visibility.Visible;
    }

    // ---- ナビゲーション: 小さなホーム画面から2モードを選ぶ。各画面は中身に合わせた
    //      サイズ(合成モードは写真プレビューのぶんホーム/位置合わせより大きく開く)。 ----

    // HomeSettingsPanel はホーム右上に浮く(⚙️ ボタンの下にアンカー、HomePanel の
    // 中央 StackPanel の中ではない)。開いても窓はリサイズせず、下のモードカードに
    // 普通のドロップダウンのように重なるだけ。
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

        // ホームは位置合わせ用ではないので、ライブオーバーレイがここで VRChat の上に
        // 乗っても邪魔なだけ。SetManuallyHidden(単なる Hide() ではない)は、ホーム表示中に
        // カメラが開いても抑制を維持する(同メソッドの doc 参照)。
        _overlayWindow.SetManuallyHidden(true);
    });

    // ⚙️ はホームでしか意味がない(監視フォルダはモード別設定ではない)ので、他の
    // Show*/EnterCompact はこれとドロップダウンを両方隠す。どちらも HomePanel の
    // Visibility トグルの中に無いため、放置すると他モード上に浮いてしまう。
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

        // ShowHome/EnterCompact の抑制を解除し、VRChat の現在のカメラ状態へ再同期する。
        _overlayWindow.SetManuallyHidden(false);
        RefreshAlignBanner();
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
            DropGuideOverlay.Visibility = Visibility.Collapsed; // ドラッグ中断で残っていた場合の保険
            // このモードに入るたび再スキャン(キャッシュしない)。監視フォルダの今の中身を常に映す。
            RefreshRecentPhotosUI();
            // 作業領域ぎりぎりまで(少しだけ小さく)。このモードは写真プレビュー +
            // 2列コントロールで一番広さの恩恵がある。
            Width = SystemParameters.WorkArea.Width - 60;
            Height = SystemParameters.WorkArea.Height - 60;
            PinToRightEdge();

            // 実レンダー(下)はここでは行わず遅延する: 大きい写真では本当に遅く
            // (距離変換ベースのエッジぼかしが特に)、WithRedrawSuspended はこの
            // アクション全体が終わるまで再描画を止めるので、ここでローディング表示しても
            // 描画されない。
            ShowCompositeLoading();
        });

        // 上の WithRedrawSuspended が終わり一度再描画された(ローディング表示)ので、
        // 実レンダーを直後にキューする。ApplicationIdle は、WPF 自身の Render 優先度の
        // レイアウト/描画パスを含む全上位アイテムが片付いてから走るので、重い計算の
        // 開始前に窓(ローディング含む)が確実に表示済みになる。
        // ここで 一括調整 のラベルハイライト/コネクターアドーナーも(再)確立する:
        // EnsureLookLinkAdorner/PositionLookLinkConnector は CompositePanel への実際の
        // レイアウトパスを必要とし、Collapsed の間は起きない ── 初回オープンまで
        // Collapsed なのでここで行う。
        Dispatcher.InvokeAsync(() =>
        {
            UpdateLinkedRowStyles();
            FinishMatchRender();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>CompositeLoadingPanel(スピナー + テキスト)を表示して回転を開始する。
    /// <see cref="HideCompositeLoading"/> と対。Visibility を直接いじらず必ず両方を
    /// 呼ぶ ── そうしないとパネルを隠したあともスピナーが回り続ける。</summary>
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

    /// <summary>モードごとに Width が違うが WPF のリサイズは左端を固定するので、
    /// Left を動かさず幅だけ広げると窓が画面右端からはみ出す。幅が変わるたびに
    /// 右端を起動時と同じ「作業領域端から 20px」の位置に再アンカーする。画面より
    /// 広いモード用に作業領域の左端でクランプもする。</summary>
    private void PinToRightEdge()
    {
        double left = SystemParameters.WorkArea.Right - Width - 20;
        if (left < SystemParameters.WorkArea.Left) left = SystemParameters.WorkArea.Left;
        Left = left;
    }

    // ---- モード切替は Width/Height/Left と複数パネルの Visibility を個別に書き、
    //      それぞれが窓の移動/リサイズ/再描画を起こしてチラつく。WM_SETREDRAW で
    //      一連の変更中は再描画を止め、最終状態になってから1回だけ再描画させる。 ----

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

    private const string FeedbackFormUrl =
        "https://docs.google.com/forms/d/e/1FAIpQLSfHQFGMUtwCCMca225BGKxqHQY5_mbW58dzVLEOiurkNq3xxA/viewform?usp=publish-editor";

    /// <summary>ブラウザでGoogleフォームを開くだけ -- アプリ側は何も収集/
    /// 送信しない。</summary>
    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(FeedbackFormUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // 既定ブラウザの起動に失敗。ほかにできることはない。
        }
    }

    /// <summary>PatchNotesText は初回オープン時に一度だけ PATCHNOTES.md から読む。
    /// WPF リソースとして埋め込んであり(csproj 参照)ネットワーク無しで読める。
    /// ライセンス/サードパーティ表記は別の LicensePanel(ShowLicense 参照)。</summary>
    private void ShowAbout() => WithRedrawSuspended(() =>
    {
        if (!_aboutContentLoaded)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            AboutVersionText.Text = $"バージョン {version.Major}.{version.Minor}.{version.Build}";
            PatchNotesText.Text = LoadEmbeddedText("Assets/PATCHNOTES.md");
            _aboutContentLoaded = true;
        }

        // タイトルバーの更新ボタン自体が通知(ShowUpdateAvailableNotification 参照)。
        // ここで開く更新セクションを今まさに見ているので不要。
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

    /// <summary>LicenseText/ThirdPartyNoticesText は初回オープン時に一度だけ
    /// LICENSE.md / THIRD-PARTY-NOTICES.md から読む。WPF リソースとして埋め込んであり
    /// (csproj 参照)、中の一部の表記(SIL OFL、MIT)が要求するオフライン閲覧に対応する。</summary>
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

    /// <summary>バックグラウンドの CheckForUpdatesAsync が実行中ビルドより新しいものを
    /// 見つけたとき App.xaml.cs から呼ばれる。このボタン自体が通知(他にバッジは無い)。
    /// ユーザーが押してバージョン情報を開き、自分でバージョンを選ぶまで何も
    /// ダウンロードしない(UpdateApplyButton_Click 参照)。</summary>
    public void ShowUpdateAvailableNotification() => TitleBarUpdateButton.Visibility = Visibility.Visible;

    /// <summary>作業領域を手で埋める代替ではなく本物の WindowState.Maximized を使う。
    /// ネイティブ挙動が2つ無料で付く: 最大化窓のタイトルバードラッグで復元しつつ
    /// カーソル追従(DragMove() 組込)と、端ドラッグリサイズ。RestoreBounds が
    /// 最大化前のサイズ/位置を自動追跡する。</summary>
    private void TitleBarMaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>WindowState がどう変わっても最大化ボタンのアイコンを同期する
    /// (上のボタン、タイトルバーのダブルクリック、Aero Snap、タスクバー右クリック)。</summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- WindowStyle="None" + WindowState="Maximized" の既知の WPF バグ: この
    //      フックが無いと、最大化したボーダーレス窓が作業領域ではなくモニタ全域
    //      (タスクバーを覆う)まで広がる。WM_GETMINMAXINFO を横取りして実際の
    //      作業領域を自前で埋めるのが定番の対処 ── OnSourceInitialized が
    //      Win32 ハンドル確定後にこのフックを入れる。 ----

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

    // WM_NCCALCSIZE: ResizeMode="CanResize" は WindowStyle="None" でも細い
    // 非クライアント境界を確保し、そこに DWM が既定の白背景を描く(上端のヘアライン)。
    // 窓全体をクライアント領域扱いにすると消えるが、OS の端ヒットテスト(端ドラッグ
    // リサイズ)も一緒に消える ── それは下の WM_NCHITTEST で戻す。
    private const int WM_NCCALCSIZE = 0x0083;

    // WM_NCACTIVATE: DWM の既定処理はアクティブ/非アクティブ時に非クライアント境界を
    // 再描画する(他窓から戻ったときの白フラッシュ)。TRUE を返して handled にすると
    // その既定再描画をまるごとスキップする。
    private const int WM_NCACTIVATE = 0x0086;

    // WM_NCHITTEST: 上の WM_NCCALCSIZE で非クライアント領域ゼロにしたため OS が
    // リサイズ端を判定できなくなる。外周数ピクセルを自前で HTLEFT/HTRIGHT/HTTOP/
    // HTBOTTOM/隅に分類し、OS のネイティブなリサイズドラッグループへ戻す。
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
            // 端ではない ── 既定処理(HTCLIENT)へ。タイトルバーの DragMove()
            // ドラッグや各ボタンのクリック処理はそのまま。
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

    /// <summary>リリースフィードに公開中の全バージョンで UpdateVersionCombo を埋め
    /// (新しい順、先頭を選択)、UpdateStatusText を更新する。AboutPanel を開くたびに
    /// 実行し(ライセンステキストと違いキャッシュしない)、今公開されている実状を映す。</summary>
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
        UpdateVersionCombo.SelectedIndex = 0; // 新しい順、先頭を既定で選択
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
            // DownloadAndApplyAsync 内の ApplyUpdatesAndRestart がプロセスを
            // 再起動する ── 成功時はここから先は実行されない。
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

    // ---- カスタムタイトルバー: WindowStyle="None" でネイティブのを消したので
    //      ドラッグと閉じるは自前。ドラッグ開始前に、押下が Button(閉じるボタン)
    //      上で始まったかを確認する ── ボタン側で PreviewMouseLeftButtonDown を
    //      handled にする方式はボタン自身の click 判定まで潰したので、ここで発生元を
    //      見る方式にした。 ----

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

    // ---- 共有 Slider テンプレート(Window.Resources 参照): PART_Track の
    //      どこかで押してそのままドラッグしたら、初回クリックで1回跳ぶだけでなく
    //      ずっとマウスに追従させたい。Track.ValueFromPoint が座標→値の計算を
    //      するので、ここはマウスをキャプチャして現在座標を渡し続けるだけ。 ----

    private bool _sliderTrackDragging;
    private Track? _sliderTrackDraggingTrack;
    private Point _sliderTrackPendingPoint;
    private bool _sliderTrackPendingUpdate;

    private void SliderTrack_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Track track) return;
        // Thumb 自体へのクリックは触らない ── 組込機構で正しくドラッグされる。
        if (e.OriginalSource is DependencyObject source && HasAncestorOrSelf<Thumb>(source)) return;

        _sliderTrackDragging = true;
        _sliderTrackDraggingTrack = track;
        track.CaptureMouse();
        track.Value = track.ValueFromPoint(e.GetPosition(track));
        CompositionTarget.Rendering += SliderTrackDragging_Rendering;
        // これが無いと、未処理のイベントが下の Decrease/IncreaseRepeatButton
        // (透明スタイルだが実体は RepeatButton)へ届き、今セットした値の上に
        // さらに LargeChange ステップを発火する。両者が Value を奪い合うと
        // トラックドラッグが重く感じる。
        e.Handled = true;
    }

    /// <summary>ここでは最新のマウス座標を記録するだけ ── 実際の Value 代入
    /// (と連鎖する RefreshFromState / ScheduleCompositeRender など)は下の
    /// CompositionTarget.Rendering 経由で1フレームにつき最大1回。高ポーリングの
    /// マウスは UI が再描画できる以上の WM_MOUSEMOVE を送ってくる。</summary>
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

    // ---- コンパクトモード: 窓全体を右上隅の小ウィジェットに縮める。ライブ調整中に
    //      VRChat を覆わないため。元のモードと窓位置を覚えて「元に戻す」で正確に復元する。 ----

    private enum PanelMode { Align, Composite }

    private PanelMode _preCompactMode;
    private double _preCompactLeft, _preCompactTop;
    private readonly double _defaultMinWidth;
    private readonly double _defaultMinHeight;

    private void EnterCompact(PanelMode mode) => WithRedrawSuspended(() =>
    {
        // 小さな隅ウィジェットを最大化状態で見せない。
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
        // コンパクトモードには専用の拡大機構(ExpandButton)があるので最大化ボタンは不要。
        TitleBarMaximizeButton.Visibility = Visibility.Collapsed;
        HideHomeSettings();
        CompactModeText.Text = mode == PanelMode.Align ? "位置合わせモード" : "レタッチモード";

        MinWidth = 260;
        // 元の想定 +4: タイトルバーを Windows 標準のキャプション高に合わせて
        // 28→32px にしたぶん、CompactPanel の狭いコンテンツ行を圧迫していた。
        MinHeight = 104;
        Width = 300;
        Height = 116;
        PinToRightEdge();
        Top = 20;

        // 位置合わせオーバーレイはアバターを VRChat カメラUIに合わせている間しか
        // 役に立たない。コンパクトウィジェットに畳んだら VRChat の上に乗って邪魔なだけ
        // なので、ExpandButton_Click で戻すまで隠す。SetManuallyHidden(単なる Hide()
        // ではない)は、最小化中にカメラを開き直しても隠したままにする。
        _overlayWindow.SetManuallyHidden(true);
    });

    /// <summary>位置合わせ/合成の両モード共通(タイトルバーへ移設)。置き換え前の
    /// モード別ボタンと違い、どちらのモードが開いているか自分で判定する。</summary>
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

    private void OnVrChatClientResized(IntPtr hwnd, Rect region) => ApplyPositionEstimate(hwnd, region);

    private void OnOscOrientationChanged(bool landscape) => Dispatcher.Invoke(() =>
    {
        if (_overlayWindow.FollowedHwnd is { } hwnd && _overlayWindow.FollowedClientRect is { } region)
        {
            ApplyPositionEstimate(hwnd, region);
        }
        // 未アタッチ。再配置するものは無い ── 次の手動リセットでこの向きを拾う。
    });

    private void ControlPanelWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _colorPickTarget != ColorPickTarget.None)
        {
            BeginColorPick(_colorPickTarget);
            e.Handled = true;
            return;
        }

        // 選択中デカールの矢印移動 / Delete 削除。値編集コントロールに
        // フォーカスがある間は横取りしない(スライダーの値変更・キャレット移動を優先)。
        if (_isDecalPlacementModeActive && _placingDecal is { } editDecal && !IsTextEntryFocused())
        {
            if (e.Key is Key.Delete or Key.Back)
            {
                RemoveDecal(editDecal);
                e.Handled = true;
                return;
            }
            double nudge = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            double ndx = e.Key switch { Key.Left => -nudge, Key.Right => nudge, _ => 0 };
            double ndy = e.Key switch { Key.Up => -nudge, Key.Down => nudge, _ => 0 };
            if (ndx != 0 || ndy != 0)
            {
                NudgeDecal(editDecal, ndx, ndy);
                e.Handled = true;
                return;
            }
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

    /// <summary>Ctrl+Z/Ctrl+Y は複数のルック調整値をまとめて飛べる(Match ボタンの
    /// 結果を取り消すなど)。これは FinishMatchRender/ShowCompositeLoading を用意した
    /// のと同じフル解像度の再合成コストを踏むので、同じ扱いをしないと Undo/Redo が
    /// 即適用されたあと UI スレッドが無言で固まる。実際のジャンプを
    /// _isCompositeDragging で包み(途中レンダーを最終品質扱いしない)、確定後の1回は
    /// FinishMatchRender を使う ── コストがあるのは合成モード表示中だけなのでそのときだけ。</summary>
    private void PerformUndoOrRedo(bool isRedo)
    {
        bool showsComposite = CompositePanel.Visibility == Visibility.Visible;
        if (showsComposite) ShowCompositeLoading();

        _isCompositeDragging = true;
        if (isRedo) _undo.Redo(); else _undo.Undo();
        _isCompositeDragging = false;

        if (showsComposite) FinishMatchRender();
    }

    /// <summary>OverlaySnapshot の各フィールドについて、どの行をフラッシュするかの表
    /// (OnUndoRedoApplied 参照)。約20本の「if (before.X != after.X) FlashRow(...)」を
    /// 表に置き換えたもの。名前付き XAML 要素をキャプチャするので遅延構築する。
    /// 複数フィールドをまとめて見るエントリは値タプルを返す。</summary>
    private List<(Func<OverlaySnapshot, object?> Key, FrameworkElement Row)>? _overlayFlashTable;

    private List<(Func<OverlaySnapshot, object?> Key, FrameworkElement Row)> BuildOverlayFlashTable() => new()
    {
        // 位置合わせモードには X/Y/Width/Height/RotationDegrees の行がもう無く
        // (OverlayWindow のドラッグハンドルが唯一のUI)、このカードに FlashRow 対応の
        // アンカーも無いので、この5つは undo/redo でフラッシュしない ── ライブ
        // オーバーレイが復元位置へ跳ぶこと自体がフィードバックになる。
        (s => s.Opacity, OpacitySlider),
        (s => s.EdgeBlurRadius, CompositeEdgeBlurSlider),
        // Brightness..Blacks は今は合成側にしかスライダーが無いのでそれだけフラッシュする。
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

    /// <summary><see cref="BuildOverlayFlashTable"/> と同じ考え方の、
    /// CompositeSnapshot 用の表。</summary>
    private List<(Func<CompositeSnapshot, object?> Key, FrameworkElement Row)>? _compositeFlashTable;

    private List<(Func<CompositeSnapshot, object?> Key, FrameworkElement Row)> BuildCompositeFlashTable() => new()
    {
        (s => s.PhotoLook.PhotoBrightness, PhotoBrightnessSlider),
        (s => s.PhotoLook.PhotoContrast, PhotoContrastSlider),
        (s => s.PhotoLook.PhotoSaturation, PhotoSaturationSlider),
        (s => s.PhotoLook.PhotoVibrance, PhotoVibranceSlider),
        (s => s.PhotoLook.PhotoTemperature, PhotoTemperatureSlider),
        (s => s.PhotoLook.PhotoTint, PhotoTintSlider),
        (s => s.PhotoLook.PhotoHue, PhotoHueSlider),
        (s => s.PhotoLook.PhotoHighlights, PhotoHighlightsSlider),
        (s => s.PhotoLook.PhotoShadows, PhotoShadowsSlider),
        (s => s.PhotoLook.PhotoWhites, PhotoWhitesSlider),
        (s => s.PhotoLook.PhotoBlacks, PhotoBlacksSlider),
        (s => (s.PhotoLook.PhotoColorTintStrength, s.PhotoLook.PhotoColorTintR, s.PhotoLook.PhotoColorTintG, s.PhotoLook.PhotoColorTintB), PhotoColorTintStrengthSlider),
        (s => s.PhotoLook.PhotoBlurAmount, PhotoBlurSlider),
        (s => s.Finish.GrainAmount, GrainSlider),
        (s => s.Finish.VignetteAmount, VignetteSlider),
        (s => s.Finish.SoftnessAmount, SoftnessSlider),
        (s => s.Finish.SharpnessAmount, SharpnessSlider),
        (s => s.Finish.FadeAmount, FadeSlider),
        (s => s.Finish.GlowAmount, GlowSlider),
        (s => s.Finish.ChromaticAberrationAmount, ChromaticAberrationSlider),
        (s => s.Finish.ColorBleedAmount, ColorBleedSlider),
        (s => s.Finish.ScanlineAmount, ScanlineSlider),
        (s => s.Finish.ClarityAmount, ClaritySlider),
        (s => (s.Finish.LightLeakAmount, s.Finish.LightLeakAngle, s.Finish.LightLeakDistance, s.Finish.LightLeakColorB, s.Finish.LightLeakColorG, s.Finish.LightLeakColorR), LightLeakSlider),
        (s => s.Finish.ToneGradientAmount, ToneGradientSlider),
        (s => s.Finish.ToneGradientRotation, ToneGradientDirectionSlider),
        (s => s.DropShadow.DropShadowAmount, DropShadowSlider),
        (s => s.DropShadow.DropShadowDirection, DropShadowDirectionSlider),
        (s => s.DropShadow.DropShadowDistance, DropShadowDistanceSlider),
        (s => s.DropShadow.DropShadowBlur, DropShadowBlurSlider),
        (s => (s.DropShadow.DropShadowColorB, s.DropShadow.DropShadowColorG, s.DropShadow.DropShadowColorR), DropShadowColorButton),
        (s => s.DropShadow.DropShadowBlendMode, DropShadowBlendModeCombo),
        // 切り抜き幅/位置X/Y の専用行はもう無い(インタラクティブな切り抜きモード
        // ドラッグが置き換えた)ので、この2つは undo/redo でフラッシュしない。
        (s => s.CanvasCrop.CanvasAspectRatio, CanvasAspectCombo),
        // X/Y/幅/回転 の専用行も無い ── 5プロパティともトグルの行を1グループとして
        // フラッシュする。
        (s => (s.Placement.CompositePlaceX, s.Placement.CompositePlaceY, s.Placement.CompositePlaceWidth, s.Placement.CompositePlaceHeight, s.Placement.CompositeRotation), AvatarPlacementModeToggle),
    };

    /// <summary>実際の Undo/Redo ジャンプ(UndoManager.Applied)に反応し、値が
    /// 変わった行をフラッシュする(OverlaySnapshot と CompositeSnapshot Extra の
    /// 両方をカバー)。行が見つからないフィールドもあるので、消えていく undo/redo
    /// アイコンは無条件で表示する。</summary>
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

    /// <summary>1行(<paramref name="anchor"/> の Parent の Grid)を消えていく
    /// ティントで一瞬ハイライトする。行のコンテンツの背面に挿入するのでラベル/
    /// スライダーは上で読める。<paramref name="anchor"/> は通常 Slider だが、
    /// 行の Grid に直接載る FrameworkElement なら何でもよい。</summary>
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

    /// <summary>UndoRedoReactionBadge をフェードイン→短く保持→フェードアウト。
    /// 変更を適用した Undo/Redo すべてで表示する(フラッシュ対象行の有無に関わらず)。</summary>
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

    /// <summary>下の各 RefreshFromState セクションが依存する OverlayState
    /// プロパティ。全 _state 変更で無条件に走らせず、そのセクションだけを回す。
    /// <paramref name="changedProperty"/> が null は「不明/多数変更」
    /// (初回呼び出し、LoadImageFile の全リロード、OverlayState のバッチ通知)を意味し、
    /// 「全部更新」として扱う。</summary>
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
        _suppressEventsDepth++;
        try
        {
            bool all = changedProperty is null;

            if (all || PositionRefreshPropertyNames.Contains(changedProperty))
            {
                // ここで同期する X/Y/幅/高さ/回転(度) はもう無い ── 不透明度/表示
                // だけが位置合わせモード専用UIを持つ。
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
                // 合成パネルのミラー PNG コントロール(下の「PNG look」ハンドラ参照)。
                // 同じ _state をここからも同期する。ルックスライダー(Brightness..Blacks)
                // と境界ぼかしは合成側にしか無いので、存在しない位置合わせ側の Box.Text を
                // ミラーせず _state から直接読む。
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
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        }
    }

    private void LoadImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = ImageFileDialogFilter,
            Title = "アバター画像を選択",
        };
        if (dialog.ShowDialog() == true)
        {
            LoadImageFile(dialog.FileName);
        }
    }

    private void LoadImageFile(string path)
    {
        _undo.BeginChange();
        try
        {
            _overlayWindow.LoadImage(path);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or ArgumentException)
        {
            _undo.CommitChange();
            if (CompositePanel.Visibility == Visibility.Visible)
                ShowCompositeSaveStatus("画像を読み込めませんでした。", success: false);
            else
                ResetStatusText.Text = "画像を読み込めませんでした。";
            return;
        }
        PerformReset();
        _undo.CommitChange();
        RefreshFromState();

        // 別のアバター画像は縦横比/サイズが違うので、写真を選び直したときと同様に
        // 自動配置の推定もやり直す。
        _compositePlacementInitialized = false;
        // アバターを明示的に(再)読み込むのは、合成に戻したいという明確な意思表示。
        // 以前の「アバターなしで進める」選択を上書きする。
        _compositeSkipAvatar = false;
        RefreshSkipAvatarUI();
        ScheduleCompositeRender();
        AddRecentAvatarPath(path);
    }

    // ---- 最近のアバター / 最近の写真: ControlPanelWindow.RecentFiles.cs に分離。 ----

    // ---- ドラッグ&ドロップ: レタッチモードでは画像ドラッグ中に DropGuideOverlay を出し、
    //      ドロップ位置(上部左=アバター / 上部右=背景 / 下部=1枚レタッチ)で読み込み先を
    //      分ける。他モード / ガイド外へのドロップは従来どおりアバター画像として読み込む。
    //      アバター/背景とも形式は限定しない(WPF BitmapImage がデコードできる画像なら可)。 ----

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff" };

    private const string ImageFileDialogFilter =
        "画像ファイル|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff|すべてのファイル|*.*";

    private enum DropZone { Avatar, Background, Single }

    private static readonly Brush DropZoneIdleBorder = MakeFrozen(Color.FromRgb(0x9A, 0x9A, 0xB0));
    private static readonly Brush DropZoneIdleFill = MakeFrozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DropZoneActiveBorder = MakeFrozen(Color.FromRgb(0x7B, 0x87, 0xE0));
    private static readonly Brush DropZoneActiveFill = MakeFrozen(Color.FromArgb(0x55, 0x4A, 0x58, 0xC4));

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // WPF はドラッグを中止/画面外で離した時などに DragLeave/Drop を必ずしも
    // 発火しないので、ガイドが出っぱなしになる。OLE のドラッグループは実行中
    // 定期的に DragOver を呼ぶため、最後の DragOver から一定時間途切れたら
    // 「ドラッグ終了」とみなして隠す。
    private DispatcherTimer? _dragEndWatch;
    private DateTime _lastDragOverUtc;

    private void MarkDragActive()
    {
        _lastDragOverUtc = DateTime.UtcNow;
        if (_dragEndWatch is null)
        {
            _dragEndWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _dragEndWatch.Tick += (_, _) =>
            {
                // DragOver は基本マウス移動時のみ届く。静止ドラッグでの誤消しを避けつつ
                // 中止/画面外リリースからの復帰は速いよう、やや長めに待つ。
                if ((DateTime.UtcNow - _lastDragOverUtc).TotalMilliseconds < 700) return;
                HideDropGuide();
            };
        }
        _dragEndWatch.Start();
    }

    private void HideDropGuide()
    {
        _dragEndWatch?.Stop();
        DropGuideOverlay.Visibility = Visibility.Collapsed;
        HighlightDropZone(null);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool hasImage = GetDroppedImagePath(e) is not null;
        if (hasImage && CompositePanel.Visibility == Visibility.Visible && !_isMaskEditModeActive)
        {
            DropGuideOverlay.Visibility = Visibility.Visible;
            MarkDragActive();
        }
        e.Effects = hasImage ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave はバブリングするので窓内の境界越えでも飛ぶ。カーソルが窓の外へ
        // 出たときだけ隠す(窓内に残っていれば watchdog が終了を検知する)。
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) return;
        HideDropGuide();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        HideDropGuide();
        if (GetDroppedImagePath(e) is { } path)
        {
            LoadImageFile(path); // ガイド外 / 位置合わせモード: 従来どおりアバター画像
        }
    }

    private void DropGuide_DragOver(object sender, DragEventArgs e)
    {
        DropGuideOverlay.Visibility = Visibility.Visible;
        MarkDragActive();
        HighlightDropZone(HitTestDropZone(e));
        e.Effects = GetDroppedImagePath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropGuide_DragLeave(object sender, DragEventArgs e) => HighlightDropZone(null);

    private void DropGuide_Drop(object sender, DragEventArgs e)
    {
        var zone = HitTestDropZone(e);
        HideDropGuide();
        if (GetDroppedImagePath(e) is not { } path) return;
        switch (zone)
        {
            case DropZone.Avatar: LoadImageFile(path); break;
            case DropZone.Background: LoadPhotoForComposite(path); break;
            case DropZone.Single: LoadSingleImageForRetouch(path); break;
        }
        e.Handled = true;
    }

    /// <summary>座標の基準はレイアウト済みが保証される PreviewHost。DropGuideOverlay は
    /// ドラッグ中に Visible にした直後だと ActualWidth/Height が 0 のことがあり、
    /// それを基準にすると常に下段(Single)判定になってしまう。</summary>
    private DropZone HitTestDropZone(DragEventArgs e)
    {
        double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
        if (w <= 0 || h <= 0) return DropZone.Background;
        var p = e.GetPosition(PreviewHost);
        if (p.Y >= h * 0.6) return DropZone.Single;
        return p.X < w / 2 ? DropZone.Avatar : DropZone.Background;
    }

    private void HighlightDropZone(DropZone? active)
    {
        SetZoneLook(DropZoneAvatar, active == DropZone.Avatar);
        SetZoneLook(DropZoneBackground, active == DropZone.Background);
        SetZoneLook(DropZoneSingle, active == DropZone.Single);
    }

    private static void SetZoneLook(Border zone, bool on)
    {
        zone.BorderBrush = on ? DropZoneActiveBorder : DropZoneIdleBorder;
        zone.Background = on ? DropZoneActiveFill : DropZoneIdleFill;
    }

    /// <summary>「合成せず1枚でレタッチ」: ドロップ画像を背景写真として読み込み、
    /// アバター無し(<see cref="_compositeSkipAvatar"/>)に切り替える。以降は通常の
    /// 写真レタッチと同じパイプライン(色調補正 / 仕上げ / 切り抜き / デカール / マスク)が
    /// 1枚に対して働き、そのまま保存できる。</summary>
    private void LoadSingleImageForRetouch(string path)
    {
        LoadPhotoForComposite(path);
        if (_photoPixelBuffer is null) return; // 読み込み失敗
        _compositeSkipAvatar = true;
        RefreshSkipAvatarUI();
        _ = RenderCompositePreview();
    }

    private static string? GetDroppedImagePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return Array.Find(files, f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>手動リセット: Z 順アタッチ + 移動追従を確立してから位置推定を適用する
    /// (自動トリガーは発火時点で既にアタッチ済みなのでここだけ必要)。新規画像の
    /// 読み込み直後(<see cref="LoadImageFile"/>)と、VRChat 起動済みならアプリ起動時
    /// (App.OnStartup)にも自動で呼ばれる ── 新しい画像や起動直後は、前セッションの
    /// 残り位置ではなくカメラ幅にフィットした既知の良い位置から始めるべき。</summary>
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

        var clientRect = VRChatWindowService.GetClientRectInDips(hwnd.Value);
        if (clientRect is not { Width: > 0, Height: > 0 } region)
        {
            ResetStatusText.Text = "VRChatにアタッチしました（ウィンドウ位置の取得に失敗、位置は変更していません）。";
            return;
        }
        ApplyPositionEstimate(hwnd.Value, region);
    }

    /// <summary>現在の向きの推定カメラ枠矩形にオーバーレイを置き、読み込んだ画像の
    /// 縦横比をその枠にフィットさせる。向き未報告なら現サイズで再センタリングに
    /// フォールバック。既知の hwnd/rect を受け取る ── 現在値を持つ呼び出し元は
    /// FindVRChatWindow() や AttachToOwner をやり直さない。手動/自動どちらの
    /// 呼び出しも undo で包む(入れ子 Begin/Commit が並行編集との重なりを正しく扱う)。
    /// <paramref name="region"/> は DIP。</summary>
    private void ApplyPositionEstimate(IntPtr hwnd, Rect region)
    {
        _undo.BeginChange();
        // 下の X/Y/Width/Height は1つの論理移動。バッチで OverlayState 通知を
        // 4回でなく1回にまとめる(OverlayState.BeginBatch 参照)。ここは手動ボタン
        // だけでなく VRChat 窓のリサイズ/移動ごとに走るので効く。
        _state.BeginBatch();

        // 向き不明でも既知時と同じ枠矩形式を使い、汎用の再センタリングではなく
        // 横向き(多い方)を仮定する ── 横向きの推測の方が中央よりも近い可能性が高い。
        bool? knownOrientation = _oscListener.IsLandscape;
        bool landscape = knownOrientation ?? true;
        var (frameLeft, frameTop, frameWidth, frameHeight) = VRChatWindowService.ComputeCameraFrameRect(region, landscape);

        var nativeSize = _overlayWindow.ImageNativeSize;
        if (nativeSize is { Width: > 0, Height: > 0 } size)
        {
            // 引き伸ばさず、縦横比を保って枠にフィットさせ、枠内で中央寄せ。
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

        // 「成功」メッセージではなくクリアする ── オーバーレイが所定位置へ動くこと
        // 自体が成功の表れで、以前のエラー文(「VRChatウィンドウが見つかりませんでした」
        // 等)も消す。
        ResetStatusText.Text = "";

        _state.EndBatch();
        _undo.CommitChange();
    }

    /// <summary>テキストボックスのフォーカスベース undo グルーピング: フォーカス
    /// 取得〜喪失の間の変更が1 undo ステップになる(オーバーレイのドラッグ
    /// グルーピングと同じ原理)。</summary>
    private void Field_GotFocus(object sender, RoutedEventArgs e) => _undo.BeginChange();

    private void Field_LostFocus(object sender, RoutedEventArgs e) => _undo.CommitChange();

    /// <summary>スライダー専用のマウスベース undo グルーピング: WPF Slider は
    /// ドラッグを離してもキーボードフォーカスを保つため、フォーカスベースだけだと
    /// 編集が「開いたまま」になる。Begin/Commit を実際のマウス down/up に結び付けて
    /// ドラッグ終了の瞬間に確定する。入れ子 Begin/Commit のおかげで
    /// Field_GotFocus/LostFocus と同時に発火しても安全。</summary>
    private void Field_MouseDown(object sender, MouseButtonEventArgs e) => _undo.BeginChange();

    private void Field_MouseUp(object sender, MouseButtonEventArgs e) => _undo.CommitChange();

    // ---- 明るさ/コントラスト/彩度/自然な彩度/色温度/色かぶり/色相
    //      (アバター側と写真ルック側の両コピー)はドラッグ開始/終了を報告し、
    //      ドラッグ中の途中レンダーを保存品質として確定しない
    //      (OverlayWindow.SetColorDragging と _isCompositeDragging 参照)。
    //      境界ぼかしは下の別ハンドラを使う。 ----

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

    // ---- 境界ぼかし: 今は他スライダー同様ドラッグ中もライブプレビューする。
    //      GpuAvatarEdgeBlur で GPU 実行なので離すまで固める旧処理は不要。 ----

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
        _state.EdgeBlurRadius = 5; // 0 ではなく既定値。ぼかし無しは中立の基準ではない
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

    /// <summary>許容範囲内なら最寄りのターゲットへ値を引き寄せる磁石スナップ
    /// (ハードステップではない)。他の位置では自由に動き、意味のある値
    /// (中央、90度、半分)の近くでだけそこにピタッと収まる。</summary>
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
            _suppressEventsDepth++;
            OpacitySlider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        }
        _state.Opacity = snapped / 100.0;
    }

    // ---- PNG ルック(境界ぼかし/明るさ/コントラスト/彩度): 共有 _state。位置合わせ
    //      パネルと合成パネルのミラーコントロールの両方から編集でき、sender ベース
    //      なので同じハンドラが両コピーを処理する。代入直後の _state.PropertyChanged で
    //      RefreshFromState が両側のボックス/スライダーを再同期するので、ここで
    //      兄弟コントロールへ書き戻す必要はない。 ----

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

    /// <summary>UnityCameraGuideService のエクスポートファイルを選択した状態で
    /// エクスプローラーを開く。接続状況バッジだけでは分からないとき、ファイルの
    /// 有無や最終更新を直接確認できる。</summary>
    private void OpenGuideFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{UnityCameraGuideService.FilePath}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // エクスプローラーの起動に失敗。ほかにできることはない。
        }
    }

    private const double UnityIntegrationGuideOffscreenY = -400;

    /// <summary>UnityIntegrationGuidePanel を上からスライドインさせる
    /// (Visibility は既に切替済み、ここはアニメーションのみ)。
    /// ShowUndoRedoReaction 等と同じ DependencyProperty への BeginAnimation で、
    /// Opacity の代わりに TranslateTransform.Y。</summary>
    private void OpenUnityIntegrationGuideButton_Click(object sender, RoutedEventArgs e)
    {
        UnityIntegrationGuideOverlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(UnityIntegrationGuideOffscreenY, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        UnityIntegrationGuideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseUnityIntegrationGuideButton_Click(object sender, RoutedEventArgs e) => CloseUnityIntegrationGuide();

    private void UnityIntegrationGuideScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseUnityIntegrationGuide();

    private void CloseUnityIntegrationGuide()
    {
        var anim = new DoubleAnimation(0, UnityIntegrationGuideOffscreenY, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => UnityIntegrationGuideOverlay.Visibility = Visibility.Collapsed;
        UnityIntegrationGuideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // ---- OSC有効化ガイド: 上の Unity連携ガイドと同じスライドイン方式。位置合わせモードの
    //      バナー(RefreshAlignBanner)の「OSCを有効にする手順」ボタンから開く。 ----

    private void OpenOscSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        OscSetupGuideOverlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(UnityIntegrationGuideOffscreenY, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        OscSetupGuideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void CloseOscSetupGuideButton_Click(object sender, RoutedEventArgs e) => CloseOscSetupGuide();

    private void OscSetupGuideScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseOscSetupGuide();

    private void CloseOscSetupGuide()
    {
        var anim = new DoubleAnimation(0, UnityIntegrationGuideOffscreenY, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => OscSetupGuideOverlay.Visibility = Visibility.Collapsed;
        OscSetupGuideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>qsToolBoxのインストールページ(https://qsyi.github.io/qsToolbox/install)
    /// にある「Add to VCC」ボタンと全く同じvcc://ディープリンク -- 同じ
    /// vpm-reposのリスティング(index.json)にAvaSnap連携も同梱されている
    /// ので、リポジトリURLも変える必要がない。VCCがインストールされていて
    /// vcc://プロトコルハンドラが登録されていれば、クリックでVCCが起動し
    /// リポジトリ追加の確認ダイアログが出る。</summary>
    private void AddToVccButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "vcc://vpm/addRepo?url=https%3A%2F%2Fqsyi.github.io%2Fvpm-repos%2Findex.json")
            { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // VCC 未インストール / vcc:// プロトコル未登録。ほかにできることはない。
        }
    }

    /// <summary>UnityCameraGuideService.DataUpdatedが発火するたび(＝取得
    /// ボタンへの応答がUnityから届くたび)に「取得しました」を数秒表示して
    /// 消える一時通知 -- ShowUndoRedoReactionと同じ
    /// フェードイン/ホールド/フェードアウトのキーフレームパターン。継続的な
    /// 接続状態表示ではないので、押していない間は常にCollapsed。</summary>
    private void ShowGuideFetchedNotification()
    {
        UnityConnectionBadge.Visibility = Visibility.Visible;

        var keyFrames = new DoubleAnimationUsingKeyFrames();
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2500))));
        keyFrames.Completed += (_, _) => UnityConnectionBadge.Visibility = Visibility.Collapsed;
        UnityConnectionBadge.BeginAnimation(OpacityProperty, keyFrames);
    }

    /// <summary>「取得」ボタン: UnityのCameraCompositionGuideExporterへ
    /// スナップショットをリクエストする(設定不要、Unityを開いてさえいれば
    /// バックグラウンドで自動応答)。送りっぱなし(応答を待たない) --
    /// Unity Editorが起動していなければ何も起きず、何も表示は変わらない
    /// (取得できた時だけShowGuideFetchedNotificationが一時通知を出す)。
    /// Unity Editorがフォーカスを失っていると応答が遅れることがあるため、
    /// XAML側に常時表示のヒントテキストを置いてある。</summary>
    private void RequestGuideButton_Click(object sender, RoutedEventArgs e) => _unityCameraGuide.RequestUpdate();

    private void GuideVisibleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _state.GuideVisible = GuideVisibleToggle.IsChecked == true;
    }

    /// <summary>自前の _suppressEvents 管理は無し ── 呼び出し元が既に抑制区間の
    /// 中にいる(RefreshFromState、またはコンストラクタの DataUpdated ハンドラ)。</summary>
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
            _suppressEventsDepth++;
            slider.Value = snapped;
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        }
        double rounded = Math.Round(snapped);
        double delta = rounded - _state.Blacks;
        _state.Blacks = rounded;
        ShiftPhotoIfLinked(ref _photoBlacks, delta, -100, 100);
    }

    // ---- 合成モード: 写真を選び(手動またはスクショ監視トースト経由)、位置合わせ済み
    //      PNG をその上に合成する。PNG のルック(境界ぼかし/明るさ/コントラスト/彩度)は
    //      位置合わせモードと同じ共有 _state。下の写真側の明るさ/コントラスト/彩度は
    //      独立した合成モード専用の状態で、別々にも一緒にも調整できる。 ----

    private ImageAdjustment.PixelBuffer? _photoPixelBuffer;
    private string? _photoPath;
    private double _photoBrightness, _photoContrast, _photoSaturation;
    private double _photoVibrance, _photoTemperature, _photoTint, _photoHue;
    private double _photoHighlights, _photoShadows, _photoWhites, _photoBlacks;

    private double _photoColorTintStrength;
    private byte _photoColorTintR = 255, _photoColorTintG = 255, _photoColorTintB = 255;

    /// <summary>アバター側/写真側それぞれのティント色ホイールで最後に選んだ
    /// 色相/彩度。RGB だけでは彩度0(グレー/白)のとき色相を表せないので、
    /// _dropShadowHue/Sat と同様に RGB とは別にキャッシュする。</summary>
    private double _avatarColorTintHue, _avatarColorTintSat;
    private double _photoColorTintHue, _photoColorTintSat;

    /// <summary>写真全体のぼかし(0..100、0 = オフ、既定0)。境界ぼかし(アバター
    /// 切り抜きの縁だけ)と違い背景写真全体を柔らかくする。アバター合成前に適用する
    /// のでアバター自体はシャープなまま。アバター側に対応が無いので 一括調整 リンク
    /// からは除外。</summary>
    private double _photoBlurAmount;

    private double _grainAmount, _vignetteAmount;

    /// <summary>0..100、0 = オフ。PhotoBlurAmount(写真のみ、アバター合成前)と違い、
    /// この2つは最終合成全体(アバター + 写真)へ仕上げパスとして適用する。範囲は
    /// グレイン/ビネットと同じ。ImageAdjustment.ApplySoftness/ApplySharpness 参照。</summary>
    private double _softnessAmount, _sharpnessAmount;

    /// <summary>0..100、0 = オフ。グレイン/ビネット/ソフト/シャープと同じ
    /// 「合成全体・仕上げパス」範囲。ImageAdjustment.ApplyFade/ApplyGlow 参照。</summary>
    private double _fadeAmount, _glowAmount;

    /// <summary>0..100、0 = オフ。他と同じ「合成全体・仕上げパス」範囲の VHS 風
    /// アーティファクト。ImageAdjustment.ApplyChromaticAberration/ApplyColorBleed/
    /// ApplyScanlines 参照。</summary>
    private double _chromaticAberrationAmount, _colorBleedAmount, _scanlineAmount;

    /// <summary>0..100、0 = オフ。他と同じ「合成全体・仕上げパス」範囲。
    /// ImageAdjustment.ApplyClarity/ApplyLightLeak 参照。</summary>
    private double _clarityAmount, _lightLeakAmount;

    /// <summary>0..360 度、時計回り、0 = 真下。_dropShadowDirection/
    /// _toneGradientRotation と同じ自由角度規約(ImageAdjustment.ApplyLightLeak 参照)。</summary>
    private double _lightLeakAngle = 225;

    /// <summary>0..1、光のアンカーが中心から LightLeakDial の縁へどれだけ寄るか。
    /// 1(既定)は常に縁の従来動作、0 は中央。ImageAdjustment.ApplyLightLeak 参照。</summary>
    private double _lightLeakDistance = 1.0;

    /// <summary>色選択がドロップシャドウと同じホイール+RGB ポップアップになったので、
    /// 旧「暖色」プリセットの RGB を既定にする。</summary>
    private byte _lightLeakColorB = 60, _lightLeakColorG = 160, _lightLeakColorR = 255;

    /// <summary>ライトリーク色ポップアップの最後の有効な色相/彩度のキャッシュ。
    /// _dropShadowHue/_dropShadowSat と同じ理由。</summary>
    private double _lightLeakHue, _lightLeakSat;

    /// <summary>0..100、0 = オフ。他と同じ「合成全体・仕上げパス」範囲。
    /// ImageAdjustment.ApplyToneGradient 参照 ── ライトリークの固定ティントと違い、
    /// 合成全体の重み付き明暗トーン(アバター + 背景写真)から作った線形グラデを
    /// スクリーン合成する。</summary>
    private double _toneGradientAmount;

    /// <summary>0..360 度、時計回り、0 = 真下。_dropShadowDirection/_lightLeakAngle と
    /// 同じ規約。規約上の 0 ではなく 180(真上、上が明るい)を既定にする ──
    /// ドットが暗ではなく明を指す理由は ImageAdjustment.GpuToneGradient 参照。</summary>
    private double _toneGradientRotation = 180;

    /// <summary>グラデの両端色 ── 既定は白/黒(GpuToneGradient.TryDetectColors の
    /// GPU 無しフォールバックと一致)。明色/暗色 で編集でき、自動判定 ボタン
    /// (ToneGradientAutoDetectButton_Click)で現在の写真から都度更新する。</summary>
    private byte _toneGradientLightR = 255, _toneGradientLightG = 255, _toneGradientLightB = 255;
    private byte _toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB;

    /// <summary>グラデ2色それぞれのホイールポップアップで最後に選んだ色相/彩度。
    /// _dropShadowHue/_dropShadowSat と同じキャッシュ理由(彩度0で RGB だけでは
    /// 色相を表せない)。</summary>
    private double _toneGradientLightHue, _toneGradientLightSat;
    private double _toneGradientDarkHue, _toneGradientDarkSat;

    /// <summary>0..100、0 = オフ。アバターのシルエットをオフセット/ぼかし/着色して
    /// 複製する(ImageAdjustment.ApplyDropShadow 参照)。アバター読み込み時のみ効果が
    /// あるので、RenderCompositePreview のアバター無し分岐ではスキップされる。</summary>
    private double _dropShadowAmount;

    /// <summary>0..360 度、時計回り、0 = 真下。_toneGradientRotation と同じ規約。</summary>
    private double _dropShadowDirection;

    /// <summary>フル解像度の写真ピクセル単位のオフセット距離。</summary>
    private double _dropShadowDistance = 100;

    private double _dropShadowBlur = 10;

    /// <summary>既定は黒、ドロップシャドウの定番色。</summary>
    private byte _dropShadowColorB, _dropShadowColorG, _dropShadowColorR;

    /// <summary>影の色が下の写真とどう混ざるか ── ImageAdjustment.DropShadowBlendMode
    /// 参照。既定は Multiply(従来からの唯一のルック)。</summary>
    private ImageAdjustment.DropShadowBlendMode _dropShadowBlendMode = ImageAdjustment.DropShadowBlendMode.Multiply;

    /// <summary>直近の実レンダーのフル品質「アフター」合成(ドラッグ中は凍結)。
    /// CompareSlider の位置に関わらず、保存が実際に書き出すのはこれ。</summary>
    private WriteableBitmap? _lastComposite;

    /// <summary>_lastComposite の「ビフォー」版: 配置/回転は同じだが、どちらの
    /// レイヤーにもルック調整も仕上げ効果も適用しない(RenderCompositePreview 参照)。
    /// 素材が無ければ null。</summary>
    private WriteableBitmap? _lastBeforeComposite;

    /// <summary>CompareSlider の値、0..100。0(既定)はプレビュー全体に
    /// _lastComposite(アフター)を表示。値を上げるとビフォー/アフターの分割線が
    /// 右へ進み、左から _lastBeforeComposite が現れる。</summary>
    private double _beforeAfterSplit;

    private void CompareSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _beforeAfterSplit = CompareSlider.Value;
        // 必要になった初回にだけ計算する(全 RenderCompositePreview 呼び出しでは
        // なく)。ComputeBeforeComposite 参照。
        if (_beforeAfterSplit > 0 && _lastBeforeComposite is null)
        {
            _lastBeforeComposite = ComputeBeforeComposite();
        }
        UpdateComparisonPreview(_lastComposite, _lastBeforeComposite);
    }

    /// <summary>CompareSlider の現在位置に応じて <paramref name="after"/>/
    /// <paramref name="before"/> をマージして表示する。RenderCompositePreview の
    /// ビフォー/アフター生成とは分けてあるので、CompareSlider のドラッグはこの
    /// 軽いマージだけをやり直す。</summary>
    private void UpdateComparisonPreview(WriteableBitmap? after, WriteableBitmap? before)
    {
        if (after is null) return;
        PreviewImage.Source = _beforeAfterSplit > 0 && before is not null
            ? ImageAdjustment.MergeBeforeAfter(before, after, _beforeAfterSplit / 100.0)
            : after;
        UpdateCompareSplitLine();
    }

    /// <summary>マージの分割位置と同じ x に CompareSplitLine を置く。
    /// PreviewBorder.Width が「画像の表示幅」を兼ねる(SizePreviewToImage 参照)ので、
    /// 割合がそのまま Margin.Left になる。Thumb の視覚中心の補正はあえてしない ──
    /// 補正すると Value=100 でも縁に約8px の「アフター」が残る。端で Thumb が線から
    /// 数 px ずれるのはこの種のスライダーの通常の見た目。</summary>
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

    /// <summary>「一括調整」トグルがアバタールックと写真ルックをリンクしている間 true。
    /// 一方を動かすともう一方を同じ差分だけずらす(一致させるのではない)ので、
    /// 元々あった差(例: 写真をわざと明るめ)は保たれる。IsClickThrough のような
    /// モードトグルで、それ自体は undo 対象ではなく値も動かさない ── 実際にリンク
    /// スライダーをドラッグ/入力したときだけ動く(ShiftPhotoIfLinked 参照)。
    /// 既定はオン(一緒に動かす方がよく使われるため)。</summary>
    private bool _lookLinked = true;

    private void LookLinkToggle_Changed(object sender, RoutedEventArgs e)
    {
        _lookLinked = LookLinkToggle.IsChecked == true;
        UpdateLinkedRowStyles();
    }

    /// <summary>一括調整 がオンの間、共有パラメータのラベル(アバタールックカードと
    /// 写真ルックカードの両方)をハイライトし、2カード間にコネクターバー+アイコンを
    /// 表示して、どのスライダーが連動中かを見て分かるようにする。境界ぼかし/ぼかし/
    /// 仕上げ は対応が無いので対象外。ラベルの周りに要素を足さずテキスト色だけ変える
    /// ので、2カード間の行ぞろえを乱さない。</summary>
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

    /// <summary>バー+アイコンの見た目を一度だけ作り、LookLinkConnector の
    /// AdornerLayer に付ける。Popup ではなく Adorner を使う: 角丸のため
    /// AllowsTransparency="True" にした Popup は実質「常に最前面の窓」になり、
    /// デスクトップ上の全窓の上に描かれてしまった。Adorner は Popup と同じく
    /// 通常のビジュアルツリーの上に描くが、この窓内に閉じる(AdornerDecorator の
    /// AdornerLayer 経由)。
    /// 元の問題: 各「Card」Border は DropShadowEffect を持ち、Effect 付き要素の
    /// サブツリーは別の合成レイヤーで描かれ、重なる兄弟に対して Panel.ZIndex や
    /// 宣言順を確実には尊重しない ── そのためアイコンがカードの白背景の下に
    /// 潜っていた。</summary>
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

    /// <summary>AvatarBrightnessRow(共有7行の先頭)と AvatarLookDivider
    /// (境界ぼかし の前の境目)が実際に描かれた位置を LookLinkConnector 座標系で
    /// 測り、LookLinkBar/LookLinkIcon をちょうどその範囲に配置する。両カードの行は
    /// そろっているので、写真カードを測らなくてもアバターカードの位置で
    /// 「共有ブロックの始点と終点」が分かる。</summary>
    private void PositionLookLinkConnector()
    {
        if (_lookLinkBar is null || _lookLinkIcon is null) return;

        double top = AvatarBrightnessRow.TranslatePoint(new Point(0, 0), LookLinkConnector).Y;
        double bottom = AvatarLookDivider.TranslatePoint(new Point(0, 0), LookLinkConnector).Y;
        double height = Math.Max(0, bottom - top);

        // LookLinkConnector は 12px の溝カラム。3px バーと 22px アイコンを
        // 左端ではなく中央に置く。
        Canvas.SetLeft(_lookLinkBar, (12 - _lookLinkBar.Width) / 2.0);
        Canvas.SetTop(_lookLinkBar, top);
        _lookLinkBar.Height = height;

        // カラム全体の高さではなくバーの範囲に対して中央寄せ。
        Canvas.SetLeft(_lookLinkIcon, (12 - _lookLinkIcon.Width) / 2.0);
        Canvas.SetTop(_lookLinkIcon, top + height / 2 - _lookLinkIcon.Height / 2);
    }

    /// <summary>任意の UIElement(ここではバー+アイコンの Canvas)を Adorner として
    /// ホストする。付与先要素の小さいサイズではなく大きな固定 Measure/Arrange
    /// サイズを使う ── Adorner は付与先の外に描く必要があり、Canvas は
    /// Canvas.Left/Top 配置の子を自身のサイズで制約しないため。</summary>
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

    /// <summary>リンクの「アバタールック変更」側: 指定の写真ルックフィールドを
    /// <paramref name="delta"/> ぶんずらす(有効範囲でクランプ)。アバターの新しい値を
    /// そのままコピーしないので、両者の既存の差は保たれる。各アバタールックの
    /// ハンドラからそのフィールドの delta 付きで呼ばれる ── 差分計算にはこの変更
    /// 直前の値が要り、事後に飛ぶ property-changed コールバックにはそれが無い。</summary>
    private void ShiftPhotoIfLinked(ref double photoField, double delta, double min, double max)
    {
        if (!_lookLinked || delta == 0) return;
        photoField = Math.Clamp(photoField + delta, min, max);
        RefreshPhotoLookUI();
        ScheduleCompositeRender();
    }

    /// <summary>色調補正スライダー(アバター/写真ルック)をドラッグ中の間 true ──
    /// false に戻るまで RenderCompositePreview は保存品質の結果(_lastComposite)を
    /// 更新しない。PngColorSlider*/PhotoColorSlider* 参照。</summary>
    private bool _isCompositeDragging;

    /// <summary>ユーザーが「アバターなしで進める」を選ぶと true ── アバターが
    /// 読み込まれていても(前セッションからの復元など)この合成ではアバター無し扱いに
    /// する。アバター画像は位置合わせモードと共有のグローバル状態なので、実際に
    /// アンロードするとそちらにも影響するため。アバター再読み込みで自動クリア。</summary>
    private bool _compositeSkipAvatar;

    // ---- アバターが写真上のどこに載るか: 写真ピクセル座標(割合ではない)。写真ごとに
    //      1回、VRChat のライブ枠かフィットのフォールバックから初期化し、その後
    //      「配置」カードのスライダーで編集できる。位置合わせモードの X/Y/Width とは独立
    //      (あちらは VRChat 相対のスクリーンピクセルで、ライブ窓が無いと無意味)。 ----

    private double _compositePlaceX, _compositePlaceY, _compositePlaceWidth, _compositePlaceHeight;
    private double _compositeRotation;

    private bool _compositePlacementInitialized;

    /// <summary>null = 切り抜き無し(保存画像は写真そのままのサイズ、既定)。
    /// それ以外は完成した合成に最後のステップとして適用する幅/高さ比。
    /// ImageAdjustment.CropToAspect と下の ApplyCanvasCrop 参照。</summary>
    private double? _canvasAspectRatio;

    /// <summary>0..100、<see cref="_canvasAspectRatio"/> 適用後に余裕のある軸で
    /// 切り抜き窓がどこに座るか(50 = 中央、既定)。null でない比のときだけ意味を持つ。</summary>
    private double _canvasCropOffsetX = 50, _canvasCropOffsetY = 50;

    /// <summary>10..100、比で最大の切り抜きボックスに対する割合(100 = 既定: 写真に
    /// 収まる <see cref="_canvasAspectRatio"/> の最大ボックス)。100 未満は比を変えずに
    /// 縮小する ── その場ズームのノブ。両軸に切り抜き位置の余裕も生まれる。</summary>
    private double _canvasCropWidthPercent = 100;

    /// <summary>10..100、_canvasCropWidthPercent と同じその場ズームだが、自由モード
    /// (_canvasAspectRatio が null)でのみ意味を持つ。固定比モードでは比が高さを
    /// 幅に縛るので _canvasCropWidthPercent だけで両方動くが、自由には縛る比が
    /// 無いので高さ用に独立ノブが要る。</summary>
    private double _canvasCropHeightPercent = 100;

    /// <summary>切り抜きモード がオンの間 true。プレビュー上に切り抜き境界 + 隅ハンドルを
    /// 出して直接ドラッグできる。true の間、RenderCompositePreview は最終の切り抜きを
    /// スキップして未切り抜きの合成全体を表示し、UpdateCanvasCropBoundary が
    /// 捨てられる部分を暗くする(写真編集ソフトの定番)。CropModeToggle_Changed、
    /// CanvasCropHandle_*、CanvasCropBoundary_* 参照。</summary>
    private bool _isCropModeActive;

    /// <summary>切り抜きモード / アバター配置モード中は、まだ確定していない
    /// 切り抜きの外へも要素を置いて見えるよう、プレビューは未切り抜きの写真
    /// 全体を表示する。GetDisplayedCropRect と RenderCompositePreview の
    /// cropAdjusting、RefreshSliderLockState の hardLocked は必ずこの1つを
    /// 参照して食い違わないようにする(デカール配置モードは含めない -- 切り抜き
    /// 後のキャンバス上で作業する)。</summary>
    private bool PreviewShowsUncropped => _isCropModeActive || _isAvatarPlacementModeActive;

    private ImageAdjustment.ColorAdjustments PhotoAdjustments => new(
        _photoBrightness, _photoContrast, _photoSaturation,
        _photoVibrance, _photoTemperature, _photoTint, _photoHue,
        _photoHighlights, _photoShadows, _photoWhites, _photoBlacks,
        _photoColorTintStrength, _photoColorTintR, _photoColorTintG, _photoColorTintB);

    /// <summary>アバターの現在のルック調整値(<see cref="PhotoAdjustments"/> のミラー)。
    /// ルック一致ボタンがアバターを一致先にするとき、現在のルックで描くのに使う。</summary>
    private ImageAdjustment.ColorAdjustments CurrentAvatarAdjustments => new(
        _state.Brightness, _state.Contrast, _state.Saturation,
        _state.Vibrance, _state.Temperature, _state.Tint, _state.Hue,
        _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks);

    /// <summary>両 Match ボタンのクラスタリングパスの主要色数。アバターの主な
    /// 色領域(肌/髪/服)を分けるのに十分で、ノイズまで細分化しない程度。</summary>
    private const int MatchLookClusterCount = 4;

    /// <summary>アバターのルック調整スライダーを背景写真の現在のルックへ寄せる
    /// (ImageAdjustment.SolveMatchAdjustmentsClustered 参照)。ソース統計はアバターの
    /// 無調整ピクセル(不透明部分のみマスク)、ターゲット統計は現在調整済みの写真から
    /// 取るので、生スクショではなく今見えているものに合わせる。色相はあえて触らない
    /// (要望による)。まず 一括調整 をオフにし、ローディング表示を出す。実計算は
    /// バックグラウンドスレッド(Task.Run)で走る ── UI スレッドの同期呼び出しは
    /// WPF の描画/アニメーションポンプを止めるため。使う各関数は WPF/Dispatcher
    /// 依存オブジェクトを持たない PixelBuffer バイト配列だけを扱うので
    /// UI スレッド外で安全。</summary>
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
        // 下の各代入が _state.PropertyChanged を発火してプレビューレンダーを
        // 同期で起こす。_isCompositeDragging 中なので途中レンダーは保存品質扱い
        // されない。確定レンダーはあとで FinishMatchRender が1回行う。
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
        // 上の低解像度レンダーが各自このボタンを再有効化するので、
        // FinishMatchRender のフル解像度パスが終わるまで無効に戻す。
        MatchAvatarToPhotoButton.IsEnabled = false;
        MatchPhotoToAvatarButton.IsEnabled = false;
        _undo.CommitChange();
        FinishMatchRender();
    }

    /// <summary><see cref="MatchAvatarToPhotoButton_Click"/> の逆: 写真のルック
    /// 調整スライダーをアバターの現在(調整済み)のルックへ寄せる。色相不変/
    /// 一括調整オフ/バックグラウンドスレッドの扱いは同じ。</summary>
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

    /// <summary><see cref="WarmUpGpuPipelineAsync"/> が実行済みなら true。
    /// 最初の await より前に同期でセットし、UI スレッドからの再入呼び出しが
    /// check-then-set レースを見ないようにする。</summary>
    private bool _gpuPipelineWarmedUp;

    /// <summary>GPU エフェクトチェーン全体を、出力を捨てる小さなバッファで1回走らせ、
    /// 各シェーダの初回ドライバコンパイルコストをここ(ローディング表示中)で
    /// 引き受ける ── ユーザーが最初に触ったスライダーで発生させない。
    ///
    /// 合成モード入場時のレンダーだけでは足りない: 仕上げ効果の多くは既定 amount=0 で、
    /// amount&lt;=0 だと自前シェーダを dispatch せず early-return するため、既定設定の
    /// レンダーはパイプラインの大半をコンパイルしない。ここでは全ステージを強制オンに
    /// する(非ゼロ量、ドロップシャドウのトーン + ぼかし + 実オーバーレイ)。
    /// _gpuPipelineWarmedUp によりアプリセッションで最大1回、以降は no-op。
    ///
    /// GpuAvatarEdgeBlur(境界ぼかし)も別途ウォームする: その JFA ベースの
    /// シェーダは CompositeOverlayOntoPhoto が触るものとは別なので、上の呼び出しでは
    /// コンパイルされない。</summary>
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

    /// <summary>Match ボタン処理の締め: 上の両ハンドラが必要とするフル解像度の
    /// プレビューレンダーを1回行う。Background 優先度に遅延してローディング表示を
    /// アニメーションさせ、本当に終わってから隠す ── 数メガピクセル写真のフル解像度
    /// 再合成は一瞬 UI スレッドを止めるので、その間スピナーを維持する。
    /// WarmUpGpuPipelineAsync もここで走る(合成モードの初回レンダー)。</summary>
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
        new CompositePhotoLook(
            PhotoBrightness: _photoBrightness, PhotoContrast: _photoContrast, PhotoSaturation: _photoSaturation,
            PhotoVibrance: _photoVibrance, PhotoTemperature: _photoTemperature, PhotoTint: _photoTint, PhotoHue: _photoHue,
            PhotoHighlights: _photoHighlights, PhotoShadows: _photoShadows, PhotoWhites: _photoWhites, PhotoBlacks: _photoBlacks,
            PhotoColorTintStrength: _photoColorTintStrength, PhotoColorTintR: _photoColorTintR, PhotoColorTintG: _photoColorTintG, PhotoColorTintB: _photoColorTintB,
            PhotoBlurAmount: _photoBlurAmount),
        new CompositeFinish(
            GrainAmount: _grainAmount, VignetteAmount: _vignetteAmount,
            SoftnessAmount: _softnessAmount, SharpnessAmount: _sharpnessAmount,
            FadeAmount: _fadeAmount, GlowAmount: _glowAmount,
            ChromaticAberrationAmount: _chromaticAberrationAmount, ColorBleedAmount: _colorBleedAmount, ScanlineAmount: _scanlineAmount,
            ClarityAmount: _clarityAmount, LightLeakAmount: _lightLeakAmount, LightLeakAngle: _lightLeakAngle, LightLeakDistance: _lightLeakDistance,
            LightLeakColorB: _lightLeakColorB, LightLeakColorG: _lightLeakColorG, LightLeakColorR: _lightLeakColorR,
            ToneGradientAmount: _toneGradientAmount, ToneGradientRotation: _toneGradientRotation,
            ToneGradientLightR: _toneGradientLightR, ToneGradientLightG: _toneGradientLightG, ToneGradientLightB: _toneGradientLightB,
            ToneGradientDarkR: _toneGradientDarkR, ToneGradientDarkG: _toneGradientDarkG, ToneGradientDarkB: _toneGradientDarkB),
        new CompositeDropShadow(
            DropShadowAmount: _dropShadowAmount, DropShadowDirection: _dropShadowDirection, DropShadowDistance: _dropShadowDistance, DropShadowBlur: _dropShadowBlur,
            DropShadowColorB: _dropShadowColorB, DropShadowColorG: _dropShadowColorG, DropShadowColorR: _dropShadowColorR,
            DropShadowBlendMode: _dropShadowBlendMode),
        new CompositeCanvasCrop(
            CanvasAspectRatio: _canvasAspectRatio, CanvasCropOffsetX: _canvasCropOffsetX, CanvasCropOffsetY: _canvasCropOffsetY,
            CanvasCropWidthPercent: _canvasCropWidthPercent, CanvasCropHeightPercent: _canvasCropHeightPercent),
        new CompositePlacement(
            CompositePlaceX: _compositePlaceX, CompositePlaceY: _compositePlaceY, CompositePlaceWidth: _compositePlaceWidth, CompositePlaceHeight: _compositePlaceHeight,
            CompositeRotation: _compositeRotation),
        CaptureDecalSnapshot(),
        new CompositeBlankCanvas(
            BlankCanvasR: _blankCanvasR, BlankCanvasG: _blankCanvasG, BlankCanvasB: _blankCanvasB,
            BlankCanvasR2: _blankCanvasR2, BlankCanvasG2: _blankCanvasG2, BlankCanvasB2: _blankCanvasB2,
            BlankCanvasGradientEnabled: _blankCanvasGradientEnabled, BlankCanvasGradientDirection: _blankCanvasGradientDirection, IsBlankCanvasActive: _isBlankCanvasActive),
        CaptureMaskSnapshot(),
        _photoPixelBuffer);

    private void ApplyCompositeSnapshot(object? snapshot)
    {
        if (snapshot is not CompositeSnapshot s) return;
        _photoBrightness = s.PhotoLook.PhotoBrightness;
        _photoContrast = s.PhotoLook.PhotoContrast;
        _photoSaturation = s.PhotoLook.PhotoSaturation;
        _photoVibrance = s.PhotoLook.PhotoVibrance;
        _photoTemperature = s.PhotoLook.PhotoTemperature;
        _photoTint = s.PhotoLook.PhotoTint;
        _photoHue = s.PhotoLook.PhotoHue;
        _photoHighlights = s.PhotoLook.PhotoHighlights;
        _photoShadows = s.PhotoLook.PhotoShadows;
        _photoWhites = s.PhotoLook.PhotoWhites;
        _photoBlacks = s.PhotoLook.PhotoBlacks;
        _photoColorTintStrength = s.PhotoLook.PhotoColorTintStrength;
        _photoColorTintR = s.PhotoLook.PhotoColorTintR;
        _photoColorTintG = s.PhotoLook.PhotoColorTintG;
        _photoColorTintB = s.PhotoLook.PhotoColorTintB;
        _photoBlurAmount = s.PhotoLook.PhotoBlurAmount;
        _grainAmount = s.Finish.GrainAmount;
        _vignetteAmount = s.Finish.VignetteAmount;
        _softnessAmount = s.Finish.SoftnessAmount;
        _sharpnessAmount = s.Finish.SharpnessAmount;
        _fadeAmount = s.Finish.FadeAmount;
        _glowAmount = s.Finish.GlowAmount;
        _chromaticAberrationAmount = s.Finish.ChromaticAberrationAmount;
        _colorBleedAmount = s.Finish.ColorBleedAmount;
        _scanlineAmount = s.Finish.ScanlineAmount;
        _clarityAmount = s.Finish.ClarityAmount;
        _lightLeakAmount = s.Finish.LightLeakAmount;
        _lightLeakAngle = s.Finish.LightLeakAngle;
        _lightLeakDistance = s.Finish.LightLeakDistance;
        _lightLeakColorB = s.Finish.LightLeakColorB;
        _lightLeakColorG = s.Finish.LightLeakColorG;
        _lightLeakColorR = s.Finish.LightLeakColorR;
        _toneGradientAmount = s.Finish.ToneGradientAmount;
        _toneGradientRotation = s.Finish.ToneGradientRotation;
        _toneGradientLightR = s.Finish.ToneGradientLightR;
        _toneGradientLightG = s.Finish.ToneGradientLightG;
        _toneGradientLightB = s.Finish.ToneGradientLightB;
        _toneGradientDarkR = s.Finish.ToneGradientDarkR;
        _toneGradientDarkG = s.Finish.ToneGradientDarkG;
        _toneGradientDarkB = s.Finish.ToneGradientDarkB;
        _dropShadowAmount = s.DropShadow.DropShadowAmount;
        _dropShadowDirection = s.DropShadow.DropShadowDirection;
        _dropShadowDistance = s.DropShadow.DropShadowDistance;
        _dropShadowBlur = s.DropShadow.DropShadowBlur;
        _dropShadowColorB = s.DropShadow.DropShadowColorB;
        _dropShadowColorG = s.DropShadow.DropShadowColorG;
        _dropShadowColorR = s.DropShadow.DropShadowColorR;
        _dropShadowBlendMode = s.DropShadow.DropShadowBlendMode;
        _canvasAspectRatio = s.CanvasCrop.CanvasAspectRatio;
        _canvasCropOffsetX = s.CanvasCrop.CanvasCropOffsetX;
        _canvasCropOffsetY = s.CanvasCrop.CanvasCropOffsetY;
        _canvasCropWidthPercent = s.CanvasCrop.CanvasCropWidthPercent;
        _canvasCropHeightPercent = s.CanvasCrop.CanvasCropHeightPercent;
        _compositePlaceX = s.Placement.CompositePlaceX;
        _compositePlaceY = s.Placement.CompositePlaceY;
        _compositePlaceWidth = s.Placement.CompositePlaceWidth;
        _compositePlaceHeight = s.Placement.CompositePlaceHeight;
        _compositeRotation = s.Placement.CompositeRotation;

        // #2 背景写真の +90°回転: スナップショット時点の(可能なら回転済みの)
        // ピクセルバッファ参照をそのまま戻す。回転のみが _photoPixelBuffer を
        // Undo 対象で差し替える操作なので、静止した写真では全スナップショットが
        // 同じ参照を共有し、メモリ増も等価判定の誤検知も無い。
        if (s.PhotoBuffer is { } pb && !ReferenceEquals(pb, _photoPixelBuffer))
        {
            _photoPixelBuffer = pb;
            ImageAdjustment.PrecomputeFilmGrainNoise(pb.Width, pb.Height);
        }

        // #1 「背景なしで作成」の色/グラデーション。
        bool blankChanged =
            _blankCanvasR != s.BlankCanvas.BlankCanvasR || _blankCanvasG != s.BlankCanvas.BlankCanvasG || _blankCanvasB != s.BlankCanvas.BlankCanvasB ||
            _blankCanvasR2 != s.BlankCanvas.BlankCanvasR2 || _blankCanvasG2 != s.BlankCanvas.BlankCanvasG2 || _blankCanvasB2 != s.BlankCanvas.BlankCanvasB2 ||
            _blankCanvasGradientEnabled != s.BlankCanvas.BlankCanvasGradientEnabled ||
            _blankCanvasGradientDirection != s.BlankCanvas.BlankCanvasGradientDirection ||
            _isBlankCanvasActive != s.BlankCanvas.IsBlankCanvasActive;
        _blankCanvasR = s.BlankCanvas.BlankCanvasR; _blankCanvasG = s.BlankCanvas.BlankCanvasG; _blankCanvasB = s.BlankCanvas.BlankCanvasB;
        _blankCanvasR2 = s.BlankCanvas.BlankCanvasR2; _blankCanvasG2 = s.BlankCanvas.BlankCanvasG2; _blankCanvasB2 = s.BlankCanvas.BlankCanvasB2;
        _blankCanvasGradientEnabled = s.BlankCanvas.BlankCanvasGradientEnabled;
        _blankCanvasGradientDirection = s.BlankCanvas.BlankCanvasGradientDirection;
        _isBlankCanvasActive = s.BlankCanvas.IsBlankCanvasActive;
        if (blankChanged)
        {
            RefreshBlankCanvasUI();
            if (_isBlankCanvasActive) RegenerateBlankCanvas(); // #2 で戻したバッファと同じ寸法で塗り直す
        }

        ApplyDecalSnapshot(s.Decals);
        ApplyMaskSnapshot(s.Masks);
        RefreshPhotoLookUI();
        RefreshFinishUI();
        RefreshCompositePlacementUI();
        ScheduleCompositeRender();
    }

    /// <summary>現在読み込んでいる合成写真のパス。null もあり得る。終了時に
    /// App.xaml.cs が読んで永続化する(OverlayState.ImagePath と同様)。</summary>
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
            // フィルムグレインのノイズは幅/高さのみに依存(固定シード)なので、
            // 初回レンダーでなく今作る。PrecomputeFilmGrainNoise 参照。
            ImageAdjustment.PrecomputeFilmGrainNoise(_photoPixelBuffer.Width, _photoPixelBuffer.Height);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return false;
        }

        _compositePlacementInitialized = false; // 新しい写真は配置推定をやり直す
        _photoPath = path;
        PhotoPathText.Text = Path.GetFileName(path);
        // 実写真に切り替えたら「背景なしで作成」の色/グラデーションUIは
        // もう意味を持たない(RegenerateBlankCanvasが実写真を上書きして
        // しまわないよう、ここでガードを倒しておく)。
        _isBlankCanvasActive = false;
        BlankCanvasColorPanel.Visibility = Visibility.Collapsed;
        RefreshBlankCanvasActiveUI();
        // デカールの位置は今の写真の画素座標系そのものなので、別の写真に
        // 差し替えたら意味を持たなくなる -- 新しい写真ごとにリセット
        // (アバターマーカーだけ残す)。
        _decalLayerOrder.RemoveAll(l => l is not null);
        ExitDecalPlacementMode();
        RebuildDecalStrip();
        ClearMasks();
        // 別の写真に切り替えたら Undo 履歴は無効(配置/look/デカール/背景色は
        // その写真に対してのみ意味を持つ)。モード入場スナップショットも同様。
        _undo.Clear();
        _cropModeEntrySnapshot = null;
        _avatarPlacementModeEntrySnapshot = null;
        return true;
    }

    /// <summary>配置カードの回転ボタン -- 押すたびに背景写真を90°ずつ回転
    /// (幅と高さが入れ替わる)。アバター配置・デカール位置は回転前の写真の
    /// 画素座標系のままでは意味を失うので、新しい写真を読み込んだ時
    /// (TryLoadPhotoPixels)と同じ理由で初期化し直す。</summary>
    private void RotatePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_photoPixelBuffer is not { } photo) return;
        // Undo/Redo に乗せる: 回転前のバッファ参照(CompositeSnapshot.PhotoBuffer)と
        // 巻き添えで消えるデカール(CompositeSnapshot.Decals)がスナップショットに
        // 入るので、Ctrl+Z で向き・デカールごと戻る。
        _undo.BeginChange();
        _photoPixelBuffer = ImageAdjustment.RotateClockwise90(photo);
        ImageAdjustment.PrecomputeFilmGrainNoise(_photoPixelBuffer.Width, _photoPixelBuffer.Height);
        _compositePlacementInitialized = false;
        _decalLayerOrder.RemoveAll(l => l is not null);
        ExitDecalPlacementMode();
        RebuildDecalStrip();
        ClearMasks();
        // 配置の推定し直し(_compositePlacementInitialized=false)を CommitChange の
        // 前に確定させたいので、ScheduleCompositeRender ではなく同期的に走らせる
        // (ResetCompositePlacementButton_Click と同じ理由・同じやり方)。
        // SizePreviewToImage は RenderCompositePreview 側が入れ替わった幅高さで呼ぶ。
        _ = RenderCompositePreview();
        RefreshCompositePlacementUI();
        _undo.CommitChange();
    }

    /// <summary>写真を読み込み(手動ピッカーまたはスクショ監視トーストのクリック)、
    /// コントロールパネルを前面へ出す ── トーストはあえて非アクティブ化
    /// (<see cref="ScreenshotToastWindow"/> 参照)なので、そのままでは
    /// この窓が前に出ない。</summary>
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

    /// <summary>起動時に前回の合成写真を復元する ── 合成モードに切り替えず、
    /// フォーカスも奪わず静かに。PNG の ImagePath 復元と同じ扱い。</summary>
    public void RestorePhotoSilently(string path) => TryLoadPhotoPixels(path);

    private void PickPhotoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = ImageFileDialogFilter,
            Title = "背景写真を選択",
        };
        if (dialog.ShowDialog() == true)
        {
            LoadPhotoForComposite(dialog.FileName);
        }
    }

    private const int BlankCanvasFallbackSize = 2048;
    private byte _blankCanvasR = 255, _blankCanvasG = 255, _blankCanvasB = 255;
    private double _blankCanvasHue, _blankCanvasSat;
    private byte _blankCanvasR2, _blankCanvasG2, _blankCanvasB2;
    private double _blankCanvasHue2, _blankCanvasSat2;
    private bool _blankCanvasGradientEnabled;
    private double _blankCanvasGradientDirection;

    /// <summary>「背景なしで作成」で作った合成用の仮想写真が今アクティブか
    /// -- これがtrueの間だけ色/グラデーションUIの変更が_photoPixelBufferを
    /// その場で塗り直す(RegenerateBlankCanvas)。実写真を読み込むと
    /// TryLoadPhotoPixels側でfalseに戻り、色UI自体も隠れる(以後の色UI操作は
    /// 何もしない -- そのための現在アクティブかどうかのガード)。</summary>
    private bool _isBlankCanvasActive;

    // ---- 背景の色: アバター画像側のティント色(CompositeColorTintButton)
    //      と全く同じ色相環+RGB+hexのUI/ロジック(GetColorWheelBitmap/
    //      RgbToHsv/HsvToRgb/PositionColorWheelCursorは共通ヘルパーとして
    //      再利用)。ティントと違い_state(永続/Undo対象)には乗らない。
    //      「背景なしで作成」を押すまでは色UI自体がCollapsedなので操作
    //      できない -- 押した後は変更するたびにRegenerateBlankCanvasで
    //      即座に塗り直してプレビューへ反映する。 ----

    private void BlankCanvasColorButton_Click(object sender, RoutedEventArgs e)
    {
        BlankCanvasColorWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncBlankCanvasColorUI(_blankCanvasR, _blankCanvasG, _blankCanvasB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        BlankCanvasColorPopup.IsOpen = true;
    }

    private void BlankCanvasColorEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.BlankCanvas);

    private bool _isDraggingBlankCanvasColorWheel;

    private void BlankCanvasColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingBlankCanvasColorWheel = true;
        _undo.BeginChange();
        BlankCanvasColorWheel.CaptureMouse();
        UpdateBlankCanvasColorFromWheelPosition(e.GetPosition(BlankCanvasColorWheel));
    }

    private void BlankCanvasColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingBlankCanvasColorWheel) return;
        UpdateBlankCanvasColorFromWheelPosition(e.GetPosition(BlankCanvasColorWheel));
    }

    private void BlankCanvasColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingBlankCanvasColorWheel) return;
        _isDraggingBlankCanvasColorWheel = false;
        BlankCanvasColorWheel.ReleaseMouseCapture();
        _undo.CommitChange();
    }

    private void UpdateBlankCanvasColorFromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _blankCanvasHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _blankCanvasSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_blankCanvasHue, _blankCanvasSat, BlankCanvasColorValueSlider.Value / 100.0);
        SetBlankCanvasColor(r, g, b);
    }

    private void BlankCanvasColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_blankCanvasHue, _blankCanvasSat, BlankCanvasColorValueSlider.Value / 100.0);
        SetBlankCanvasColor(r, g, b);
    }

    private void BlankCanvasColorRSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor((byte)Math.Round(BlankCanvasColorRSlider.Value), _blankCanvasG, _blankCanvasB);
    }

    private void BlankCanvasColorGSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor(_blankCanvasR, (byte)Math.Round(BlankCanvasColorGSlider.Value), _blankCanvasB);
    }

    private void BlankCanvasColorBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor(_blankCanvasR, _blankCanvasG, (byte)Math.Round(BlankCanvasColorBSlider.Value));
    }

    private void BlankCanvasColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColorRBox.Text, out var v)) return;
        SetBlankCanvasColor((byte)Math.Clamp(v, 0, 255), _blankCanvasG, _blankCanvasB);
    }

    private void BlankCanvasColorGBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColorGBox.Text, out var v)) return;
        SetBlankCanvasColor(_blankCanvasR, (byte)Math.Clamp(v, 0, 255), _blankCanvasB);
    }

    private void BlankCanvasColorBBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColorBBox.Text, out var v)) return;
        SetBlankCanvasColor(_blankCanvasR, _blankCanvasG, (byte)Math.Clamp(v, 0, 255));
    }

    private void BlankCanvasColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(BlankCanvasColorHexBox.Text, out var r, out var g, out var b)) return;
        SetBlankCanvasColor(r, g, b);
    }

    private void SyncBlankCanvasColorUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _blankCanvasSat = s;
        if (s > 0.001) _blankCanvasHue = h;

        BlankCanvasColorRSlider.Value = r;
        BlankCanvasColorRBox.Text = r.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColorGSlider.Value = g;
        BlankCanvasColorGBox.Text = g.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColorBSlider.Value = b;
        BlankCanvasColorBBox.Text = b.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(BlankCanvasColorWheelCursor, _blankCanvasHue, _blankCanvasSat);
        BlankCanvasColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        BlankCanvasColorHexBox.Text = ToHexColor(r, g, b);
    }

    private void SetBlankCanvasColor(byte r, byte g, byte b)
    {
        _blankCanvasR = r;
        _blankCanvasG = g;
        _blankCanvasB = b;

        _suppressEventsDepth++;
        SyncBlankCanvasColorUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        BlankCanvasColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (_isBlankCanvasActive) RegenerateBlankCanvas();
    }

    // ---- 背景の色2: グラデーションon時のみ意味を持つもう一方の色。
    //      UI/ロジックは色1(BlankCanvasColor*)と全く同じ構成をそのまま
    //      複製しているだけ。 ----

    private void BlankCanvasColor2Button_Click(object sender, RoutedEventArgs e)
    {
        BlankCanvasColor2Wheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncBlankCanvasColor2UI(_blankCanvasR2, _blankCanvasG2, _blankCanvasB2);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        BlankCanvasColor2Popup.IsOpen = true;
    }

    private void BlankCanvasColor2EyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.BlankCanvas2);

    private bool _isDraggingBlankCanvasColor2Wheel;

    private void BlankCanvasColor2Wheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingBlankCanvasColor2Wheel = true;
        _undo.BeginChange();
        BlankCanvasColor2Wheel.CaptureMouse();
        UpdateBlankCanvasColor2FromWheelPosition(e.GetPosition(BlankCanvasColor2Wheel));
    }

    private void BlankCanvasColor2Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingBlankCanvasColor2Wheel) return;
        UpdateBlankCanvasColor2FromWheelPosition(e.GetPosition(BlankCanvasColor2Wheel));
    }

    private void BlankCanvasColor2Wheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingBlankCanvasColor2Wheel) return;
        _isDraggingBlankCanvasColor2Wheel = false;
        BlankCanvasColor2Wheel.ReleaseMouseCapture();
        _undo.CommitChange();
    }

    private void UpdateBlankCanvasColor2FromWheelPosition(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _blankCanvasHue2 = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _blankCanvasSat2 = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_blankCanvasHue2, _blankCanvasSat2, BlankCanvasColor2ValueSlider.Value / 100.0);
        SetBlankCanvasColor2(r, g, b);
    }

    private void BlankCanvasColor2ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_blankCanvasHue2, _blankCanvasSat2, BlankCanvasColor2ValueSlider.Value / 100.0);
        SetBlankCanvasColor2(r, g, b);
    }

    private void BlankCanvasColor2RSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor2((byte)Math.Round(BlankCanvasColor2RSlider.Value), _blankCanvasG2, _blankCanvasB2);
    }

    private void BlankCanvasColor2GSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor2(_blankCanvasR2, (byte)Math.Round(BlankCanvasColor2GSlider.Value), _blankCanvasB2);
    }

    private void BlankCanvasColor2BSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        SetBlankCanvasColor2(_blankCanvasR2, _blankCanvasG2, (byte)Math.Round(BlankCanvasColor2BSlider.Value));
    }

    private void BlankCanvasColor2RBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColor2RBox.Text, out var v)) return;
        SetBlankCanvasColor2((byte)Math.Clamp(v, 0, 255), _blankCanvasG2, _blankCanvasB2);
    }

    private void BlankCanvasColor2GBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColor2GBox.Text, out var v)) return;
        SetBlankCanvasColor2(_blankCanvasR2, (byte)Math.Clamp(v, 0, 255), _blankCanvasB2);
    }

    private void BlankCanvasColor2BBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasColor2BBox.Text, out var v)) return;
        SetBlankCanvasColor2(_blankCanvasR2, _blankCanvasG2, (byte)Math.Clamp(v, 0, 255));
    }

    private void BlankCanvasColor2HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(BlankCanvasColor2HexBox.Text, out var r, out var g, out var b)) return;
        SetBlankCanvasColor2(r, g, b);
    }

    private void SyncBlankCanvasColor2UI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _blankCanvasSat2 = s;
        if (s > 0.001) _blankCanvasHue2 = h;

        BlankCanvasColor2RSlider.Value = r;
        BlankCanvasColor2RBox.Text = r.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColor2GSlider.Value = g;
        BlankCanvasColor2GBox.Text = g.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColor2BSlider.Value = b;
        BlankCanvasColor2BBox.Text = b.ToString(CultureInfo.InvariantCulture);
        BlankCanvasColor2ValueSlider.Value = v * 100;
        PositionColorWheelCursor(BlankCanvasColor2WheelCursor, _blankCanvasHue2, _blankCanvasSat2);
        BlankCanvasColor2PreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        BlankCanvasColor2HexBox.Text = ToHexColor(r, g, b);
    }

    private void SetBlankCanvasColor2(byte r, byte g, byte b)
    {
        _blankCanvasR2 = r;
        _blankCanvasG2 = g;
        _blankCanvasB2 = b;

        _suppressEventsDepth++;
        SyncBlankCanvasColor2UI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        BlankCanvasColor2Swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (_isBlankCanvasActive) RegenerateBlankCanvas();
    }

    // ---- グラデーションon/off: onの間は背景の色が色1/色2の2つに増え、
    //      方向スライダーも現れる。 ----

    private void BlankCanvasGradientToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _undo.BeginChange();
        _blankCanvasGradientEnabled = BlankCanvasGradientToggle.IsChecked == true;
        RefreshBlankCanvasGradientUI();
        if (_isBlankCanvasActive) RegenerateBlankCanvas();
        _undo.CommitChange();
    }

    private void RefreshBlankCanvasGradientUI()
    {
        BlankCanvasColorLabel.Text = _blankCanvasGradientEnabled ? "背景の色1" : "背景の色";
        BlankCanvasColor2Row.Visibility = _blankCanvasGradientEnabled ? Visibility.Visible : Visibility.Collapsed;
        BlankCanvasGradientDirectionRow.Visibility = _blankCanvasGradientEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BlankCanvasGradientDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(BlankCanvasGradientDirectionSlider.Value);
        _suppressEventsDepth++;
        BlankCanvasGradientDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _blankCanvasGradientDirection) return;
        _blankCanvasGradientDirection = rounded;
        if (_isBlankCanvasActive) RegenerateBlankCanvas();
    }

    private void BlankCanvasGradientDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(BlankCanvasGradientDirectionBox.Text, out var v)) return;
        _blankCanvasGradientDirection = Math.Clamp(v, 0, 360);
        _suppressEventsDepth++;
        BlankCanvasGradientDirectionSlider.Value = _blankCanvasGradientDirection;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (_isBlankCanvasActive) RegenerateBlankCanvas();
    }

    /// <summary>色/グラデーションのon-off/方向が変わるたびに呼ばれる --
    /// 解像度はいじらず(_photoPixelBufferの今の幅高さのまま)、中身だけ
    /// 単色かグラデーションで塗り直して再描画をスケジュールする。</summary>
    private void RegenerateBlankCanvas()
    {
        if (_photoPixelBuffer is not { } current) return;
        _photoPixelBuffer = _blankCanvasGradientEnabled
            ? ImageAdjustment.CreateLinearGradient(current.Width, current.Height,
                _blankCanvasR, _blankCanvasG, _blankCanvasB, _blankCanvasR2, _blankCanvasG2, _blankCanvasB2, _blankCanvasGradientDirection)
            : ImageAdjustment.CreateSolidColor(current.Width, current.Height, _blankCanvasR, _blankCanvasG, _blankCanvasB);
        ScheduleCompositeRender();
    }

    /// <summary>アバター画像・(既に読み込まれている)背景写真のうち、解像度が
    /// 高い方に合わせる -- どちらも無ければBlankCanvasFallbackSizeの正方形。
    /// 高い方の実サイズ(幅と高さ両方)をそのまま使うので、正方形とは限らない。</summary>
    private (int Width, int Height) GetDefaultBlankCanvasSize()
    {
        Size? avatarSize = _overlayWindow.ImageNativeSize is { Width: > 0, Height: > 0 } a ? a : null;
        Size? photoSize = _photoPixelBuffer is { } p ? new Size(p.Width, p.Height) : null;

        Size chosen = (avatarSize, photoSize) switch
        {
            ({ } av, { } ph) => av.Width * av.Height >= ph.Width * ph.Height ? av : ph,
            ({ } av, null) => av,
            (null, { } ph) => ph,
            _ => new Size(BlankCanvasFallbackSize, BlankCanvasFallbackSize),
        };
        return (Math.Max(1, (int)Math.Round(chosen.Width)), Math.Max(1, (int)Math.Round(chosen.Height)));
    }

    /// <summary>実写真を使わず、白一色で塗った合成用の仮想写真を作る --
    /// 以降のクロップ/配置/デカール/仕上げエフェクトは通常の写真読み込みと
    /// 全く同じパイプラインをそのまま通る。_photoPathはnullのまま(実体
    /// ファイルが無いので、次回起動時の自動復元処理はFile.Existsで自然に
    /// スキップされる -- App.xaml.cs参照)。押すたびに白・グラデーションoff
    /// にリセットして作り直す(「作成」は毎回まっさらな状態からやり直す
    /// 操作として扱う)。作成後、下の色/グラデーションUIが現れ、以後は
    /// そこを触るたびにRegenerateBlankCanvasが同じ解像度のまま塗り直す。</summary>
    private void CreateBlankCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        var (width, height) = GetDefaultBlankCanvasSize();

        _blankCanvasR = _blankCanvasG = _blankCanvasB = 255;
        _blankCanvasR2 = _blankCanvasG2 = _blankCanvasB2 = 0;
        _blankCanvasGradientDirection = 0;
        _blankCanvasGradientEnabled = false;
        _isBlankCanvasActive = true;
        RefreshBlankCanvasActiveUI();

        _photoPixelBuffer = ImageAdjustment.CreateSolidColor(width, height, _blankCanvasR, _blankCanvasG, _blankCanvasB);
        ImageAdjustment.PrecomputeFilmGrainNoise(_photoPixelBuffer.Width, _photoPixelBuffer.Height);
        _compositePlacementInitialized = false;
        _photoPath = null;
        PhotoPathText.Text = "(背景なし)";
        _decalLayerOrder.RemoveAll(l => l is not null);
        ExitDecalPlacementMode();
        RebuildDecalStrip();
        ClearMasks();

        _photoBrightness = _photoContrast = _photoSaturation = 0;
        _photoVibrance = _photoTemperature = _photoTint = _photoHue = 0;
        _photoHighlights = _photoShadows = _photoWhites = _photoBlacks = 0;
        RefreshPhotoLookUI();
        ClearCompositeSaveStatus();

        _suppressEventsDepth++;
        SyncBlankCanvasColorUI(_blankCanvasR, _blankCanvasG, _blankCanvasB);
        SyncBlankCanvasColor2UI(_blankCanvasR2, _blankCanvasG2, _blankCanvasB2);
        BlankCanvasColorSwatch.Background = new SolidColorBrush(Color.FromRgb(_blankCanvasR, _blankCanvasG, _blankCanvasB));
        BlankCanvasColor2Swatch.Background = new SolidColorBrush(Color.FromRgb(_blankCanvasR2, _blankCanvasG2, _blankCanvasB2));
        BlankCanvasGradientToggle.IsChecked = false;
        BlankCanvasGradientDirectionSlider.Value = 0;
        BlankCanvasGradientDirectionBox.Text = "0";
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        RefreshBlankCanvasGradientUI();

        BlankCanvasColorPanel.Visibility = Visibility.Visible;
        // 新しい合成用キャンバスなので Undo 履歴は無効(TryLoadPhotoPixels と同様)。
        // 以後の色/グラデーション編集からが Undo 対象。
        _undo.Clear();
        _cropModeEntrySnapshot = null;
        _avatarPlacementModeEntrySnapshot = null;
        ShowComposite();
    }

    /// <summary>位置合わせ済みオーバーレイが写真上のどこに載るか(写真ピクセル寸法に
    /// 対する割合)。ライブプレビューは VRChat の現クライアント領域内の推定カメラ枠
    /// 矩形を基準に _state.X/Y/Width/Height でオーバーレイを置き、写真はまさにその
    /// 枠の内容(その出力解像度)なので、枠内の同じ割合位置/サイズが写真へ直接写る。
    /// 16:9/9:16 決め打ちではなく写真自身の縦横比を枠の幅に使う ── そうしないと
    /// カメラ解像度が 16:9/9:16 でないとき fracW と fracH が異なる倍率でスケールされ、
    /// オーバーレイが伸びる。</summary>
    private (double FracX, double FracY, double FracW, double FracH)? ComputeOverlayFrameFraction(double photoAspect)
    {
        if (_oscListener.IsLandscape is not { } landscape) return null;
        var hwnd = VRChatWindowService.FindVRChatWindow();
        if (hwnd is null) return null;
        if (VRChatWindowService.GetClientRectInDips(hwnd.Value) is not { Width: > 0, Height: > 0 } region) return null;

        var frame = VRChatWindowService.ComputeCameraFrameRect(region, landscape, photoAspect);
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        double fracX = (_state.X - frame.Left) / frame.Width;
        double fracY = (_state.Y - frame.Top) / frame.Height;
        double fracW = _state.Width / frame.Width;
        double fracH = _state.Height / frame.Height;
        return (fracX, fracY, fracW, fracH);
    }

    /// <summary>写真ごとに1回の配置推定: VRChat が起動中で位置/向きを報告して
    /// いればそのライブ枠、なければアバターの縦横比を写真の余白にフィットさせる。
    /// 以降はユーザーがプレビュー上で直接ドラッグ/ホイールズームして調整するので
    /// (PreviewImage_MouseLeftButtonDown 等)、「自動配置に戻す」でこれを再実行する
    /// のがもう一つの変更手段。</summary>
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
            // アバターの縦横比を写真の 100% にフィットさせる。制約が強い方の軸で
            // スケールが決まり、その軸で写真の端に接する。
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

    /// <summary>ここで同期する X/Y/幅/回転(度) UI はもう無い ── 今は
    /// _compositePlaceX/Y/Width/Height/_compositeRotation からプレビュー上の
    /// ハイライト/ハンドルを再導出し、同じパネルの切り抜きコントロール用に
    /// RefreshCanvasAspectUI を呼ぶだけ。</summary>
    private void RefreshCompositePlacementUI()
    {
        RefreshCanvasAspectUI();
        UpdateAvatarPlacementHighlight();
    }

    /// <summary>縦横比ラジオ群と切り抜き位置スライダーを
    /// _canvasAspectRatio/_canvasCropOffsetX/Y に同期する ── RefreshCompositePlacementUI
    /// と一緒に呼ばれる(同じ配置パネル、同じ更新機会: 構築、undo/redo、スナップショット復元)。</summary>
    private void RefreshCanvasAspectUI()
    {
        _suppressEventsDepth++;
        int index = _canvasAspectRatio switch
        {
            null => 0,
            1.0 => 1,
            0.8 => 2,
            0.5625 => 3,
            1.7778 => 4,
            _ => 5, // カスタム -- 5プリセットのどれにも一致しない比
        };
        CanvasAspectCombo.SelectedIndex = index;
        // カスタム のときだけ表示/設定する。RefreshCanvasAspectUI はこの2ボックスと
        // 無関係な理由でも頻繁に呼ばれる(切り抜き隅ドラッグの各 tick など)ので、
        // ユーザーが打った値("3"/"4" など)を毎回 "0.75"/"1" に上書きしてはいけない。
        // 現在のテキストが同じ比に還元されなくなったとき(undo/redo、プリセット選択、
        // カスタム比の新規読み込み)だけ書き換える。
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
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
    }

    private void CanvasAspectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (CanvasAspectCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        if (tag == "custom")
        {
            // 現在有効な比を保つ(比が無かった初回だけ 1:1 にフォールバック)。
            // 4:5 の直後に カスタム を選んだら 0.8:1 を編集させたい。
            _canvasAspectRatio ??= 1.0;
            _suppressEventsDepth++;
            CanvasAspectCustomRow.Visibility = Visibility.Visible;
            CanvasAspectCustomWidthBox.Text = _canvasAspectRatio.Value.ToString("0.###", CultureInfo.InvariantCulture);
            CanvasAspectCustomHeightBox.Text = "1";
            _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
            ScheduleCompositeRender();
            return;
        }
        CanvasAspectCustomRow.Visibility = Visibility.Collapsed;
        _canvasAspectRatio = tag == "original" ? null : double.Parse(tag, CultureInfo.InvariantCulture);
        ScheduleCompositeRender();
    }

    /// <summary>CanvasAspectCustomWidthBox と CanvasAspectCustomHeightBox の共通
    /// ハンドラ: _canvasAspectRatio はその商で、どちらかが変わるたびに再計算する。</summary>
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
        // OFF→ON の遷移時だけキャプチャ。キャンセル(PreviewModeCancelButton_Click)で
        // このドラッグ開始直前の状態へ正確に戻せる。
        if (turningOn) _cropModeEntrySnapshot = CaptureCompositeSnapshot();
        // アバター配置モードと排他 ── どちらもプレビューのクリックドラッグを独占する。
        // AvatarPlacementModeToggle_Changed の対応チェック参照。
        if (_isCropModeActive && _isAvatarPlacementModeActive)
        {
            AvatarPlacementModeToggle.IsChecked = false;
        }
        // デカール配置中とも排他 ── これは専用トグルを持たない(デカール追加で入る)。
        // 既存デカールの再編集なら確定して抜ける、新規未確定なら破棄。
        if (_isCropModeActive && _isDecalPlacementModeActive)
        {
            if (_editingExistingDecal) ExitDecalPlacementMode();
            else CancelDecalPlacement();
        }
        ScheduleCompositeRender();
        // デバウンスされたレンダーの UpdateCanvasCropBoundary を待たず即更新する ──
        // トグルは押した瞬間に境界+ハンドルを出し入れすべき。
        UpdateCanvasCropBoundary();

        CropModeLabel.Foreground = _isCropModeActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        CropModeLabel.FontWeight = _isCropModeActive ? FontWeights.SemiBold : FontWeights.Normal;
        // 切り抜きドラッグ中にアバター配置(X/Y/幅/回転)を編集できると紛らわしいので
        // グループごと無効化する。
        CompositePlacementControlsPanel.IsEnabled = !_isCropModeActive;
        RefreshSliderLockState();
    }

    /// <summary>合成モードの配置パネルに X/Y/幅/回転(度) スライダーはもう無い ──
    /// このトグル + プレビュー上の直接ドラッグが完全に置き換えた(位置合わせモードが
    /// ずっとそうだったのと同じ)。</summary>
    private void AvatarPlacementModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool turningOn = AvatarPlacementModeToggle.IsChecked == true && !_isAvatarPlacementModeActive;
        _isAvatarPlacementModeActive = AvatarPlacementModeToggle.IsChecked == true;
        // OFF→ON の遷移時だけキャプチャ。キャンセル(PreviewModeCancelButton_Click)で
        // このドラッグ開始直前の状態へ正確に戻せる。
        if (turningOn) _avatarPlacementModeEntrySnapshot = CaptureCompositeSnapshot();
        // 切り抜きモードと排他。CropModeToggle_Changed の対応チェック参照。
        if (_isAvatarPlacementModeActive && _isCropModeActive)
        {
            CropModeToggle.IsChecked = false;
        }
        if (_isAvatarPlacementModeActive && _isDecalPlacementModeActive)
        {
            if (_editingExistingDecal) ExitDecalPlacementMode();
            else CancelDecalPlacement();
        }

        AvatarPlacementModeLabel.Foreground = _isAvatarPlacementModeActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        AvatarPlacementModeLabel.FontWeight = _isAvatarPlacementModeActive ? FontWeights.SemiBold : FontWeights.Normal;
        // プレビューを切り抜き/写真全体の表示で切り替える(スケジュール実行)。
        // 下の UpdateAvatarPlacementHighlight は GetDisplayedCropRect の新しい値で
        // ハンドル/ハイライトを即座に再配置するので、トグルから1レンダー遅れない。
        ScheduleCompositeRender();
        UpdateAvatarPlacementHighlight();
        RefreshSliderLockState();
    }

    /// <summary>プレビューのドラッグを独占する「プレビュー操作モード」共通の集約点
    /// (現在は切り抜きモードとアバター配置モード)。いずれか有効な間は右のルック/
    /// 仕上げ効果スライダーをグレーアウトし SliderLockNotice を出す。各モードの
    /// _Changed ハンドラが _is*ModeActive を更新した後に呼ぶ。</summary>
    private void RefreshSliderLockState()
    {
        // クロップ/アバター配置はプレビューのドラッグを独占するのでカード列
        // 全体を止める。デカール配置は「デカール」カードの図形プロパティ
        // (色/太さ)を位置確定前に触りたい & 右列をスクロールしたいので、
        // カード列は生かしたまま確定バーだけ出す(ロック通知は出さない)。
        bool hardLocked = PreviewShowsUncropped;
        bool anyMode = hardLocked || _isDecalPlacementModeActive || _isMaskEditModeActive;
        CompositeCardsScrollViewer.IsEnabled = !hardLocked;
        SliderLockNotice.Visibility = hardLocked ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeConfirmBar.Visibility = anyMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>各モードのトグルが OFF→ON した瞬間のスナップショット。
    /// PreviewModeCancelButton_Click が、1ステップ undo ではなくその編集開始前の
    /// 状態へ正確に戻せる。</summary>
    private CompositeSnapshot? _cropModeEntrySnapshot;
    private CompositeSnapshot? _avatarPlacementModeEntrySnapshot;

    /// <summary>確定: 現在の設定を保ったままアクティブなモードを抜ける
    /// (トグルを直接オフにするのと同じ)。同時に有効なモードは1つだけなので、
    /// ここで両方を見て有効な方を処理する。</summary>
    private void PreviewModeConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        else if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
        else if (_isDecalPlacementModeActive) ExitDecalPlacementMode();
        else if (_isMaskEditModeActive) ConfirmMaskEdit();
    }

    /// <summary>キャンセル: モードをオンにしたときのスナップショットを1つの
    /// アトミックな undo ステップとして復元し(ApplyCompositeSnapshot を再利用)、
    /// そのあと 確定 と同じようにモードを抜ける。</summary>
    private void PreviewModeCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDecalPlacementModeActive)
        {
            CancelDecalPlacement();
            return;
        }
        if (_isMaskEditModeActive)
        {
            CancelMaskEdit();
            return;
        }
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

    // ---- 切り抜きモード: 切り抜き幅/位置X/Y スライダーではなくプレビュー上で
    //      切り抜き境界を直接ドラッグする。隅ドラッグ = リサイズ(アス比固定、
    //      対角隅アンカー)、本体ドラッグ = 移動。どちらも PreviewBorder.Width/
    //      photo.Width で写真ピクセル空間で動く。ドラッグ中は
    //      RenderCompositePreview が未切り抜きの写真全体を表示するので、その間
    //      PreviewBorder は写真の全域に 1:1 対応する。 ----

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

    /// <summary>固定比モードではドラッグの水平成分だけがリサイズを動かす ── 幅だけで
    /// 高さが決まるので垂直成分は冗長。自由モード(_canvasAspectRatio が null)では
    /// 縛る比が無いので dx と dy が各軸を独立に動かす。どちらの場合も、ドラッグ中の隅と
    /// 対角の隅をアンカーにする(リサイズ後にその隅から _canvasCropOffsetX/Y を
    /// 再導出して固定)。</summary>
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

    /// <summary>完成した合成を _canvasAspectRatio に切り抜く唯一の集約点 ──
    /// RenderCompositePreview/ComputeBeforeComposite が表示/保存/比較用の
    /// WriteableBitmap を作るたびに呼ばれるので、どの経路で作っても切り抜きが一致する。</summary>
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

    /// <summary>配置 の本体を折りたたむ/展開する(折りたたみ時はヘッダーのみ表示)。
    /// プレビュー画像の上に浮くので、配置が決まったら畳んで避けられるように。
    /// undo 対象外の表示設定。</summary>
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

    /// <summary>CompositeSkipAvatarButton の有効状態/ラベルを
    /// <see cref="_compositeSkipAvatar"/> に同期する ── フラグはボタンクリック以外に
    /// プログラム的にも変わる(LoadImageFile がアバター再読み込み時にクリア)ため必要。
    /// AvatarLookCard もグレーアウトし BlankCanvasButton を無効化する ──
    /// アバターなし と 背景なし は排他(残る排他判定は RefreshBlankCanvasActiveUI)。</summary>
    private void RefreshSkipAvatarUI()
    {
        CompositeSkipAvatarButton.IsEnabled = !_compositeSkipAvatar && !_isBlankCanvasActive;
        CompositeSkipAvatarButtonText.Text = _compositeSkipAvatar ? "アバターなしで進行中" : "アバターなしにする";
        AvatarLookCard.IsEnabled = !_compositeSkipAvatar;
        BlankCanvasButton.IsEnabled = !_compositeSkipAvatar;
    }

    /// <summary>アバターなし/背景なし 排他のもう半分 ── <see cref="_isBlankCanvasActive"/>
    /// が変わるたびに CreateBlankCanvasButton_Click と TryLoadPhotoPixels から呼ばれる。
    /// 背景なしキャンバス有効中は PhotoLookCard をグレーアウトし、
    /// CompositeSkipAvatarButton の有効条件を正しく保つ。</summary>
    private void RefreshBlankCanvasActiveUI()
    {
        PhotoLookCard.IsEnabled = !_isBlankCanvasActive;
        CompositeSkipAvatarButton.IsEnabled = !_compositeSkipAvatar && !_isBlankCanvasActive;
    }

    /// <summary>Undo/Redo で <see cref="ApplyCompositeSnapshot"/> が背景なし
    /// キャンバスのフィールドを書き戻したあと、色1/色2/グラデーション関連の
    /// コントロールをその値に合わせて一括同期する。</summary>
    private void RefreshBlankCanvasUI()
    {
        _suppressEventsDepth++;
        SyncBlankCanvasColorUI(_blankCanvasR, _blankCanvasG, _blankCanvasB);
        SyncBlankCanvasColor2UI(_blankCanvasR2, _blankCanvasG2, _blankCanvasB2);
        BlankCanvasColorSwatch.Background = new SolidColorBrush(Color.FromRgb(_blankCanvasR, _blankCanvasG, _blankCanvasB));
        BlankCanvasColor2Swatch.Background = new SolidColorBrush(Color.FromRgb(_blankCanvasR2, _blankCanvasG2, _blankCanvasB2));
        BlankCanvasGradientToggle.IsChecked = _blankCanvasGradientEnabled;
        BlankCanvasGradientDirectionSlider.Value = _blankCanvasGradientDirection;
        BlankCanvasGradientDirectionBox.Text = _blankCanvasGradientDirection.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        RefreshBlankCanvasGradientUI();
        RefreshBlankCanvasActiveUI();
        BlankCanvasColorPanel.Visibility = _isBlankCanvasActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>オーバーレイ描画済みビットマップの生 BGRA32 ピクセルを取り出す ──
    /// CompositeOverlayOntoPhoto が BitmapSource ではなく生ピクセルを取るように
    /// なる前に内部でやっていた処理(この変更でバックグラウンドスレッド実行が
    /// 安全になった)。配置サイズのビットマップ1枚の CopyPixels なので軽く、
    /// RenderOverlayForComposite 直後に UI スレッドのまま行う。</summary>
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

    /// <summary>最近傍ダウンスケール。スライダー/位置ドラッグ中のライブプレビュー
    /// 縮小専用。ドラッグ終了の瞬間にフル解像度で再計算される
    /// (<see cref="_isCompositeDragging"/> が保存品質を別途ゲート)ので品質は問わない。
    /// PreviewImage の Stretch="Uniform" が小さい結果をペインに合わせて拡大する。</summary>
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

    /// <summary>DownscalePixelBuffer の結果の1スロットキャッシュ。キーはソース
    /// バッファの参照 + ターゲットサイズ。スライダー/色ドラッグは同じ
    /// _photoPixelBuffer 参照・同じペインサイズで毎秒何度も RenderCompositePreview を
    /// 呼ぶので、縮小ループはドラッグの初回 tick(またはドラッグ中のリサイズ後)だけで済む。</summary>
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

    /// <summary>ライブプレビューをネイティブ解像度のこの割合未満では描かない下限
    /// (ドラッグプレビューがブロックノイズっぽく見えないため。正しさの要件ではない)。
    /// <see cref="DragPreviewOversample"/> はペインの DIP サイズに掛けて、正確な DPI
    /// スケールが分からないぶんの安全マージンにする。</summary>
    private const double MinDragPreviewScale = 0.2;
    private const double DragPreviewOversample = 2.0;

    /// <summary>1.0(フル解像度)を返す。ただし <paramref name="dragging"/> かつ
    /// プレビューペインがレイアウト済みで写真より小さいときを除く ── 色/配置/
    /// ドロップシャドウのスライダーはチェーンの先頭付近にあり下流を再利用できないので、
    /// ドラッグ中に毎 tick フル解像度で描くのがスライダーラグの残った原因だった。
    /// ペインは元々写真のごく一部の画素数でしか表示しない。</summary>
    private double ComputeDragPreviewRenderScale(bool dragging, int photoWidth, int photoHeight)
    {
        if (!dragging) return 1.0;
        double paneWidth = PreviewHost.ActualWidth * DragPreviewOversample;
        double paneHeight = PreviewHost.ActualHeight * DragPreviewOversample;
        if (paneWidth <= 0 || paneHeight <= 0 || photoWidth <= 0 || photoHeight <= 0) return 1.0;

        double fitScale = Math.Min(paneWidth / photoWidth, paneHeight / photoHeight);
        return Math.Clamp(fitScale, MinDragPreviewScale, 1.0);
    }

    /// <summary>RenderCompositePreview の冒頭でインクリメントし、各 await 後に
    /// 再チェックする ── ドラッグ中に前のバックグラウンドレンダーが終わる前に
    /// 再度呼ばれ得るので、遅い(古い)レンダーが後から結果を上書きしないようにする。
    /// RefreshRecentPhotosUI の _recentPhotosScanToken と同じパターン。</summary>
    private int _compositeRenderToken;

    /// <summary>RenderOverlayForComposite(WPF ビジュアルツリー依存、UI スレッド専用)の
    /// 出力をキャッシュし、オーバーレイのルック/配置/回転と無関係なスライダーで
    /// 発生したレンダーがこれをやり直さずに済むようにする。</summary>
    private readonly record struct CachedOverlayRender(
        BitmapSource? Source, double Width, double Height, double Rotation,
        byte[] Pixels, int Stride, int PixelsWidth, int PixelsHeight, double OffsetX, double OffsetY);

    private CachedOverlayRender? _cachedOverlayRender;

    /// <summary>合成を再計算して表示する: 写真(独立ルック適用)+ 位置合わせ済み PNG
    /// (共有の位置合わせモードルック、サイズ/回転を合わせる)を現在の配置で上に載せる。
    /// 片方だけ読み込まれている場合はそちらを単独表示する。色/配置ドラッグ中は
    /// (<see cref="_isCompositeDragging"/>)、ドラッグ終了まで保存品質の結果
    /// (<see cref="_lastComposite"/>)を更新しない。
    ///
    /// 実ピクセル処理(CompositeOverlayOntoPhoto + CropToAspect。UI スレッドに
    /// 残す必要のある RenderOverlayForComposite の WPF 描画を除く)は Task.Run 内で
    /// 走る。結果を待たない呼び出し元は await 不要、待つ呼び出し元
    /// (FinishMatchRender)は返り Task を await する。
    ///
    /// _compositeRenderGate は Task.Run 本体だけを直列化する: GPU パイプラインの
    /// キャッシュや ComputeSharp のコマンド送出は「同時に1レンダーのみ」を前提に
    /// 作られており、2本の Task.Run が別スレッドで並走するスレッド安全性は無い。
    /// ゲート取得直後にトークンを再チェックし、待機中に古くなったレンダーは
    /// 無駄な処理をスキップする。</summary>
    private readonly SemaphoreSlim _compositeRenderGate = new(1, 1);

    private async Task RenderCompositePreview()
    {
        int token = ++_compositeRenderToken;
        var overlaySource = _compositeSkipAvatar ? null : _overlayWindow.AdjustedPngSource;

        if (_photoPixelBuffer is not { } photoBuffer)
        {
            // 写真未読み込み ── プレビューを空にせず、PNG があればそれだけ表示する。
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
        // 切り抜きモード / アバター配置モード 中は最終切り抜きをスキップする
        // (未確定の切り抜き端の近く/外への配置が見える必要がある)。デカール配置
        // モードはここに含めない: デカールは切り抜き済みキャンバス上に置く。
        bool cropAdjusting = PreviewShowsUncropped;

        if (overlaySource is null)
        {
            // アバター未読み込み ── アバターありのときと同じ仕上げ効果パイプラインに
            // 写真を通し、ブレンドだけスキップ(overlay = null)する。グレイン/ビネット/
            // グロー/ライトリーク/トーングラデ等が保存結果に効く。
            double photoOnlyScale = ComputeDragPreviewRenderScale(dragging, photoBuffer.Width, photoBuffer.Height);
            var renderPhotoBuffer = photoOnlyScale < 1.0
                ? GetDownscaledPhoto(photoBuffer, (int)Math.Round(photoBuffer.Width * photoOnlyScale), (int)Math.Round(photoBuffer.Height * photoOnlyScale))
                : photoBuffer;
            var photoAdjustments = PhotoAdjustments;
            var snap = CaptureCompositeSnapshot();
            var behindDecalsNoAvatar = CaptureBehindAvatarDecals(photoOnlyScale, dragging);
            var frontDecalsNoAvatar = CaptureInFrontOfAvatarDecals(photoOnlyScale, dragging);
            renderPhotoBuffer = ApplyBehindAvatarDecals(renderPhotoBuffer, behindDecalsNoAvatar, photoOnlyScale, snap.PhotoLook.PhotoBlurAmount, photoOnlyScale);
            double effectivePhotoBlurAmountNoAvatar = EffectivePhotoBlurAmount(snap.PhotoLook.PhotoBlurAmount, behindDecalsNoAvatar);
            var maskPlanNoAvatar = BuildMaskPlan();
            var maskCropNoAvatar = GetCanvasCropRect(photoBuffer.Width, photoBuffer.Height);

            await _compositeRenderGate.WaitAsync();
            WriteableBitmap after;
            try
            {
                if (token != _compositeRenderToken) return; // ゲート待ちの間に新しいレンダーに置き換わった
                after = await Task.Run(() =>
                {
                    // アバター不在なので変種インデックスは無視。
                    WriteableBitmap RunNoAvatar(ImageAdjustment.ColorAdjustments adj, double toneAmt, double leakAmt, int _) =>
                        ImageAdjustment.CompositeOverlayOntoPhoto(
                            renderPhotoBuffer, adj,
                            grainAmount: snap.Finish.GrainAmount, vignetteAmount: snap.Finish.VignetteAmount,
                            photoBlurAmount: effectivePhotoBlurAmountNoAvatar, photoBlurScale: photoOnlyScale,
                            softnessAmount: snap.Finish.SoftnessAmount, sharpnessAmount: snap.Finish.SharpnessAmount, finishDetailScale: photoOnlyScale,
                            fadeAmount: snap.Finish.FadeAmount, glowAmount: snap.Finish.GlowAmount, glowScale: photoOnlyScale,
                            chromaticAberrationAmount: snap.Finish.ChromaticAberrationAmount, colorBleedAmount: snap.Finish.ColorBleedAmount,
                            scanlineAmount: snap.Finish.ScanlineAmount, vhsScale: photoOnlyScale,
                            clarityAmount: snap.Finish.ClarityAmount, clarityScale: photoOnlyScale,
                            lightLeakAmount: leakAmt, lightLeakAngle: snap.Finish.LightLeakAngle, lightLeakDistance: snap.Finish.LightLeakDistance,
                            lightLeakColorB: snap.Finish.LightLeakColorB, lightLeakColorG: snap.Finish.LightLeakColorG, lightLeakColorR: snap.Finish.LightLeakColorR,
                            toneGradientAmount: toneAmt, toneGradientRotation: snap.Finish.ToneGradientRotation,
                            toneGradientLightR: snap.Finish.ToneGradientLightR, toneGradientLightG: snap.Finish.ToneGradientLightG, toneGradientLightB: snap.Finish.ToneGradientLightB,
                            toneGradientDarkR: snap.Finish.ToneGradientDarkR, toneGradientDarkG: snap.Finish.ToneGradientDarkG, toneGradientDarkB: snap.Finish.ToneGradientDarkB);

                    var result = maskPlanNoAvatar.Count == 0
                        ? RunNoAvatar(photoAdjustments, snap.Finish.ToneGradientAmount, snap.Finish.LightLeakAmount, 0)
                        : BlendMasked(RunNoAvatar, photoAdjustments, snap.Finish.ToneGradientAmount, snap.Finish.LightLeakAmount,
                            maskPlanNoAvatar, new int[maskPlanNoAvatar.Count],
                            maskCropNoAvatar.Left, maskCropNoAvatar.Top, maskCropNoAvatar.Width, maskCropNoAvatar.Height, photoOnlyScale);
                    result = ApplyInFrontOfAvatarDecals(result, frontDecalsNoAvatar, photoOnlyScale);
                    return cropAdjusting ? result : ImageAdjustment.CropToAspect(result, snap.CanvasCrop.CanvasAspectRatio, snap.CanvasCrop.CanvasCropOffsetX, snap.CanvasCrop.CanvasCropOffsetY, snap.CanvasCrop.CanvasCropWidthPercent, snap.CanvasCrop.CanvasCropHeightPercent);
                });
            }
            finally
            {
                _compositeRenderGate.Release();
            }

            if (token != _compositeRenderToken) return; // 途中で新しいレンダーが始まった。結果は古い

            // ここでインライン再計算せず ComputeBeforeComposite を使う ── キャッシュ済みで、
            // このレンダーを起こした仕上げ効果スライダーに依存しない。
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

        // ドラッグ中でなければフル解像度(ComputeDragPreviewRenderScale 参照)。
        // _compositePlaceX/Y/Width/Height はフル解像度の写真ピクセル座標のまま
        // (配置/undo/保存が基準にする正準空間)。下の実レンダーに使う値だけ
        // renderPhotoBuffer と歩調を合わせて縮小する。
        double previewScale = ComputeDragPreviewRenderScale(dragging, photoBuffer.Width, photoBuffer.Height);
        var scaledPhotoBuffer = previewScale < 1.0
            ? GetDownscaledPhoto(photoBuffer, (int)Math.Round(photoBuffer.Width * previewScale), (int)Math.Round(photoBuffer.Height * previewScale))
            : photoBuffer;
        var behindDecals = CaptureBehindAvatarDecals(previewScale, dragging);
        var frontDecals = CaptureInFrontOfAvatarDecals(previewScale, dragging);

        double placeLeft = _compositePlaceX * previewScale;
        double placeTop = _compositePlaceY * previewScale;
        double placeWidth = _compositePlaceWidth * previewScale;
        double placeHeight = _compositePlaceHeight * previewScale;

        // 実合成では位置合わせモードのスライダーに関わらず不透明度を 100% に固定する:
        // あのスライダーはライブの(不透明な)VRChat 背景に合わせるためのもので、
        // 出力に焼き込むものではない。ここは UI スレッドに残る(実 WPF ビジュアル
        // ツリーを描く)。以降は byte[]/PixelBuffer 処理だけで下の Task.Run へ移せる。
        //
        // このステップへの入力が変わらないレンダー間でキャッシュする ── 合成専用
        // スライダーの大半(グレイン、ビネット、写真ルック、切り抜き、全仕上げ効果)は
        // オーバーレイの配置/回転/ルックと無関係なのに、以前は毎レンダーこの
        // WPF 描画 + ピクセル抽出をやり直していた。overlaySource の参照は
        // OverlayWindow.ApplyImageAdjustments がアバターのルックを再処理したときだけ
        // 変わる ── GpuTexturePool.RentUploaded が写真バッファに使うのと同じ
        // 参照等価をキャッシュキーにする発想。
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
        scaledPhotoBuffer = ApplyBehindAvatarDecals(scaledPhotoBuffer, behindDecals, previewScale, fullSnap.PhotoLook.PhotoBlurAmount, previewScale);
        double effectivePhotoBlurAmount = EffectivePhotoBlurAmount(fullSnap.PhotoLook.PhotoBlurAmount, behindDecals);
        var maskPlan = BuildMaskPlan();
        var maskCrop = GetCanvasCropRect(photoBuffer.Width, photoBuffer.Height);

        // アバターの色調補正マスク: 色違いに焼いたアバターpixelを何枚か用意し、
        // BlendMasked では「どの変種を使うか」(0 = 中立)で表現する。UI スレッドで焼く
        // -- RenderOverlayForComposite が WPF ビジュアルを描くので Task.Run 内では不可。
        // アバターの色調補正マスクが1つも無ければ変種は overlayPixels 1枚だけ(従来動作)。
        var overlayVariants = new List<byte[]> { overlayPixels };
        var variantIndexPerGroup = new int[maskPlan.Count];
        if (maskPlan.Count > 0)
        {
            var avatarMasked = maskPlan.SelectMany(g => g.Targets).Where(IsAvatarTarget).Distinct().ToArray();
            if (avatarMasked.Length > 0 && _overlayWindow.EdgeBlurredPixelBuffer is { } blurredAvatar)
            {
                var fullAv = AvatarAdjustments;
                var neutralAv = avatarMasked.Aggregate(fullAv, WithAvatarTargetZeroed);
                byte[] RenderAvatarVariant(ImageAdjustment.ColorAdjustments av)
                {
                    var colored = ImageAdjustment.ApplyColor(blurredAvatar, av);
                    var (rendered, _, _) = ImageAdjustment.RenderOverlayForComposite(colored, placeWidth, placeHeight, _compositeRotation, 1.0);
                    var (px, _, _, _) = ExtractBgraPixels(rendered);
                    return px;
                }
                overlayVariants[0] = RenderAvatarVariant(neutralAv); // 変種0 = 中立
                for (int i = 0; i < maskPlan.Count; i++)
                {
                    var gAvT = maskPlan[i].Targets.Where(IsAvatarTarget).ToArray();
                    if (gAvT.Length == 0) { variantIndexPerGroup[i] = 0; continue; }
                    var gAv = gAvT.Aggregate(neutralAv, (a, t) => WithAvatarTargetRestored(a, fullAv, t));
                    overlayVariants.Add(RenderAvatarVariant(gAv));
                    variantIndexPerGroup[i] = overlayVariants.Count - 1;
                }
            }
        }
        var overlayVariantArr = overlayVariants.ToArray();

        // 仕上げ効果(フィルムグレイン、ビネット)は最終合成結果にだけ1回かける ──
        // レイヤーごとにかけると質感が二重になる。
        await _compositeRenderGate.WaitAsync();
        WriteableBitmap afterComposite;
        try
        {
            if (token != _compositeRenderToken) return; // ゲート待ちの間に新しいレンダーに置き換わった
            afterComposite = await Task.Run(() =>
            {
                WriteableBitmap RunAvatar(ImageAdjustment.ColorAdjustments adj, double toneAmt, double leakAmt, int variantIdx) =>
                    ImageAdjustment.CompositeOverlayOntoPhoto(
                        scaledPhotoBuffer, adj,
                        overlayVariantArr[variantIdx], overlayStride, overlayWidth, overlayHeight, overlayLeft, overlayTop,
                        fullSnap.Finish.GrainAmount, fullSnap.Finish.VignetteAmount, effectivePhotoBlurAmount, previewScale,
                        fullSnap.Finish.SoftnessAmount, fullSnap.Finish.SharpnessAmount, previewScale,
                        fullSnap.Finish.FadeAmount, fullSnap.Finish.GlowAmount, previewScale,
                        fullSnap.Finish.ChromaticAberrationAmount, fullSnap.Finish.ColorBleedAmount, fullSnap.Finish.ScanlineAmount, previewScale,
                        fullSnap.Finish.ClarityAmount, previewScale, leakAmt, fullSnap.Finish.LightLeakAngle, fullSnap.Finish.LightLeakDistance,
                        fullSnap.Finish.LightLeakColorB, fullSnap.Finish.LightLeakColorG, fullSnap.Finish.LightLeakColorR,
                        toneAmt, fullSnap.Finish.ToneGradientRotation,
                        fullSnap.Finish.ToneGradientLightR, fullSnap.Finish.ToneGradientLightG, fullSnap.Finish.ToneGradientLightB,
                        fullSnap.Finish.ToneGradientDarkR, fullSnap.Finish.ToneGradientDarkG, fullSnap.Finish.ToneGradientDarkB,
                        fullSnap.DropShadow.DropShadowAmount, fullSnap.DropShadow.DropShadowDirection, fullSnap.DropShadow.DropShadowDistance, fullSnap.DropShadow.DropShadowBlur,
                        fullSnap.DropShadow.DropShadowColorB, fullSnap.DropShadow.DropShadowColorG, fullSnap.DropShadow.DropShadowColorR, previewScale,
                        // トーン風(ハーフトーン)UIは削除済み: 常時オフのプレーンな影のみ。
                        false, 8, fullSnap.DropShadow.DropShadowBlendMode);

                var result = maskPlan.Count == 0
                    ? RunAvatar(fullPhotoAdjustments, fullSnap.Finish.ToneGradientAmount, fullSnap.Finish.LightLeakAmount, 0)
                    : BlendMasked(RunAvatar, fullPhotoAdjustments, fullSnap.Finish.ToneGradientAmount, fullSnap.Finish.LightLeakAmount,
                        maskPlan, variantIndexPerGroup, maskCrop.Left, maskCrop.Top, maskCrop.Width, maskCrop.Height, previewScale);
                result = ApplyInFrontOfAvatarDecals(result, frontDecals, previewScale);
                return cropAdjusting ? result : ImageAdjustment.CropToAspect(result, fullSnap.CanvasCrop.CanvasAspectRatio, fullSnap.CanvasCrop.CanvasCropOffsetX, fullSnap.CanvasCrop.CanvasCropOffsetY, fullSnap.CanvasCrop.CanvasCropWidthPercent, fullSnap.CanvasCrop.CanvasCropHeightPercent);
            });
        }
        finally
        {
            _compositeRenderGate.Release();
        }

        if (token != _compositeRenderToken) return; // 途中で新しいレンダーが始まった。結果は古い

        // 「ビフォー」はここで毎レンダー再計算しない ── 比較スライダーと無関係な
        // レンダーでも合成作業を倍にするのは、合成モードを開くときのラグの実測原因
        // だった。CompareSlider を実際に使うときだけ作る(ComputeBeforeComposite と
        // CompareSlider_ValueChanged が 0 から動いた初回に遅延構築する)。
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

    /// <summary>「ビフォー」比較合成を作る(_lastBeforeComposite 参照): 現在の
    /// 配置/回転だが、どちらのレイヤーにもルック調整も仕上げ効果もかけない。
    /// CompareSlider_ValueChanged からも独立して呼ばれるので自己完結
    /// (現在フィールドから配置/スケールを再導出)。RenderCompositePreview の
    /// 「アフター」計算と違い同期のまま ── 比較スライダー使用中しか要らない副次的な
    /// 半分なので、ドラッグ毎 tick で UI を止めていた原因ではない。
    ///
    /// 結果は写真・アバターの無調整ピクセル・配置/回転・切り抜きにのみ依存し、
    /// 色/仕上げ効果スライダーには依存しない。キーのいずれかが前回と実際に
    /// 違うときだけ再計算する自己無効化(PixelBuffer は byte[] を参照で比較)。</summary>
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
        // デカールはキーに含めない ── List<T> は参照比較なので、新しいキャプチャは
        // 前回と決して等しくならずキャッシュが無効化される。単純な正しい対処:
        // デカールが1つでも存在したらキャッシュを丸ごとスキップする
        // (_decalLayerOrder はアバターセンチネルを常に含むので > 1)。
        bool hasDecals = _decalLayerOrder.Count > 1;
        if (!hasDecals && _cachedBeforeCompositeKey == key && _cachedBeforeCompositeResult is not null)
        {
            return _cachedBeforeCompositeResult;
        }

        // RenderCompositePreview の Task.Run がバックグラウンドで走行中なら少し
        // ブロックする(このメソッドは async でないので WaitAsync でなく同期 Wait)。
        // このパスはキャッシュ済みで比較的稀なので短いブロックは許容範囲。
        _compositeRenderGate.Wait();
        WriteableBitmap result;
        try
        {
            var behindDecals = CaptureBehindAvatarDecals(1.0, dragging: false);
            var frontDecals = CaptureInFrontOfAvatarDecals(1.0, dragging: false);
            // 「ビフォー」は写真ぼかしをかけない(比較用の未加工写真)ので、
            // 背景ぼかしから背面デカールを除外する処理は不要。
            var decaledPhotoBuffer = ApplyBehindAvatarDecals(photoBuffer, behindDecals, 1.0, photoBlurAmount: 0, photoBlurScale: 1.0);

            if (_compositeSkipAvatar || _overlayWindow.RawPngSource is not { } rawOverlaySource)
            {
                // アバター未読み込み(または明示スキップ)── 「ビフォー」は未加工写真そのもの。
                var beforeResult = ImageAdjustment.CompositeOverlayOntoPhoto(decaledPhotoBuffer, default);
                beforeResult = ApplyInFrontOfAvatarDecals(beforeResult, frontDecals, 1.0);
                result = ApplyCanvasCrop(beforeResult);
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
                var beforeResult = ImageAdjustment.CompositeOverlayOntoPhoto(
                    decaledPhotoBuffer, default,
                    rawOverlayPixels, rawOverlayStride, rawOverlayWidth, rawOverlayHeight,
                    placeLeft - rawOffsetX, placeTop - rawOffsetY);
                beforeResult = ApplyInFrontOfAvatarDecals(beforeResult, frontDecals, 1.0);
                result = ApplyCanvasCrop(beforeResult);
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

    // ---- プレビュー上での Shift+ドラッグ(または常時のアバター配置モード)で
    //      アバターを移動する(_compositePlaceX/Y。スライダーUIは無い)。どちらかが
    //      有効な間、現在の配置をなぞるハイライト矩形 + リサイズ/回転ハンドルが出る。
    //      下の PreviewImage_MouseLeftButtonDown/Window_PreviewKeyDown 参照。 ----

    /// <summary>Shift 長押しの常時版: オンの間、アバターのバウンディングボックス +
    /// 隅ハンドル + 回転ギズモが出続け、プレビューのどこでもドラッグでアバターを
    /// 移動できる。_isCropModeActive と排他。</summary>
    private bool _isAvatarPlacementModeActive;

    private bool _isDraggingAvatarPlacement;
    private System.Windows.Point _avatarDragStartMouse;
    private double _avatarDragStartPlaceX, _avatarDragStartPlaceY;

    /// <summary>現在のキャンバス切り抜き矩形を元の(切り抜き前)写真ピクセルで返す ──
    /// ImageAdjustment.CropToAspect の計算と完全に一致させる。PreviewImage.Source は
    /// 切り抜き後の合成ビットマップ、_compositePlaceX/Y/Width/Height は切り抜き前
    /// 空間なので、スクリーン座標↔配置座標の変換は切り抜きオフセットを考慮しないと
    /// アス比有効時にハイライト/ドラッグがずれる。_canvasAspectRatio が null は
    /// 自由モード(各軸を独立に縮小する有効な切り抜き)。</summary>
    private (double Left, double Top, double Width, double Height) GetCanvasCropRect(int photoWidth, int photoHeight)
    {
        if (photoWidth <= 0 || photoHeight <= 0) return (0, 0, photoWidth, photoHeight);

        // ImageAdjustment.CropToAspect が実際に切る比フィット + ズーム + オフセット
        // 計算と同じなので、ここで描くハンドル/ガイドが確定した切り抜きからずれない。
        // (GetMaxCropSize は別: インタラクティブな隅ドラッグは 100% ズームの箱が要る。)
        var (left, top, cropWidth, cropHeight) = ImageAdjustment.ComputeCropRect(
            photoWidth, photoHeight, _canvasAspectRatio,
            _canvasCropOffsetX, _canvasCropOffsetY, _canvasCropWidthPercent, _canvasCropHeightPercent);
        return (left, top, cropWidth, cropHeight);
    }

    /// <summary>アバター/デカール配置のスクリーン↔写真座標変換が「PreviewBorder が
    /// 今表示している領域」として扱うべきもの: 切り抜きモード / アバター配置モード 中は
    /// 未切り抜きの写真全体(未確定の切り抜き端の近く/外へも配置できるように)、
    /// それ以外は実際のキャンバス切り抜き ── デカール配置モードも後者
    /// (切り抜き済みキャンバス上に置きたい)。RenderCompositePreview の
    /// `cropAdjusting` と常に一致させる。</summary>
    private (double Left, double Top, double Width, double Height) GetDisplayedCropRect(int photoWidth, int photoHeight) =>
        PreviewShowsUncropped
            ? (0, 0, photoWidth, photoHeight)
            : GetCanvasCropRect(photoWidth, photoHeight);

    /// <summary>写真に収まる _canvasAspectRatio 比の最大ボックス(100% ズーム)。
    /// インタラクティブな隅ドラッグ(CanvasCropHandle_MouseMove)が同じ比フィット
    /// 計算を再利用できるよう GetCanvasCropRect から切り出したもの。自由モード
    /// (_canvasAspectRatio が null)では写真全体サイズを返す。</summary>
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

    /// <summary>AvatarPlacementHighlight(と、アバター配置モード中のみ隅ハンドル +
    /// 回転ギズモ)を表示/非表示・配置する。Shift 押下中または
    /// _isAvatarPlacementModeActive で、合成モードが開いていてアバターが読み込まれて
    /// いるときに表示。Window_PreviewKeyDown/Up、AvatarPlacementModeToggle_Changed、
    /// RefreshCompositePlacementUI、各ドラッグの MouseMove から呼ばれる。</summary>
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

        // AvatarHandlesLayer の親は Canvas でなく Grid なので、レイヤー自体への
        // Canvas.SetLeft/Top は無視される。Grid セル内では Margin で動かす
        // (上の AvatarPlacementHighlight と同じ)。レイヤー内のハンドル位置
        // (PlaceAvatarHandle)はこのレイヤー自体が Canvas なので Canvas.Left/Top で正しい。
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

    // ---- アバター配置モード: 四隅リサイズハンドル(アス比固定、回転対応)+ 回転ギズモ。
    //      OverlayWindow の位置合わせモードのハンドル系(Handle_MouseMove 等)と同じだが、
    //      合成モードのプレビューは写真の縮小表示なので 1:1 スクリーンピクセルではなく
    //      PreviewBorder スケール・切り抜き考慮の座標で動く。CropHandleCorner を再利用。 ----

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

    /// <summary>アス比固定の隅リサイズ、回転対応: スクリーン空間のドラッグ差分を
    /// まずアバターのローカル軸へ逆回転し(OverlayWindow の Handle_MouseMove と同技法)、
    /// ドラッグした隅の対角線へ射影して連続スケール係数を1つ得る ── 対角線付近での
    /// 幅/高さ振動を避ける。</summary>
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
        if (dragScale <= 0) return; // 中心を越えてドラッグ。反転させず無視

        // 可能なら読み込んだ PNG のネイティブ縦横比に固定する(現 W/H は真の比率から
        // ずれている可能性がある)。
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

    /// <summary>切り抜き境界の暗転 + 枠線 + 隅ハンドルオーバーレイを表示/非表示・
    /// 配置する。<see cref="_isCropModeActive"/> の間表示。その間 PreviewImage.Source は
    /// 未切り抜きの合成に切り替わるので、ここでは PreviewBorder.Width が写真全体の幅に
    /// 対応する ── GetCanvasCropRect の他の呼び出し元とは逆なので、下のスケールは
    /// crop.Width ではなく photo.Width を読む。</summary>
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

        // 隅ハンドルは常時の切り抜きモードでのみ意味を持つ。表示だけの違いなので、
        // 4つとも同じフラグでここでまとめてゲートする。
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

        UpdateSplitGuides(); // 切り抜き枠の変更に分割線を追従させる
    }

    /// <summary>プレビュー限定ではなくウィンドウ全体: Shift の状態は、カーソル位置に
    /// 関わらず押下/解放の瞬間に分かる必要がある。TextBox 等にフォーカスがあっても
    /// 発火するよう KeyDown/Up ではなく PreviewKeyDown/Up。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => UpdateAvatarPlacementHighlight();

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => UpdateAvatarPlacementHighlight();

    // ---- プレビュー限定のビューポートズーム/パン: ホイールで拡大縮小、ドラッグでパン。
    //      ディテール確認用。PreviewImage 自体の RenderTransform で実装しており、
    //      _compositePlace* や合成ビットマップ・保存内容には一切触れない。
    //      写真ビューアのズームと同じで、アバターの移動/リサイズ手段ではない。 ----

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
            // 常に画像中心でズームするのではなく、カーソル下のピクセルを画面上で
            // 固定する。RenderTransformOrigin(0.5,0.5) では局所点 P が画面位置
            // O + zoom*(P-O) + Pan に写る。「ズーム前後でマウスの画面位置が
            // 変わらない」を新しい Pan について解くとこの更新式になる。
            // ズームアウト時はあえてこれをスキップし Pan を触らない(要望による)。
            var mouse = e.GetPosition(PreviewBorder);
            double originX = PreviewImage.ActualWidth / 2.0;
            double originY = PreviewImage.ActualHeight / 2.0;
            _previewPanX += (oldZoom - _previewZoom) * (mouse.X - originX);
            _previewPanY += (oldZoom - _previewZoom) * (mouse.Y - originY);
        }
        else
        {
            // 1x より上でのズームアウト: Pan をズームと同じ比率で縮める。ズーム中に
            // 表示範囲外へずれた視点が、スクロールアウトするにつれ連続的に中央へ戻る。
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

        if (TryStartDecalBodyDrag(e)) return;

        _isPanningPreview = true;
        // PreviewImage 自体ではなく PreviewBorder(RenderTransform を持たない)を
        // 基準に測る ── ズームの RenderTransform を持つ要素上の GetPosition は
        // 結果を現在のズームで割る(スケール前のローカル空間で報告する)一方、
        // 下の translate はスケール済み/画面空間で適用される。未変換の祖先を
        // 基準にすると、どのズームレベルでもマウスと 1:1 を保てる。
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
            // *_previewZoom(/ ではない): PreviewBorder 基準の生の画面 DIP マウス
            // 差分はズームに関わらず実際の画面移動と 1:1 だが、ズーム z ではその
            // 画面ピクセル数はより少ない配置ピクセルに対応する ── z を未ズームの
            // 表示スケールと一緒に分母に入れると、どのズームでもカーソル位置を追える。
            double scale = PreviewBorder.Width / crop.Width * _previewZoom;
            var current = e.GetPosition(PreviewBorder);
            _compositePlaceX = _avatarDragStartPlaceX + (current.X - _avatarDragStartMouse.X) / scale;
            _compositePlaceY = _avatarDragStartPlaceY + (current.Y - _avatarDragStartMouse.Y) / scale;
            RefreshCompositePlacementUI();
            ScheduleCompositeRender();
            return;
        }

        if (TryContinueDecalBodyDrag(e)) return;

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

        if (TryEndDecalBodyDrag()) return;

        _isPanningPreview = false;
        PreviewImage.ReleaseMouseCapture();
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => SizePreviewToImage();

    /// <summary>プレビューボックスを読み込み中の画像にアス比固定で合わせて縮める
    /// (レターボックスにしない)。未読み込みならプレビュー領域全体を埋める
    /// フォールバック、ホスト未レイアウトなら何もしない(SizeChanged が再試行する)。</summary>
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
            UpdateSplitGuides();
            return;
        }

        double maxWidth = PreviewHost.ActualWidth;
        double maxHeight = PreviewHost.ActualHeight;
        if (maxWidth <= 0 || maxHeight <= 0) return;

        // ぴったりではなく *0.96: 最大ズームアウト時に画像の周りへ少し余白を残し、
        // PreviewHost の端に接しないようにする。
        double scale = Math.Min(maxWidth / bmp.PixelWidth, maxHeight / bmp.PixelHeight) * 0.96;
        PreviewBorder.HorizontalAlignment = HorizontalAlignment.Center;
        PreviewBorder.VerticalAlignment = VerticalAlignment.Center;
        PreviewBorder.Width = bmp.PixelWidth * scale;
        PreviewBorder.Height = bmp.PixelHeight * scale;
        // CompareSlider は画像と同幅ではなく広くする: WPF の Track は 16px の Thumb を
        // 両端で半幅ぶんインセットするので、同幅だと Thumb 中心が画像端の 8px 手前で
        // 止まる。スライダー幅をその 16px ぶん広げると、Track の内部インセットで
        // Value 0/100 のとき Thumb 中心が画像の左右端にちょうど乗る。
        CompareSlider.Width = PreviewBorder.Width + CompareThumbDiameter;
        UpdateCompareSplitLine();
        UpdateSplitGuides();
    }

    private const double CompareThumbDiameter = 16.0;

    // ---- 写真ルックスライダーのドラッグ中は合成再レンダーをスロットルする
    //      (OverlayWindow の PNG 調整スロットルと同じ)。数メガピクセルの写真の
    //      フル再合成を毎 tick やると目に見えてカクつく。 ----

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
        _suppressEventsDepth++;
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
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoBrightnessSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoBrightnessSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoBrightnessSlider.Value = snapped;
        PhotoBrightnessBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoContrastSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoContrastSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoContrastSlider.Value = snapped;
        PhotoContrastBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoSaturationSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoSaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoSaturationSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoSaturationSlider.Value = snapped;
        PhotoSaturationBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoVibranceSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoVibranceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoVibranceSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoVibranceSlider.Value = snapped;
        PhotoVibranceBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoTemperatureSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoTemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoTemperatureSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoTemperatureSlider.Value = snapped;
        PhotoTemperatureBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoTintSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoTintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoTintSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoTintSlider.Value = snapped;
        PhotoTintBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoHueSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoHueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoHueSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoHueSlider.Value = snapped;
        PhotoHueBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoHighlightsSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoHighlightsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoHighlightsSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoHighlightsSlider.Value = snapped;
        PhotoHighlightsBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoShadowsSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoShadowsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoShadowsSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoShadowsSlider.Value = snapped;
        PhotoShadowsBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoWhitesSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoWhitesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoWhitesSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoWhitesSlider.Value = snapped;
        PhotoWhitesBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoBlacksSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoBlacksSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double snapped = SoftSnap(PhotoBlacksSlider.Value, 3, 0);
        _suppressEventsDepth++;
        PhotoBlacksSlider.Value = snapped;
        PhotoBlacksBox.Text = snapped.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        PhotoBlurSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(PhotoBlurSlider.Value);
        _suppressEventsDepth++;
        PhotoBlurBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _photoBlurAmount) return;
        _photoBlurAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- 仕上げ効果: フィルムグレイン + ビネット。最終合成結果にだけ1回かける
    //      (CompositeOverlayOntoPhoto の grainAmount/vignetteAmount)。アバター/写真
    //      ルックとは共有せず、上の写真ルックと同じ扱い。 ----

    private void RefreshFinishUI()
    {
        _suppressEventsDepth++;
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
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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
        _suppressEventsDepth++;
        GrainSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void GrainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(GrainSlider.Value);
        _suppressEventsDepth++;
        GrainBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _grainAmount) return;
        _grainAmount = rounded;
        ScheduleCompositeRender();
    }

    private void VignetteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(VignetteBox.Text, out var v) || v < 0) return;
        _vignetteAmount = v;
        _suppressEventsDepth++;
        VignetteSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void VignetteSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(VignetteSlider.Value);
        _suppressEventsDepth++;
        VignetteBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _vignetteAmount) return;
        _vignetteAmount = rounded;
        ScheduleCompositeRender();
    }

    private void SoftnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(SoftnessBox.Text, out var v) || v < 0) return;
        _softnessAmount = v;
        _suppressEventsDepth++;
        SoftnessSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void SoftnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(SoftnessSlider.Value);
        _suppressEventsDepth++;
        SoftnessBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _softnessAmount) return;
        _softnessAmount = rounded;
        ScheduleCompositeRender();
    }

    private void SharpnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(SharpnessBox.Text, out var v) || v < 0) return;
        _sharpnessAmount = v;
        _suppressEventsDepth++;
        SharpnessSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void SharpnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(SharpnessSlider.Value);
        _suppressEventsDepth++;
        SharpnessBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _sharpnessAmount) return;
        _sharpnessAmount = rounded;
        ScheduleCompositeRender();
    }

    private void FadeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(FadeBox.Text, out var v) || v < 0) return;
        _fadeAmount = v;
        _suppressEventsDepth++;
        FadeSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void FadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(FadeSlider.Value);
        _suppressEventsDepth++;
        FadeBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _fadeAmount) return;
        _fadeAmount = rounded;
        ScheduleCompositeRender();
    }

    private void GlowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(GlowBox.Text, out var v) || v < 0) return;
        _glowAmount = v;
        _suppressEventsDepth++;
        GlowSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void GlowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(GlowSlider.Value);
        _suppressEventsDepth++;
        GlowBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _glowAmount) return;
        _glowAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ChromaticAberrationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ChromaticAberrationBox.Text, out var v) || v < 0) return;
        _chromaticAberrationAmount = v;
        _suppressEventsDepth++;
        ChromaticAberrationSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ChromaticAberrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ChromaticAberrationSlider.Value);
        _suppressEventsDepth++;
        ChromaticAberrationBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _chromaticAberrationAmount) return;
        _chromaticAberrationAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ColorBleedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ColorBleedBox.Text, out var v) || v < 0) return;
        _colorBleedAmount = v;
        _suppressEventsDepth++;
        ColorBleedSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ColorBleedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ColorBleedSlider.Value);
        _suppressEventsDepth++;
        ColorBleedBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _colorBleedAmount) return;
        _colorBleedAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ScanlineBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ScanlineBox.Text, out var v) || v < 0) return;
        _scanlineAmount = v;
        _suppressEventsDepth++;
        ScanlineSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ScanlineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ScanlineSlider.Value);
        _suppressEventsDepth++;
        ScanlineBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _scanlineAmount) return;
        _scanlineAmount = rounded;
        ScheduleCompositeRender();
    }

    private void ClarityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ClarityBox.Text, out var v) || v < 0) return;
        _clarityAmount = v;
        _suppressEventsDepth++;
        ClaritySlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ClaritySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ClaritySlider.Value);
        _suppressEventsDepth++;
        ClarityBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _clarityAmount) return;
        _clarityAmount = rounded;
        ScheduleCompositeRender();
    }

    private void LightLeakBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakBox.Text, out var v) || v < 0) return;
        _lightLeakAmount = v;
        _suppressEventsDepth++;
        LightLeakSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void LightLeakSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(LightLeakSlider.Value);
        _suppressEventsDepth++;
        LightLeakBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _lightLeakAmount) return;
        _lightLeakAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- ライトリークの方向: 他行と同じ普通のスライダー(方向ダイヤルはアプリ全体で
    //      廃止)。ダイヤルの距離操作も無くなり、_lightLeakDistance は 1.0 固定。 ----

    private void LightLeakDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(LightLeakDirectionBox.Text, out var v)) return;
        _lightLeakAngle = Math.Clamp(v, 0, 360);
        _suppressEventsDepth++;
        LightLeakDirectionSlider.Value = _lightLeakAngle;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void LightLeakDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(LightLeakDirectionSlider.Value);
        _suppressEventsDepth++;
        LightLeakDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _lightLeakAngle) return;
        _lightLeakAngle = rounded;
        ScheduleCompositeRender();
    }

    // ---- ライトリークの色: ドロップシャドウと同じホイール+RGB ポップアップ
    //      (GetColorWheelBitmap 等は共有)。プリセット(暖色/寒色/白)と
    //      ポップアップ/ホイール/スライダー要素だけ専用。 ----

    private void LightLeakColorButton_Click(object sender, RoutedEventArgs e)
    {
        LightLeakColorWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncLightLeakColorUI(_lightLeakColorR, _lightLeakColorG, _lightLeakColorB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

        _suppressEventsDepth++;
        SyncLightLeakColorUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        LightLeakColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    private void ToneGradientBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientBox.Text, out var v) || v < 0) return;
        _toneGradientAmount = v;
        _suppressEventsDepth++;
        ToneGradientSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ToneGradientSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ToneGradientSlider.Value);
        _suppressEventsDepth++;
        ToneGradientBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _toneGradientAmount) return;
        _toneGradientAmount = rounded;
        ScheduleCompositeRender();
    }

    // ---- グラデーションの方向: 他行と同じ普通のスライダー(方向ダイヤルはアプリ全体で廃止)。 ----

    private void ToneGradientDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(ToneGradientDirectionBox.Text, out var v)) return;
        _toneGradientRotation = Math.Clamp(v, 0, 360);
        _suppressEventsDepth++;
        ToneGradientDirectionSlider.Value = _toneGradientRotation;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void ToneGradientDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(ToneGradientDirectionSlider.Value);
        _suppressEventsDepth++;
        ToneGradientDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _toneGradientRotation) return;
        _toneGradientRotation = rounded;
        ScheduleCompositeRender();
    }

    // ---- ドロップシャドウ: アバターのシルエットを複製し、方向にオフセットして
    //      ぼかし/着色(乗算ブレンド)する ── ImageAdjustment.ApplyDropShadow 参照。
    //      方向は他行と同じ普通のスライダー、幅(距離)は別の
    //      DropShadowDistanceSlider/Box。 ----

    private void DropShadowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowBox.Text, out var v) || v < 0) return;
        _dropShadowAmount = v;
        _suppressEventsDepth++;
        DropShadowSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void DropShadowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowSlider.Value);
        _suppressEventsDepth++;
        DropShadowBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _dropShadowAmount) return;
        _dropShadowAmount = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowDirectionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowDirectionBox.Text, out var v)) return;
        _dropShadowDirection = Math.Clamp(v, 0, 360);
        _suppressEventsDepth++;
        DropShadowDirectionSlider.Value = _dropShadowDirection;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void DropShadowDirectionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowDirectionSlider.Value);
        _suppressEventsDepth++;
        DropShadowDirectionBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _dropShadowDirection) return;
        _dropShadowDirection = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowDistanceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowDistanceBox.Text, out var v) || v < 0) return;
        _dropShadowDistance = v;
        _suppressEventsDepth++;
        DropShadowDistanceSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void DropShadowDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowDistanceSlider.Value);
        _suppressEventsDepth++;
        DropShadowDistanceBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _dropShadowDistance) return;
        _dropShadowDistance = rounded;
        ScheduleCompositeRender();
    }

    private void DropShadowBlurBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DropShadowBlurBox.Text, out var v) || v < 0) return;
        _dropShadowBlur = v;
        _suppressEventsDepth++;
        DropShadowBlurSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void DropShadowBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DropShadowBlurSlider.Value);
        _suppressEventsDepth++;
        DropShadowBlurBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

    /// <summary>OS 標準の色ダイアログではなく AvaSnap 風の自前ピッカー
    /// DropShadowColorPopup を開き、ホイール/明度/R-G-B を現在の色で初期化する。</summary>
    private void DropShadowColorButton_Click(object sender, RoutedEventArgs e)
    {
        DropShadowColorWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncColorPickerUI(_dropShadowColorR, _dropShadowColorG, _dropShadowColorB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        DropShadowColorPopup.IsOpen = true;
    }

    // ---- カラーホイール: 角度 = 色相、中心からの距離 = 彩度。140x140 ビットマップを
    //      一度だけ生成(明度 = 1 固定)。明度は別スライダーが色相/彩度の RGB を
    //      再スケールするだけなので、ホイール自体の再生成は不要。 ----

    private const int ColorWheelSize = 140;
    private WriteableBitmap? _colorWheelBitmap;
    private bool _isDraggingColorWheel;

    /// <summary>最後に選んだ色相/彩度(0..360 / 0..1)。彩度0(グレー/黒)では
    /// RGB だけで色相を表せないので、RGB とは別にキャッシュする。</summary>
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
                if (dist > 1.0) continue; // 円の外は透明のまま
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

    /// <summary>"#RRGGBB" または "RRGGBB"(先頭 "#" は任意)を受け付ける。それ以外
    /// (入力途中を含む)は無言で失敗するので、1文字ずつの入力を邪魔しない。</summary>
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

    /// <summary>RGB 三値から UI を同期するだけ(ホイールカーソル、明度/R/G/B、
    /// プレビュースウォッチ)── フィールド書き込みもレンダーもしない。
    /// SetDropShadowColor(実適用)と DropShadowColorButton_Click(オープン時の
    /// 初期化)で共有する。</summary>
    private void SyncColorPickerUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _dropShadowSat = s;
        // 彩度0(グレー/黒)では色相が未定義 ── 0(赤)にスナップせず最後の
        // 有効な色相を保ち、明度をグレーへ下げてもホイールカーソルが飛ばないようにする。
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

    /// <summary>影の色が変わる全経路(プリセット、ホイール、明度、R/G/B)の
    /// 唯一の集約点 ── どのコントロール発でも、ポップアップとメインボタンの
    /// スウォッチと _dropShadowColor* フィールドを互いに同期させる。</summary>
    private void SetDropShadowColor(byte r, byte g, byte b)
    {
        _dropShadowColorR = r;
        _dropShadowColorG = g;
        _dropShadowColorB = b;

        _suppressEventsDepth++;
        SyncColorPickerUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        DropShadowColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    // ---- グラデーション 明色/暗色: ドロップシャドウと同じホイール+RGB ポップアップ。
    //      今は常時自動計算ではなく手動(自動再判定は ToneGradientAutoDetectButton_Click)。 ----

    private void ToneGradientLightColorButton_Click(object sender, RoutedEventArgs e)
    {
        ToneGradientLightColorWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncToneGradientLightColorUI(_toneGradientLightR, _toneGradientLightG, _toneGradientLightB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

        _suppressEventsDepth++;
        SyncToneGradientLightColorUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        ToneGradientLightSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    private void ToneGradientDarkColorButton_Click(object sender, RoutedEventArgs e)
    {
        ToneGradientDarkColorWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncToneGradientDarkColorUI(_toneGradientDarkR, _toneGradientDarkG, _toneGradientDarkB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

        _suppressEventsDepth++;
        SyncToneGradientDarkColorUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

        ToneGradientDarkSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ScheduleCompositeRender();
    }

    /// <summary>以前は毎レンダー自動で行っていた重み付き全画像抽出
    /// (GpuToneGradient 参照)を、現在の手動 明色/暗色 を上書きする一発アクションとして
    /// 実行する。現在の写真バッファに対して走る ── 未読み込みなら何もしない。</summary>
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

    // ---- スポイト: 色行のピペットボタンを押してからプレビュー上をクリックすると
    //      そのピクセルをサンプルして押した行に適用する。サンプル元はアプリ内の
    //      プレビュー画像のみ(画面全体ではない)── OS の画面キャプチャ権限が不要。 ----

    private enum ColorPickTarget { None, DropShadow, LightLeak, AvatarTint, PhotoTint, ToneGradientLight, ToneGradientDark, BlankCanvas, BlankCanvas2, ShapeDecal }

    private ColorPickTarget _colorPickTarget = ColorPickTarget.None;

    /// <summary>同じ行のスポイトを再度クリックすると再アームせずキャンセルする ──
    /// そうでないと不要な色を拾わずに抜ける手段が無い。</summary>
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

    /// <summary>スクリーン座標(PreviewBorder 基準)をソースビットマップのピクセル
    /// 座標に変換する。PreviewImage_MouseWheel のズーム/パン変換を逆算し
    /// (P = O + (screen - O - Pan) / zoom)、その P を表示スケールで割って生の画像
    /// ピクセルに落とす。実ピック(TryPickColorAtClick)と、クリック前にカーソルを
    /// 追う拡大鏡プレビューで共有する。</summary>
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

        _undo.BeginChange(); // スポイトでの色確定を1 Undo ステップに(全ターゲット共通)
        switch (target)
        {
            case ColorPickTarget.DropShadow: SetDropShadowColor(r, g, b); break;
            case ColorPickTarget.LightLeak: SetLightLeakColor(r, g, b); break;
            case ColorPickTarget.AvatarTint: SetCompositeColorTint(r, g, b); break;
            case ColorPickTarget.PhotoTint: SetPhotoColorTint(r, g, b); break;
            case ColorPickTarget.ToneGradientLight: SetToneGradientLightColor(r, g, b); break;
            case ColorPickTarget.ToneGradientDark: SetToneGradientDarkColor(r, g, b); break;
            case ColorPickTarget.BlankCanvas: SetBlankCanvasColor(r, g, b); break;
            case ColorPickTarget.BlankCanvas2: SetBlankCanvasColor2(r, g, b); break;
            case ColorPickTarget.ShapeDecal: SetShapeDecalColor(r, g, b); break;
        }
        _undo.CommitChange();
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

    // ---- 拡大鏡: 色ピックがアーム中にカーソルを追う小さなルーペ。クリック前に
    //      どのピクセルをサンプルするか正確に見える。Popup ではなく Adorner
    //      (ConnectorAdorner と同じ理由でこの窓に閉じ、カードの DropShadowEffect の
    //      z 順クセの影響を受けない)。PreviewImage ではなく PreviewBorder に付ける:
    //      PreviewBorder は RenderTransform を持たないので、そのアドーナー座標系が
    //      e.GetPosition(PreviewBorder) と直接一致する。 ----

    private const int MagnifierSourcePixels = 9; // 奇数: 真ん中のピクセルが取れる
    private const double MagnifierCellSize = 12; // サンプル1ピクセルをこの DIP 幅で描く
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

        // 実際にサンプルされる中央セルを枠取りし、どれが対象か迷わないようにする。
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

    /// <summary>初回レイアウト前(ActualHeight がまだ 0)のルーペ高さのフォールバック。</summary>
    private const double MagnifierEstimatedHeight = 150;

    /// <summary>screen は e.GetPosition(PreviewBorder) ── アドーナーが描く座標系と
    /// 同じなので Canvas.Left/Top にそのまま使える。ルーペが拡大対象のカーソルの
    /// 下に来ないよう、カーソルの右上にアンカーする。</summary>
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

    // ---- ティント(色被せ): アバタールックカード用と写真ルックカード用の独立した
    //      2つの色ピッカー。上の DropShadowColor と同じホイール/明度/RGB。アバター側は
    //      _state.ColorTint*(OverlayState)へ直接書くので OverlayWindow のライブ
    //      プレビューが自動で拾う。写真側は PhotoAdjustments に渡すローカルの
    //      _photoColorTint* へ書く。 ----

    private bool _isDraggingAvatarColorTintWheel;

    private void CompositeColorTintButton_Click(object sender, RoutedEventArgs e)
    {
        CompositeColorTintWheel.Source = GetColorWheelBitmap();
        _suppressEventsDepth++;
        SyncCompositeColorTintUI(_state.ColorTintR, _state.ColorTintG, _state.ColorTintB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

    /// <summary>SetCompositeColorTint/SetPhotoColorTint の一括調整 相互伝播が
    /// 無限再帰しないためのガード(リンク中は互いを呼ぶ)。先に来た方の呼び出しの間
    /// セットされ、相手側の伝播ステップを no-op にする。</summary>
    private bool _suppressColorTintLinkSync;

    /// <summary>アバターのティント色が変わる全経路の唯一の集約点。_state へ直接
    /// 書くので、明示的な ScheduleCompositeRender() は不要(_state.PropertyChanged
    /// 購読がやる)。一括調整 オン中は強度だけでなく色自体も写真側へミラーする。</summary>
    private void SetCompositeColorTint(byte r, byte g, byte b)
    {
        _state.ColorTintR = r;
        _state.ColorTintG = g;
        _state.ColorTintB = b;

        _suppressEventsDepth++;
        SyncCompositeColorTintUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

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
        _suppressEventsDepth++;
        SyncPhotoColorTintUI(_photoColorTintR, _photoColorTintG, _photoColorTintB);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
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

    /// <summary>写真のティント色が変わる全経路の唯一の集約点 ── ただのフィールド
    /// (OverlayState ではない)なので、SetCompositeColorTint と違い明示的に
    /// レンダーと UI 同期を行う。一括調整 オン中は色をアバター側へもミラーする。</summary>
    private void SetPhotoColorTint(byte r, byte g, byte b)
    {
        _photoColorTintR = r;
        _photoColorTintG = g;
        _photoColorTintB = b;

        _suppressEventsDepth++;
        SyncPhotoColorTintUI(r, g, b);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);

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
        _suppressEventsDepth++;
        PhotoColorTintStrengthSlider.Value = v;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        ScheduleCompositeRender();
    }

    private void PhotoColorTintStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(PhotoColorTintStrengthSlider.Value);
        _suppressEventsDepth++;
        PhotoColorTintStrengthBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _photoColorTintStrength) return;
        double delta = rounded - _photoColorTintStrength;
        _photoColorTintStrength = rounded;
        if (_lookLinked && delta != 0) _state.ColorTintStrength = Math.Clamp(_state.ColorTintStrength + delta, 0, 100);
        ScheduleCompositeRender();
    }

    // ---- スクショ監視フォルダ: 既定は VRChat の標準保存先。手動で上書き可能。 ----

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
