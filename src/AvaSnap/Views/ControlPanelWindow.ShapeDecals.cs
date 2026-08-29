using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- 図形デカール: 画像ファイルを使わず、コード側で「枠線」(写真の縁取り)を
//      ラスタライズして DecalLayer.Pixels へ流し込む(ShapeRasterizer)。
//      追加後は通常の画像デカールと同じ配置モード(移動+回転ギズモ、レイヤー
//      並べ替え、アバター前後)に乗る。違いは (1) DecalLayer.ShapeKind != null、
//      (2) リサイズは 角=アス比固定 / 辺=その軸だけ変更(DecalHandle_MouseMove
//      の分岐)、(3) サイズ/色/太さを変えると RerasterizeShapeDecal で焼き直す、
//      の3点。色UIはアプリ内の他のカラーピッカー(BlankCanvasColor 等)と同じ
//      色相環+明度+hex+スポイト+プリセットで、共通ヘルパー
//      (GetColorWheelBitmap/RgbToHsv/HsvToRgb/PositionColorWheelCursor/
//      TryParseHexColor)を再利用する。永続化/Undo 対象外なのも画像デカールと同じ。 ----
public partial class ControlPanelWindow
{
    // 図形デカールは「枠線(RectangleFrame)」のみ。
    private void AddRectangleFrameDecal_Click(object sender, RoutedEventArgs e)
    {
        if (_photoPixelBuffer is not { } photo) return;

        _undo.BeginChange(); // 追加(+未確定枠の入れ替え)を1 Undo ステップに

        // 確定前(配置モード中)に再度押したら、まだ確定していない枠線デカールは
        // 破棄してから追加し直す(RemoveDecal の Begin/Commit はこの外側にネスト
        // されて1ステップにまとまる)。
        if (_isDecalPlacementModeActive && _placingDecal is { ShapeKind: not null } pending)
            RemoveDecal(pending);

        var crop = GetDisplayedCropRect(photo.Width, photo.Height);

        // 「最大状態からちょっと小さく」= キャンバスの 90%(写真の縁取り用途)。
        // 追加後すぐキャンバス端までドラッグでき、それ以上は出せない
        // (DecalHandle_MouseMove / TryContinueDecalBodyDrag のクランプ)。
        double w = crop.Width * 0.9, h = crop.Height * 0.9, stroke = 1.5; // 「細い枠」なので線は細め

        var color = Colors.White;
        var (rw, rh) = ShapeRasterizer.RasterSizeFor(w, h);
        var pixels = ShapeRasterizer.Rasterize(ShapeKind.RectangleFrame, rw, rh, color, stroke);

        var decal = new DecalLayer
        {
            Pixels = pixels,
            Thumbnail = MakeShapeThumbnail(pixels),
            X = crop.Left + crop.Width / 2 - w / 2,
            Y = crop.Top + crop.Height / 2 - h / 2,
            Width = w,
            Height = h,
            ShapeKind = ShapeKind.RectangleFrame,
            ShapeColor = color,
            ShapeStrokePercent = stroke,
        };
        _decalLayerOrder.Add(decal);
        RebuildDecalStrip();
        EnterDecalPlacementMode(decal); // ここから UpdateShapeDecalPanel() も呼ばれる
        _undo.CommitChange();
    }

    /// <summary>今のサイズ/色/太さで図形バッファを焼き直して差し替える。
    /// リサイズ確定時(DecalHandle_MouseLeftButtonUp)と色/太さ変更時に呼ぶ。</summary>
    private void RerasterizeShapeDecal(DecalLayer decal)
    {
        if (decal.ShapeKind is not { } kind) return;
        var (rw, rh) = ShapeRasterizer.RasterSizeFor(decal.Width, decal.Height);
        decal.Pixels = ShapeRasterizer.Rasterize(kind, rw, rh, decal.ShapeColor, decal.ShapeStrokePercent);
        decal.Thumbnail = MakeShapeThumbnail(decal.Pixels);
        RebuildDecalStrip();
        ScheduleCompositeRender();
    }

