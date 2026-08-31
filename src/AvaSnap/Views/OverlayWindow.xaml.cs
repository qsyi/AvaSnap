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

    public OverlayWindow(OverlayState state, UndoManager undo, VrChatOscListener oscListener)
    {
        InitializeComponent();
        _state = state;
        _undo = undo;
        _oscListener = oscListener;

        // 仮想スクリーン全体(全モニタ)をその原点で覆う。Canvas 座標が
        // モニタ配置に関わらず画面座標と 1:1 になるように。
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

        // 自動非表示: VRChat のカメラ UI が開いている間だけ意味がある(それ以外は
        // ただの浮遊画像)。定期スクショ + テンプレートマッチではなく VRChat の
        // /usercamera/Mode OSC 出力で駆動する。
        _oscListener.CameraModeChanged += OnCameraModeChanged;
        if (_oscListener.IsCameraOpen is { } open) ApplyCameraOpenState(open);
    }

    private void OnCameraModeChanged(bool open) => Dispatcher.Invoke(() => ApplyCameraOpenState(open));

    private bool _manuallyHidden;

    /// <summary>ControlPanelWindow が最小化/コンパクト時に呼ぶ。その間に VRChat の
    /// カメラ UI を開き直してもオーバーレイを隠したままにする(これが無いと
    /// ApplyCameraOpenState の Show() が最小化と競合して不意にオーバーレイが前面へ出る)。
    /// 解除時はカメラの「現在の」状態へ同期し直す(抑制中に変わっているかもしれない)。</summary>
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

    /// <summary>起動時にオーバーレイの初期表示を VRChat の実際のカメラ UI 状態に合わせる
    /// (常時表示を既定にしない)。VRChat は /usercamera/Mode を「変化時」しか送らないので、
    /// まだ OSC が何も報告していなければ状態不明 ── 推測せず、最初の OSC が来るまで隠す。</summary>
    public void InitializeCameraVisibility()
    {
        if (_oscListener.IsCameraOpen is { } open)
        {
            ApplyCameraOpenState(open);
            return;
        }
        Hide();
    }

    /// <summary>上の VRChat フォーカスポーリングでは拾えないケース用: オーバーレイ自体を
    /// クリック(Shift ドラッグ等)するとこの窓がアクティブになり、直後は AvaSnap が
    /// 前面窓になる。同じ物理キー押下での他の Ctrl+Z 経路との重複は UndoManager の
    /// デバウンスが防ぐ。</summary>
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

    /// <summary>pristine な画像を BGRA32 生バッファへ変換したもの。読み込み時に1回だけ作る。
    /// ルック調整は常にこれから再処理する(調整済み結果からではない)ので、繰り返しても劣化しない。</summary>
    private ImageAdjustment.PixelBuffer? _originalPixelBuffer;

    /// <summary>フル解像度のぼかしステージの結果。色ステージとは別にキャッシュする:
    /// 再ぼかしが重いので、エッジぼかし半径が変わった時だけ作り直す。</summary>
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

        // ボックスの Width/Height は読み込みで変わらないが、Stretch="Fill" だと
        // アス比の違う新画像が歪むので、次の Snap を待たず高さを合わせる。
        // マウスホイールズームと同じくボックスの中心基準で調整する(左上固定だと
        // Snap で再センタリングされる前に画像が飛んで見える)。
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

    // ---- スライダードラッグ中の画像再処理をスロットルする: tick ごとに再ぼかし +
    //      再色調整すると目に見えて遅れる。最大でも AdjustmentThrottle 間隔で処理し、
    //      末尾更新も必ず予約して、止めた直後に最終値が描画されるようにする。 ----

    private static readonly TimeSpan AdjustmentThrottle = TimeSpan.FromMilliseconds(80);
    private DateTime _lastAdjustmentApply = DateTime.MinValue;
    private DispatcherTimer? _pendingAdjustmentTimer;

    // ---- 色調整もエッジぼかしも、ドラッグ中フル解像度バッファを毎 tick 再処理する
    //      (上の AdjustmentThrottle でスロットル)。エッジぼかしは GPU
    //      (GpuAvatarEdgeBlur)なので、他の調整と同じ扱いでよく、ドラッグ中フリーズ
    //      させて離した時だけ追いつかせる、という分岐はもう不要。 ----

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
        // PropertyName が null は OverlayState の「複数変わった」慣習。どれかが調整
        // プロパティかもしれないので、bail out せず関連扱いにする(でないとバッチ
        // Undo/Redo・ルック一致・リセット後に色再処理が二度と走らなくなる)。
        if (e.PropertyName is not null && !AdjustmentPropertyNames.Contains(e.PropertyName)) return;

        var elapsed = DateTime.UtcNow - _lastAdjustmentApply;
        if (elapsed >= AdjustmentThrottle)
        {
            _pendingAdjustmentTimer?.Stop();
            _lastAdjustmentApply = DateTime.UtcNow;
            ApplyImageAdjustments();
            return;
        }

        // 前回適用から早すぎる ── スロットル窓が過ぎた時点の値で末尾適用を予約する。
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

    /// <summary>pristine な読み込み画像を現在のルック調整で再処理して表示する。
    /// 読み込み時と調整値が変わるたびに呼ばれる(位置/サイズ/回転では画像自体の
    /// 再処理は不要)。ぼかしは半径が変わった時だけ作り直し、それ以外は色だけ再適用する。</summary>
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

    /// <summary>読み込んだ画像のネイティブピクセルサイズ。未読み込みなら null。
    /// 検出した VRChat 枠へ縦横比を保ってフィット(引き伸ばしではない)させるのに使う。</summary>
    public Size? ImageNativeSize =>
        OverlayImage.Source is BitmapSource bmp ? new Size(bmp.PixelWidth, bmp.PixelHeight) : null;

    /// <summary>現在表示中の PNG。エッジぼかし/明るさ/コントラスト/彩度などの
    /// ルック調整を適用済み(<see cref="ApplyImageAdjustments"/> 参照)。合成モードが
    /// 元画像ではなくこの見た目をそのまま写真上に描くのに使う。</summary>
    public BitmapSource? AdjustedPngSource => OverlayImage.Source as BitmapSource;

    /// <summary>エッジぼかしだけ適用した(色調補正前の)アバターバッファ。合成モードで
    /// マスクによりアバターの色調補正を空間的に効かせる時、色違いを数枚焼くための元。
    /// <see cref="ApplyImageAdjustments"/> と同じ遅延キャッシュ(半径が変われば焼き直す)。</summary>
    public ImageAdjustment.PixelBuffer? EdgeBlurredPixelBuffer
    {
        get
        {
            if (_originalPixelBuffer is null) return null;
            if (_blurredPixelBuffer is null || _blurredAtRadius != _state.EdgeBlurRadius)
            {
                _blurredPixelBuffer = ImageAdjustment.BlurPng(_originalPixelBuffer, _state.EdgeBlurRadius);
                _blurredAtRadius = _state.EdgeBlurRadius;
            }
            return _blurredPixelBuffer;
        }
    }

    /// <summary>ルック調整を一切かけていない pristine な PNG(エッジぼかしも色グレードも
    /// 無し)。合成モードの比較用スライダーの「before」側に使う。</summary>
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

    /// <summary>pristine な PNG の生ピクセルバッファ。<see cref="RawPngSource"/> と
    /// 同じソースだが WriteableBitmap の往復なし。「ルック一致」ボタンが
    /// ComputeLookStats を直接かけるのに使う。</summary>
    public ImageAdjustment.PixelBuffer? OriginalPixelBuffer => _originalPixelBuffer;

    /// <summary>ガイドに関係する OverlayState プロパティ ── UpdateGuide の入力が実際に
    /// 依存するのはこれだけ(FOV/pitch/roll は GuideManualFov/Pitch/Roll から直接、
    /// GuideVisible でゲート)。他のプロパティ(位置/サイズ/回転/不透明度/ルック調整)は
    /// ガイドに一切影響しないので、それらの変化のたびに ~20 本の Line を作り直すのは無駄。</summary>
    private static readonly HashSet<string?> GuideRelevantPropertyNames = new()
    {
        nameof(OverlayState.GuideVisible),
        nameof(OverlayState.GuideManualFov), nameof(OverlayState.GuideManualPitch), nameof(OverlayState.GuideManualRoll),
    };

    /// <summary><paramref name="changedProperty"/> はこの呼び出しを起こした
    /// OverlayState プロパティ。null は「不明/複数変わった」(初回 Loaded、バッチ通知)で、
    /// 見るべき単一プロパティが無いので全更新扱い。</summary>
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

        double gizmoHalf = 8; // RotateGizmoHandle の Width/Height(16)の半分
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

    // ---- Unity連携ガイド: FOV/pitch/roll は _state.GuideManualFov/Pitch/Roll だけから。
    //      Unity 取得成功時もこの3フィールドに直接書くので、この窓は
    //      UnityCameraGuideService を購読しない(通常の _state.PropertyChanged →
    //      UpdateGuide 経路で足りる)。ガイド表示中かどうかとは無関係。 ----

    /// <summary>追従中の VRChat 窓のクライアント矩形へ Unity連携ガイドを描き直す:
    /// アクティブな fov/pitch/roll から水平線(ピンホール式 y = h/2 + f*tan(pitch)、
    /// f = (h/2)/tan(fov/2))と、FOV 間隔の放射線/奥行き線。roll はグリッド全体を
    /// 消失点まわりの RotateTransform で回す(端点を手で回さない)。</summary>
    private void UpdateGuide()
    {
        if (!_state.GuideVisible)
        {
            UnityGuideFrameClip.Visibility = Visibility.Collapsed;
            return;
        }
        double fov = _state.GuideManualFov;
        double pitch = _state.GuideManualPitch;
        double roll = _state.GuideManualRoll;

        if (_lastKnownClientRect is not { Width: > 0, Height: > 0 } clientRect)
        {
            UnityGuideFrameClip.Visibility = Visibility.Collapsed;
            return;
        }

        // VRChat 窓全体ではなく推定カメラフレーム(位置をリセットでアバターを
        // 合わせるのと同じ矩形)にクリップ/サイズ合わせする。窓には撮影領域の外側に
        // UI(カメラ操作・枠)が含まれ、その外に透視ガイドを出しても無意味なため。
        // クリップは UnityGuideFrameClip(外側・未回転の要素)、その下の
        // UnityGuideCanvas はローカル (0,0) のまま roll の RenderTransform だけ持つ。
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
        // 最低 1 度にクランプ ── fov=0 だと f が Infinity になり、Infinity*tan(0)
        // (pitch=0 も普通)は Infinity ではなく NaN で、RenderTransformOrigin へ渡すと
        // クラッシュした。スライダーも Minimum="1" だが、同期値は Unity の
        // Camera.fieldOfView から来て範囲保証が無いので、ここのクランプが最終防波堤。
        double fovRad = Math.Max(fov, 1.0) * Math.PI / 180.0;
        double f = (h / 2.0) / Math.Tan(fovRad / 2.0);
        double horizonY = h / 2.0 + f * Math.Tan(pitch * Math.PI / 180.0);

        UnityGuideCanvas.RenderTransformOrigin = new Point(0.5, h > 0 ? horizonY / h : 0.5);
        UnityGuideRollRotate.Angle = -roll;

        // アプリのくすんだ青ではなく明るい黄緑。実際の VRChat の風景(肌色・室内照明等)
        // に重なるので、低コントラストのブランド色は埋もれる。自然界に少ない飽和色は
        // ほぼどんな背景でもはっきり読める。
        var lineBrush = new SolidColorBrush(Color.FromRgb(0xAD, 0xFF, 0x2F));
        lineBrush.Freeze();

        const double PrimaryThickness = 3.0; // 水平線: 他の全線の基準なので一目で「主線」に見えるように
        const double SecondaryThickness = 1.75; // 放射線/奥行き線
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
            // 放射線/奥行き線だけ破線。水平線を唯一の実線として際立たせる。
            if (!isPrimary) main.StrokeDashArray = secondaryDashArray;
            UnityGuideCanvas.Children.Add(main);
        }

        const double angleStepDeg = 15;
        const double maxAngleDeg = 75;

        // 下の各線は未回転 (w,h) ボックスの縁にちょうど届くよう定義してある。roll=0 なら
        // よいが、回転させると真の(軸並行)フレームの角に届かなくなる(回転矩形は元の角から
        // 内側へ引っ込む)。全「遠側」端点を必要以上に延ばし、UnityGuideFrameClip の
        // (軸並行・未回転の)クリップで余分を切ることで、roll に関わらず縁まで届く。
        const double ExtendFactor = 2.5;
        Point Extend(double x, double y) => new Point(
            centerX + (x - centerX) * ExtendFactor,
            horizonY + (y - horizonY) * ExtendFactor);

        // 放射線には Extend が正しい(原点から一定角で伸びるので、両軸を同じ係数で
        // スケールしても角度は保たれる)。奥行き行と水平線は原点からの放射ではなく
        // 一定高さの水平線なので、Y も同じく延ばすと高さ自体が 2.5 倍ずれる。これらは
        // X の到達だけ延ばせばよい。
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

        // 奥行きの横線: 固定の角度ステップだと狭い FOV で f が大きくなり、最初の
        // 1本目から枠外に落ちて全行が消え、地平線だけになる。代わりに枠の各端へ
        // 届く実際の角度(distance-to-edge / f の atan)を求め、それを固定本数へ
        // 分割する。FOV に依らず必ず枠内に収まり、グリッドが常に埋まる。
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

    // ---- クリックスルー (WS_EX_TRANSPARENT) ----

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

    // ---- クリックスルーのホットキー: 既定は ON(クリックは VRChat へ通す)。Shift 押下中だけ
    //      一時 OFF にしてオーバーレイをドラッグ/リサイズでき、離すと元に戻る。
    //      クリックスルー中はこの窓が入力を受け取れず(WS_EX_TRANSPARENT の目的)、
    //      キーボードフォーカスは VRChat にあるので、システム全体のキー状態チェック
    //      (GetAsyncKeyState)でしか Shift を検出できない。 ----

    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool _lastShiftDown;

    // ---- VRChat にフォーカスがある間の Undo/Redo: Ctrl+Z が自前の PreviewKeyDown に
    //      届くのは AvaSnap の窓がキーボードフォーカスを持つときだけで、通常は
    //      VRChat が持っている。あえてグローバルホットキー(RegisterHotKey や
    //      低レベルキーフック)にはしない ── それだと Ctrl+Z をシステム全体で
    //      奪い、AvaSnap 起動中は他アプリの undo を壊す。ポーリング + 自前で
    //      フォアグラウンド窓を確認する方式はキー入力を消費しないので VRChat 外では
    //      無害。上の Shift クリックスルー監視と同じやり方。 ----

    private const int VK_CONTROL = 0x11;
    private const int VK_Z = 0x5A;
    private const int VK_Y = 0x59;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private DispatcherTimer? _hotkeyPollTimer;
    private bool _lastCtrlZDown, _lastCtrlShiftZDown, _lastCtrlYDown;

    /// <summary>Shift クリックスルーと undo/redo を1本のタイマーでまとめて監視する
    /// (どちらも1ティックあたり安価な P/Invoke チェック)。</summary>
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

            // 「押された瞬間」のエッジのみ、かつ VRChat がフォアグラウンドのときだけ。
            // AvaSnap 自身の窓は通常の PreviewKeyDown で Ctrl+Z を拾う。
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

    // ---- Z 順: グローバルな「常に最前面」ではなく VRChat 窓に紐付けて追従する ----

    /// <summary>このオーバーレイを VRChat 窓の owned window にし
    /// (<see cref="WindowOwnership"/> 参照)、位置追従を開始する。</summary>
    public void AttachToOwner(IntPtr ownerHwnd)
    {
        WindowOwnership.SetOwner(this, ownerHwnd);
        StartFollowing(ownerHwnd);
    }

    // ---- 追従: VRChat 窓が動かされてもオーバーレイの相対位置を固定し続ける。
    //      EVENT_OBJECT_LOCATIONCHANGE をフックして OS から移動即時に通知を受ける
    //      (体感ラグ無し)。SetParent で他プロセスの窓ツリーに強制ペアレントする
    //      方式は外部トップレベル窓のホストを想定しておらず不安定なので避ける。
    //      遅いセーフティネットタイマーが、負荷時に WinEvent を取りこぼしたレアケースを拾う。 ----

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

    /// <summary>追従中の VRChat 窓のクライアント矩形(最後の移動/リサイズ時点)を
    /// WPF の DIP 座標系で。都度取得せずキャッシュする ── 「今 VRChat がだいたい
    /// どこか」だけ要る呼び出し元は、自前の FindVRChatWindow() + P/Invoke ではなく
    /// これを使う。窓ドラッグ中に毎 PropertyChanged で問い合わせると UI スレッドが
    /// 詰まり、オーバーレイが目に見えてカクつく。</summary>
    public Rect? FollowedClientRect => _lastKnownClientRect;

    /// <summary>現在追従中の VRChat 窓。未アタッチなら null。</summary>
    public IntPtr? FollowedHwnd => _followedHwnd;

    private IntPtr? _followedHwnd;
    private Rect? _lastKnownClientRect;
    private DispatcherTimer? _followSafetyNetTimer;
    private IntPtr _winEventHook;
    private WinEventDelegate? _winEventDelegate; // 保持必須: ネイティブ側がポインタを持つ間に GC されないように

    private void StartFollowing(IntPtr hwnd)
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _followedHwnd = hwnd;
        _lastKnownClientRect = VRChatWindowService.GetClientRectInDips(hwnd);

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
        if (idObject != OBJID_WINDOW || idChild != 0) return; // 子要素は無視、窓自体のみ
        if (_followedHwnd is not { } followed || hwnd != followed) return;
        FollowTick();
    }

    /// <summary>追従中の VRChat 窓のクライアント「サイズ」が変わったとき発火
    /// (リサイズ/最大化/復元)。既知の hwnd と最新のクライアント矩形を渡すので、
    /// 購読側は自前の FindVRChatWindow() スキャン(これが以前オーバーレイを
    /// カクつかせた原因)なしで反応できる。</summary>
    public event Action<IntPtr, Rect>? ClientResized;

    /// <summary>VRChat 窓の画面位置を追い、左上隅が動いたぶんだけオーバーレイを
    /// ずらす。サイズ自体が変わったら <see cref="ClientResized"/> を発火する
    /// (自動リスケールはここでは行わず、呼び出し側が判断する)。座標は DIP。</summary>
    private void FollowTick()
    {
        if (_followedHwnd is not { } hwnd) return;
        var current = VRChatWindowService.GetClientRectInDips(hwnd);
        if (current is null) return; // 窓が閉じた/最小化。オーバーレイは今の位置に残す

        if (_lastKnownClientRect is { } last)
        {
            double dx = current.Value.X - last.X;
            double dy = current.Value.Y - last.Y;
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

    // ---- マウス操作: 左ドラッグ = 移動 ----

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

    // ---- リサイズハンドル: 四隅グリップ。回転対応(スクリーン空間のマウス移動を
    //      オーバーレイのローカル空間へ逆回転してから適用するので、回転中でも隅ドラッグが
    //      オーバーレイ自身の軸に沿ってリサイズされる)。 ----

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
        e.Handled = true; // OverlayImage の移動ドラッグハンドラへバブルさせない
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

        // スクリーン空間のドラッグ差分をオーバーレイのローカル軸へ逆回転する。
        double rad = -_state.RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double localDx = screenDx * cos - screenDy * sin;
        double localDy = screenDx * sin + screenDy * cos;

        bool left = _activeHandleTag.Contains('L');
        bool top = _activeHandleTag.Contains('T');

        // アスペクト固定の隅リサイズ、中心基準スケール。ドラッグを隅の対角線
        // (中心→隅の開始位置)へ射影して連続したスケール係数を1つ得る。幅駆動と
        // 高さ駆動の候補を毎フレーム比較する方式は対角線付近で振動する。
        double halfW0 = _handleStartWidth / 2;
        double halfH0 = _handleStartHeight / 2;
        double cornerDist0 = Math.Sqrt(halfW0 * halfW0 + halfH0 * halfH0);
        if (cornerDist0 <= 0) return;

        double dirX = (left ? -halfW0 : halfW0) / cornerDist0;
        double dirY = (top ? -halfH0 : halfH0) / cornerDist0;
        double projected = localDx * dirX + localDy * dirY;

        double scale = (cornerDist0 + projected) / cornerDist0;
        if (scale <= 0) return; // 中心を越えてドラッグ。反転させず無視

        // 可能なら読み込んだ画像のネイティブ縦横比に固定する。ボックスの現 W/H は
        // 真の比率からずれている可能性がある(丸めや過去の手編集)。
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

    // ---- 回転ギズモ: ボックス上のハンドルを中心まわりの弧に沿ってドラッグして回転。
    //      ソフトスナップ先はコントロールパネルの回転スライダーと同じ。 ----

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
