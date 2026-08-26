using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- デカール: ステッカー的な追加画像。位置/サイズ/回転はアバター配置
//      モードと同じ移動+拡縮(アス比固定)+回転ギズモ。レイヤー順はDecalLayerStrip上の
//      並び順そのもの(右ほど手前)で、削除できない「アバター」マーカー(null
//      エントリ)より左に動かすとアバターの後ろに合成される。再編集は削除して
//      追加し直す運用(既存デカールをもう一度ドラッグ編集するモードは無い)。
//      Undo/Redoとアプリ再起動時の永続化は今回のスコープ外(セッション内のみ、
//      CompositeSnapshot/UndoManagerには一切参加しない)。 ----
public partial class ControlPanelWindow
{
    private sealed class DecalLayer
    {
        public required ImageAdjustment.PixelBuffer Pixels { get; init; }
        public required BitmapSource Thumbnail { get; init; }
        public double X, Y, Width, Height; // full-res photo-pixel space, same convention as _compositePlaceX/Y/Width/Height
        public double Rotation; // degrees, same convention (positive = clockwise) as _compositeRotation
    }

    // null = 削除不可の「アバター」マーカー。これより前(手前=右に置く前)の
    // デカールはアバターの後ろに、後の(右の)デカールは前に描画される。
    private readonly List<DecalLayer?> _decalLayerOrder = new() { null };

    private bool _isDecalPlacementModeActive;
    private DecalLayer? _placingDecal;