    private static BitmapSource MakeShapeThumbnail(ImageAdjustment.PixelBuffer buffer)
    {
        var small = ImageAdjustment.Downscale(buffer, 128);
        var wb = new WriteableBitmap(small.Width, small.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, small.Width, small.Height), small.Pixels, small.Stride, 0);
        wb.Freeze();
        return wb;
    }

    // ---- 配置モード中に「デカール」カードへ出す図形プロパティパネル
    //      (枠線の色 + 太さ)。画像デカール配置中や配置モード外では隠す。 ----

    private void UpdateShapeDecalPanel()
    {
        // 不透明度は画像/枠線とも配置中は常に表示。
        if (_isDecalPlacementModeActive && _placingDecal is { } placing)
        {
            DecalOpacityRow.Visibility = Visibility.Visible;
            _suppressEvents = true;
            DecalOpacitySlider.Value = placing.Opacity * 100;
            _suppressEvents = false;
        }
        else
        {
            DecalOpacityRow.Visibility = Visibility.Collapsed;
        }

        if (_isDecalPlacementModeActive && _placingDecal is { ShapeKind: not null } decal)
        {
            ShapeDecalPanel.Visibility = Visibility.Visible;

            _suppressEvents = true;
            SyncShapeDecalColorUI(decal.ShapeColor.R, decal.ShapeColor.G, decal.ShapeColor.B);
            ShapeDecalStrokeSlider.Value = decal.ShapeStrokePercent;
            _suppressEvents = false;
            ShapeDecalColorSwatch.Background = new SolidColorBrush(decal.ShapeColor);
            // 位置確定前に色/太さを触れるよう、パネルを表示したら見える位置まで
            // スクロールする(カード列のロックは解除済み。RefreshSliderLockState)。
            Dispatcher.BeginInvoke(new Action(() => ShapeDecalPanel.BringIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            ShapeDecalPanel.Visibility = Visibility.Collapsed;
            ShapeDecalColorPopup.IsOpen = false;
        }
    }

    private void DecalOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (_placingDecal is not { } d) return;
        d.Opacity = Math.Clamp(e.NewValue / 100.0, 0, 1);
        ScheduleCompositeRender();
    }

    private double _shapeDecalHue, _shapeDecalSat;
    private bool _isDraggingShapeDecalColorWheel;

    private void ShapeDecalColorButton_Click(object sender, RoutedEventArgs e)
    {
        ShapeDecalColorWheel.Source = GetColorWheelBitmap();
        if (_placingDecal is { ShapeKind: not null } decal)
        {
            _suppressEvents = true;
            SyncShapeDecalColorUI(decal.ShapeColor.R, decal.ShapeColor.G, decal.ShapeColor.B);
            _suppressEvents = false;
        }
        ShapeDecalColorPopup.IsOpen = true;
    }

    private void ShapeDecalColorEyedropperButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.ShapeDecal);

    private void ShapeDecalColorWheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingShapeDecalColorWheel = true;
        _undo.BeginChange(); // 色相環ドラッグ1回=1 Undo ステップ
        ShapeDecalColorWheel.CaptureMouse();
        UpdateShapeDecalColorFromWheel(e.GetPosition(ShapeDecalColorWheel));
    }

    private void ShapeDecalColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingShapeDecalColorWheel) return;
        UpdateShapeDecalColorFromWheel(e.GetPosition(ShapeDecalColorWheel));
    }

    private void ShapeDecalColorWheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingShapeDecalColorWheel) return;
        _isDraggingShapeDecalColorWheel = false;
        ShapeDecalColorWheel.ReleaseMouseCapture();
        _undo.CommitChange();
    }

    private void UpdateShapeDecalColorFromWheel(Point p)
    {
        double center = (ColorWheelSize - 1) / 2.0;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy) / center;
        _shapeDecalHue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360) % 360;
        _shapeDecalSat = Math.Clamp(dist, 0, 1);
        var (r, g, b) = HsvToRgb(_shapeDecalHue, _shapeDecalSat, ShapeDecalColorValueSlider.Value / 100.0);
        SetShapeDecalColor(r, g, b);
    }

    private void ShapeDecalColorValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        var (r, g, b) = HsvToRgb(_shapeDecalHue, _shapeDecalSat, ShapeDecalColorValueSlider.Value / 100.0);
        SetShapeDecalColor(r, g, b);
    }

    private void ShapeDecalColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParseHexColor(ShapeDecalColorHexBox.Text, out var r, out var g, out var b)) return;
        SetShapeDecalColor(r, g, b);
    }

    private void ShapeDecalColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string hex) return;
        if (!TryParseHexColor(hex, out var r, out var g, out var b)) return;
        _undo.BeginChange();
        SetShapeDecalColor(r, g, b);
        _undo.CommitChange();
    }

    private void SyncShapeDecalColorUI(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        _shapeDecalSat = s;
        if (s > 0.001) _shapeDecalHue = h;

        ShapeDecalColorValueSlider.Value = v * 100;
        PositionColorWheelCursor(ShapeDecalColorWheelCursor, _shapeDecalHue, _shapeDecalSat);
        ShapeDecalColorPreviewLarge.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ShapeDecalColorHexBox.Text = ToHexColor(r, g, b);
    }

    private void SetShapeDecalColor(byte r, byte g, byte b)
    {
        if (_placingDecal is not { ShapeKind: not null } decal) return;
        decal.ShapeColor = Color.FromRgb(r, g, b);

        _suppressEvents = true;
        SyncShapeDecalColorUI(r, g, b);
        _suppressEvents = false;

        ShapeDecalColorSwatch.Background = new SolidColorBrush(decal.ShapeColor);
        RerasterizeShapeDecal(decal);
    }

    private void ShapeDecalStrokeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        if (_placingDecal is not { ShapeKind: not null } decal) return;
        decal.ShapeStrokePercent = e.NewValue;
        RerasterizeShapeDecal(decal);
    }
}