    private void AddDecalButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "画像 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "デカール画像を選択" };
        if (dialog.ShowDialog() == true) AddDecal(dialog.FileName);
    }

    private void AddDecal(string path)
    {
        if (_photoPixelBuffer is not { } photo) return;

        ImageAdjustment.PixelBuffer pixels;
        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            pixels = ImageAdjustment.PrepareBuffer(bitmap);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return;
        }

        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double maxDim = Math.Min(crop.Width, crop.Height) * 0.4;
        double scale = Math.Min(1.0, maxDim / Math.Max(pixels.Width, pixels.Height));
        double width = pixels.Width * scale;
        double height = pixels.Height * scale;

        var decal = new DecalLayer
        {
            Pixels = pixels,
            Thumbnail = bitmap,
            X = crop.Left + crop.Width / 2 - width / 2,
            Y = crop.Top + crop.Height / 2 - height / 2,
            Width = width,
            Height = height,
        };
        _decalLayerOrder.Add(decal);
        RebuildDecalStrip();
        EnterDecalPlacementMode(decal);
    }

    private void EnterDecalPlacementMode(DecalLayer decal)
    {
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
        _placingDecal = decal;
        _isDecalPlacementModeActive = true;
        RefreshSliderLockState();
        ScheduleCompositeRender();
        UpdateDecalHandles();
    }

    private void ExitDecalPlacementMode()
    {
        _isDecalPlacementModeActive = false;
        _placingDecal = null;
        RefreshSliderLockState();
        ScheduleCompositeRender();
        UpdateDecalHandles();
    }

    /// <summary>取得ボタンのキャンセル(PreviewModeCancelButton_Click)から呼ばれる
    /// -- クロップ/アバター配置と違い、デカールの追加そのものを取り消す
    /// (直前まで存在しなかったレイヤーなので、戻すスナップショットが無い)。</summary>
    private void CancelDecalPlacement()
    {
        if (_placingDecal is { } decal) RemoveDecal(decal);
        else ExitDecalPlacementMode();
    }

    private void RemoveDecal(DecalLayer decal)
    {
        _decalLayerOrder.Remove(decal);
        bool wasPlacing = _placingDecal == decal;
        if (wasPlacing) ExitDecalPlacementMode();
        RebuildDecalStrip();
        ScheduleCompositeRender();
    }

    // ---- レイヤーストリップ(横並びサムネイル、ドラッグで並べ替え) ----

    private bool _isDraggingStripEntry;
    private DecalLayer? _draggingStripDecal;
    private bool _draggingStripIsAvatarEntry;

    private void RebuildDecalStrip()
    {
        DecalLayerStrip.Children.Clear();
        foreach (var entry in _decalLayerOrder)
        {
            DecalLayerStrip.Children.Add(entry is null ? BuildAvatarStripEntry() : BuildDecalStripEntry(entry));
        }
    }

    private const double StripEntrySize = 56;

    private Border BuildAvatarStripEntry()
    {
        var border = new Border
        {
            Width = StripEntrySize,
            Height = StripEntrySize,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("PrimaryTintBrush"),
            BorderBrush = (Brush)FindResource("PrimaryBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeWE,
            Child = new TextBlock
            {
                Text = "アバター",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("PrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        WireStripEntryDrag(border, null);
        return border;
    }

    private Border BuildDecalStripEntry(DecalLayer decal)
    {
        var grid = new Grid();
        grid.Children.Add(new Image { Source = decal.Thumbnail, Stretch = Stretch.UniformToFill });

        var deleteButton = new Button
        {
            Content = "✕",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Background = (Brush)FindResource("PrimaryBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        deleteButton.Click += (_, _) => RemoveDecal(decal);
        grid.Children.Add(deleteButton);

        var container = new Border
        {
            Width = StripEntrySize,
            Height = StripEntrySize,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = Cursors.SizeWE,
            BorderBrush = (Brush)FindResource("HairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        WireStripEntryDrag(container, decal);
        return container;
    }

    /// <summary>ドラッグで並べ替え。実体(Border)はドラッグ中も破棄しない --
    /// RebuildDecalStrip(全消去+作り直し)を呼ぶとCaptureMouse中の要素ごと
    /// ツリーから消えてドラッグが途切れるので、代わりにDecalLayerStrip.
    /// Childrenを直接RemoveAt+Insertして既存のBorderインスタンスをそのまま
    /// 動かす(_decalLayerOrder側も同じインデックスで同期して動かす)。</summary>
    private void WireStripEntryDrag(Border entry, DecalLayer? decal)
    {
        entry.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // OriginalSourceは削除ボタンの中身(TextBlock等)になっていることが
            // 多く、Button自身との参照比較だけでは弾けない -- 祖先を辿って
            // Buttonが見つかれば削除ボタンのClickにそのまま譲る。
            if (e.OriginalSource is DependencyObject source && FindAncestorButton(source) is not null) return;
            _isDraggingStripEntry = true;
            _draggingStripDecal = decal;
            _draggingStripIsAvatarEntry = decal is null;
            entry.CaptureMouse();
            e.Handled = true;
        };
        entry.MouseMove += (_, e) =>
        {
            if (!_isDraggingStripEntry || e.LeftButton != MouseButtonState.Pressed) return;
            double x = e.GetPosition(DecalLayerStrip).X;
            int targetIndex = HitTestStripIndex(x);
            var key = _draggingStripIsAvatarEntry ? null : _draggingStripDecal;
            int currentIndex = _decalLayerOrder.IndexOf(key);
            if (targetIndex < 0 || targetIndex == currentIndex || currentIndex < 0) return;

            _decalLayerOrder.RemoveAt(currentIndex);
            _decalLayerOrder.Insert(targetIndex, key);
            var child = DecalLayerStrip.Children[currentIndex];
            DecalLayerStrip.Children.RemoveAt(currentIndex);
            DecalLayerStrip.Children.Insert(targetIndex, child);
            ScheduleCompositeRender();
        };
        entry.MouseLeftButtonUp += (_, _) =>
        {
            _isDraggingStripEntry = false;
            entry.ReleaseMouseCapture();
        };
    }

    private static Button? FindAncestorButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button) return button;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private int HitTestStripIndex(double x)
    {
        double cumulative = 0;
        for (int i = 0; i < DecalLayerStrip.Children.Count; i++)
        {
            var child = (FrameworkElement)DecalLayerStrip.Children[i];
            double childWidth = child.Width + child.Margin.Right;
            if (x < cumulative + childWidth / 2) return i;
            cumulative += childWidth;
        }
        return DecalLayerStrip.Children.Count - 1;
    }

    // ---- プレビュー上の配置ハンドル(移動+拡縮、回転なし) ----

    private void UpdateDecalHandles()
    {
        if (!_isDecalPlacementModeActive || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo
            || double.IsNaN(PreviewBorder.Width) || PreviewBorder.Width <= 0)
        {
            DecalPlacementHighlight.Visibility = Visibility.Collapsed;
            DecalHandlesLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;
        double width = decal.Width * scale;
        double height = decal.Height * scale;
        double marginX = (decal.X - crop.Left) * scale;
        double marginY = (decal.Y - crop.Top) * scale;

        DecalPlacementHighlight.Width = width;
        DecalPlacementHighlight.Height = height;
        DecalPlacementHighlight.Margin = new Thickness(marginX, marginY, 0, 0);
        DecalPlacementHighlightRotate.CenterX = width / 2;
        DecalPlacementHighlightRotate.CenterY = height / 2;
        DecalPlacementHighlightRotate.Angle = decal.Rotation;
        DecalPlacementHighlight.Visibility = Visibility.Visible;

        DecalHandlesLayer.Margin = new Thickness(marginX, marginY, 0, 0);
        DecalHandlesLayer.Width = width;
        DecalHandlesLayer.Height = height;
        DecalHandlesRotateTransform.Angle = decal.Rotation;

        double half = AvatarHandleSize / 2;
        PlaceAvatarHandle(DecalHandleTL, -half, -half);
        PlaceAvatarHandle(DecalHandleTR, width - half, -half);
        PlaceAvatarHandle(DecalHandleBL, -half, height - half);
        PlaceAvatarHandle(DecalHandleBR, width - half, height - half);

        double gizmoHalf = AvatarRotateGizmoSize / 2;
        double gizmoCenterY = -AvatarRotateGizmoOffset;
        DecalRotateGizmoLine.X1 = width / 2;
        DecalRotateGizmoLine.Y1 = 0;
        DecalRotateGizmoLine.X2 = width / 2;
        DecalRotateGizmoLine.Y2 = gizmoCenterY + gizmoHalf;
        PlaceAvatarHandle(DecalRotateGizmoHandle, width / 2 - gizmoHalf, gizmoCenterY - gizmoHalf);

        DecalHandlesLayer.Visibility = Visibility.Visible;
    }

    private bool _isDraggingDecalHandle;
    private CropHandleCorner _decalDragHandle;
    private Point _decalHandleDragStartMouse;
    private double _decalHandleStartX, _decalHandleStartY, _decalHandleStartWidth, _decalHandleStartHeight;

    private void DecalHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_placingDecal is not { } decal) return;
        var element = (FrameworkElement)sender;
        _decalDragHandle = Enum.Parse<CropHandleCorner>((string)element.Tag);
        _isDraggingDecalHandle = true;
        _decalHandleDragStartMouse = e.GetPosition(PreviewBorder);
        _decalHandleStartX = decal.X;
        _decalHandleStartY = decal.Y;
        _decalHandleStartWidth = decal.Width;
        _decalHandleStartHeight = decal.Height;
        element.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>アバター用(AvatarHandle_MouseMove)と全く同じ、画面座標系の
    /// ドラッグ量をデカール自身のローカル軸へ逆回転してから角の対角方向へ
    /// 射影して1つの連続したスケール値を得るロック済みアス比リサイズ。</summary>
    private void DecalHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingDecalHandle || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width * _previewZoom;
        if (scale <= 0) return;

        var current = e.GetPosition(PreviewBorder);
        double screenDx = (current.X - _decalHandleDragStartMouse.X) / scale;
        double screenDy = (current.Y - _decalHandleDragStartMouse.Y) / scale;

        double rad = -decal.Rotation * Math.PI / 180.0;
        double rotCos = Math.Cos(rad), rotSin = Math.Sin(rad);
        double dx = screenDx * rotCos - screenDy * rotSin;
        double dy = screenDx * rotSin + screenDy * rotCos;

        bool left = _decalDragHandle is CropHandleCorner.TopLeft or CropHandleCorner.BottomLeft;
        bool top = _decalDragHandle is CropHandleCorner.TopLeft or CropHandleCorner.TopRight;

        double halfW0 = _decalHandleStartWidth / 2, halfH0 = _decalHandleStartHeight / 2;
        double cornerDist0 = Math.Sqrt(halfW0 * halfW0 + halfH0 * halfH0);
        if (cornerDist0 <= 0) return;
        double dirX = (left ? -halfW0 : halfW0) / cornerDist0;
        double dirY = (top ? -halfH0 : halfH0) / cornerDist0;
        double projected = dx * dirX + dy * dirY;
        double dragScale = (cornerDist0 + projected) / cornerDist0;
        if (dragScale <= 0) return;

        double aspect = decal.Pixels.Width / (double)decal.Pixels.Height;
        double newWidth = _decalHandleStartWidth * dragScale;
        double newHeight = newWidth / aspect;
        if (newWidth < 12 || newHeight < 12) return;

        double centerX = _decalHandleStartX + _decalHandleStartWidth / 2;
        double centerY = _decalHandleStartY + _decalHandleStartHeight / 2;
        decal.Width = newWidth;
        decal.Height = newHeight;
        decal.X = centerX - newWidth / 2;
        decal.Y = centerY - newHeight / 2;

        UpdateDecalHandles();
        ScheduleCompositeRender();
    }

    private void DecalHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingDecalHandle = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool _isDraggingDecalRotateGizmo;
    private double _decalRotateGizmoStartAngle;
    private double _decalRotateGizmoStartRotation;

    private void DecalRotateGizmo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return;
        _isDraggingDecalRotateGizmo = true;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;
        double centerX = (decal.X + decal.Width / 2 - crop.Left) * scale;
        double centerY = (decal.Y + decal.Height / 2 - crop.Top) * scale;
        var mouse = e.GetPosition(PreviewBorder);
        _decalRotateGizmoStartAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        _decalRotateGizmoStartRotation = decal.Rotation;
        DecalRotateGizmoHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DecalRotateGizmo_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingDecalRotateGizmo || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width;
        double centerX = (decal.X + decal.Width / 2 - crop.Left) * scale;
        double centerY = (decal.Y + decal.Height / 2 - crop.Top) * scale;
        var mouse = e.GetPosition(PreviewBorder);
        double currentAngle = Math.Atan2(mouse.Y - centerY, mouse.X - centerX) * 180.0 / Math.PI;
        double newRotation = _decalRotateGizmoStartRotation + (currentAngle - _decalRotateGizmoStartAngle);
        decal.Rotation = SoftSnap(newRotation, 5, -180, -90, 0, 90, 180);

        UpdateDecalHandles();
        ScheduleCompositeRender();
    }

    private void DecalRotateGizmo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingDecalRotateGizmo = false;
        DecalRotateGizmoHandle.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---- プレビュー本体ドラッグ(移動) -- PreviewImage_MouseLeftButtonDown/
    //      MouseMove/MouseLeftButtonUpから呼ばれる(アバター用のShift/
    //      アバター配置モード分岐と並ぶ3つ目の分岐)。 ----

    private bool _isDraggingDecalPlacement;
    private Point _decalBodyDragStartMouse;
    private double _decalBodyDragStartX, _decalBodyDragStartY;

    private bool TryStartDecalBodyDrag(MouseButtonEventArgs e)
    {
        if (!_isDecalPlacementModeActive || _placingDecal is not { } decal || _photoPixelBuffer is null) return false;
        _isDraggingDecalPlacement = true;
        _decalBodyDragStartMouse = e.GetPosition(PreviewBorder);
        _decalBodyDragStartX = decal.X;
        _decalBodyDragStartY = decal.Y;
        _isCompositeDragging = true;
        PreviewImage.CaptureMouse();
        return true;
    }

    private bool TryContinueDecalBodyDrag(MouseEventArgs e)
    {
        if (!_isDraggingDecalPlacement || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return false;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width * _previewZoom;
        var current = e.GetPosition(PreviewBorder);
        decal.X = _decalBodyDragStartX + (current.X - _decalBodyDragStartMouse.X) / scale;
        decal.Y = _decalBodyDragStartY + (current.Y - _decalBodyDragStartMouse.Y) / scale;
        UpdateDecalHandles();
        ScheduleCompositeRender();
        return true;
    }

    private bool TryEndDecalBodyDrag()
    {
        if (!_isDraggingDecalPlacement) return false;
        _isDraggingDecalPlacement = false;
        _isCompositeDragging = false;
        PreviewImage.ReleaseMouseCapture();
        ScheduleCompositeRender();
        return true;
    }

    // ---- レンダリング統合 -- 見つからない場合(デカール0件)はコピーすら
    //      発生させない、GPUチェーンには一切手を入れず単純なCPU側アルファ
    //      合成をCompositeOverlayOntoPhotoの前後に挟むだけ。
    //
    //      CaptureBehind/InFrontOfAvatarDecalsはUIスレッドで呼び、結果の
    //      値スナップショット(DecalRenderEntry、_decalLayerOrder自体や
    //      DecalLayerの生きた参照ではなくPixels+その時点のX/Y/Width/Height
    //      だけをコピーしたもの)をTask.Run側へ渡す -- 他の合成入力
    //      (CaptureCompositeSnapshotのfullSnap等)がバックグラウンドへ渡る
    //      前にUIスレッドで値化されているのと同じ理由: デカール配置ドラッグは
    //      UIスレッドでdecal.X/Y/Width/Heightを直接書き換えるので、生きた
    //      DecalLayer参照のままバックグラウンドへ渡すとレンダー中の値と
    //      競合し得る。 ----

    private readonly record struct DecalRenderEntry(ImageAdjustment.PixelBuffer Pixels, double X, double Y, double Width, double Height, double Rotation);

    private List<DecalRenderEntry> CaptureBehindAvatarDecals() =>
        _decalLayerOrder.TakeWhile(l => l is not null)
            .Select(l => new DecalRenderEntry(l!.Pixels, l.X, l.Y, l.Width, l.Height, l.Rotation))
            .ToList();

    private List<DecalRenderEntry> CaptureInFrontOfAvatarDecals()
    {
        int avatarIndex = _decalLayerOrder.IndexOf(null);
        var after = avatarIndex < 0 ? _decalLayerOrder : _decalLayerOrder.Skip(avatarIndex + 1);
        return after.Where(l => l is not null)
            .Select(l => new DecalRenderEntry(l!.Pixels, l.X, l.Y, l.Width, l.Height, l.Rotation))
            .ToList();
    }

    /// <summary>デカールが1枚も無ければphotoをそのまま返す(クローンすら
    /// しない) -- この機能を使っていない大半のユーザーには一切コストが
    /// 乗らない。背景ぼかし(photoBlurAmount)が有効な場合、通常はGPU側の
    /// CompositeOverlayOntoPhoto内でこのphotoごとぼかされるが、それだと
    /// アバターの後ろに置いたはずのデカールまで背景と一緒にぼけてしまう
    /// (デカールは「背景よりは手前」のつもりで置いている)。そこで背景ぼかしを
    /// ここで先に適用してからデカールを重ね、呼び出し側はCompositeOverlayOntoPhoto
    /// にphotoBlurAmount: 0を渡して二重ぼかしを避ける -- 判断はEffectivePhotoBlurAmountで。</summary>
    private static ImageAdjustment.PixelBuffer ApplyBehindAvatarDecals(ImageAdjustment.PixelBuffer photo, List<DecalRenderEntry> behind, double scale, double photoBlurAmount, double photoBlurScale)
    {
        if (behind.Count == 0) return photo;
        var cloned = photo with { Pixels = (byte[])photo.Pixels.Clone() };
        if (photoBlurAmount > 0) ImageAdjustment.ApplyPhotoBlurInPlace(cloned, photoBlurAmount, photoBlurScale);
        foreach (var decal in behind)
            BlendDecalOnto(cloned, decal, decal.X * scale, decal.Y * scale, decal.Width * scale, decal.Height * scale);
        return cloned;
    }

    /// <summary>背後デカールが無ければ通常通りGPU側でぼかしていい
    /// (snap.PhotoBlurAmountをそのまま渡す)。ある場合はApplyBehindAvatarDecals
    /// 側で既にCPU側でぼかし済みなので、GPU側には0を渡して二重ぼかしを防ぐ。</summary>
    private static double EffectivePhotoBlurAmount(double photoBlurAmount, List<DecalRenderEntry> behind) =>
        behind.Count > 0 ? 0 : photoBlurAmount;

    private static WriteableBitmap ApplyInFrontOfAvatarDecals(WriteableBitmap composite, List<DecalRenderEntry> front, double scale)
    {
        if (front.Count == 0) return composite;

        int width = composite.PixelWidth, height = composite.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        composite.CopyPixels(pixels, stride, 0);
        var buffer = new ImageAdjustment.PixelBuffer(pixels, width, height, stride);
        foreach (var decal in front)
            BlendDecalOnto(buffer, decal, decal.X * scale, decal.Y * scale, decal.Width * scale, decal.Height * scale);

        var result = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        result.Freeze();
        return result;
    }

    /// <summary>最近傍サンプリングでdecal.Pixelsを(x,y,width,height)(destと
    /// 同じスケールの座標系、回転はdecal.Rotation度・中心基準)へ普通の
    /// アルファ合成(over)で描き込む。破棄前提の一時バッファに書くだけなので
    /// 専用のアンチエイリアス/バイリニアは見送り -- デカールは静止画像
    /// なので、動く実写系のエフェクトほど補間品質がシビアにならない。
    /// 各destピクセルを中心基準で逆回転してデカール自身のローカル(未回転)
    /// 矩形に写像するアプローチ -- AvatarHandle_MouseMove/DecalHandle_MouseMove
    /// のドラッグ量の逆回転と同じ考え方をピクセル単位でやっている。</summary>
    private static void BlendDecalOnto(ImageAdjustment.PixelBuffer dest, DecalRenderEntry decal, double x, double y, double width, double height)
    {
        var src = decal.Pixels;
        double centerX = x + width / 2;
        double centerY = y + height / 2;
        double halfW = width / 2, halfH = height / 2;
        double rad = decal.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        // Rotated bounding box to iterate over -- same padding formula
        // ImageAdjustment.RenderOverlayForComposite uses for the avatar's
        // own rotated bounds.
        double boundHalfW = Math.Abs(halfW * cos) + Math.Abs(halfH * sin);
        double boundHalfH = Math.Abs(halfW * sin) + Math.Abs(halfH * cos);

        int xStart = Math.Max(0, (int)Math.Floor(centerX - boundHalfW));
        int yStart = Math.Max(0, (int)Math.Floor(centerY - boundHalfH));
        int xEnd = Math.Min(dest.Width, (int)Math.Ceiling(centerX + boundHalfW));
        int yEnd = Math.Min(dest.Height, (int)Math.Ceiling(centerY + boundHalfH));
        if (xEnd <= xStart || yEnd <= yStart || halfW <= 0 || halfH <= 0) return;

        for (int dy = yStart; dy < yEnd; dy++)
        {
            double relY = dy + 0.5 - centerY;
            int destRow = dy * dest.Stride;
            for (int dx = xStart; dx < xEnd; dx++)
            {
                double relX = dx + 0.5 - centerX;
                // Inverse-rotate (screen space -> decal's own local space).
                double localX = relX * cos + relY * sin;
                double localY = -relX * sin + relY * cos;
                if (localX < -halfW || localX >= halfW || localY < -halfH || localY >= halfH) continue;

                int srcX = Math.Clamp((int)((localX + halfW) / width * src.Width), 0, src.Width - 1);
                int srcY = Math.Clamp((int)((localY + halfH) / height * src.Height), 0, src.Height - 1);
                int srcIdx = srcY * src.Stride + srcX * 4;
                byte sa = src.Pixels[srcIdx + 3];
                if (sa == 0) continue;
                int dstIdx = destRow + dx * 4;
                double a = sa / 255.0;
                double inv = 1 - a;
                dest.Pixels[dstIdx] = (byte)(src.Pixels[srcIdx] * a + dest.Pixels[dstIdx] * inv);
                dest.Pixels[dstIdx + 1] = (byte)(src.Pixels[srcIdx + 1] * a + dest.Pixels[dstIdx + 1] * inv);
                dest.Pixels[dstIdx + 2] = (byte)(src.Pixels[srcIdx + 2] * a + dest.Pixels[dstIdx + 2] * inv);
                dest.Pixels[dstIdx + 3] = (byte)(sa + dest.Pixels[dstIdx + 3] * inv);
            }
        }
    }
}
