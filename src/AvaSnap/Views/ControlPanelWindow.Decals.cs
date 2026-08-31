using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using AvaSnap.Services;

namespace AvaSnap.Views;

/// <summary>順序リスト <c>_decalLayerOrder</c> の1エントリ分の不変スナップショット。
/// <see cref="CompositeSnapshot"/> に畳み込まれて Undo/Redo の1タイムラインに乗る。
///
/// 画像デカールは焼き直さない(Pixels は生涯同じ参照)ので、全スナップショットが
/// その1つの <c>PixelBuffer</c> を共有するだけでメモリ増はない。枠線デカールは
/// サイズ/色/太さ変更のたびに焼き直す(そのつど新 byte[])ため、バッファを持つと
/// Undo ステップごとに巨大バッファが積み上がる。よって枠線は Pixels/Thumbnail を
/// 持たず、パラメータ(サイズ/色/太さ)だけ保持して <see cref="ApplyDecalSnapshot"/>
/// で焼き直す(枠の矩形描画は安い)。等価判定もパラメータで決まる。</summary>
public sealed record DecalEntrySnapshot(
    bool IsAvatarMarker,
    ImageAdjustment.PixelBuffer? Pixels,
    System.Windows.Media.Imaging.BitmapSource? Thumbnail,
    double X, double Y, double Width, double Height, double Rotation,
    bool IsFrame, byte ColorR, byte ColorG, byte ColorB, double StrokePercent,
    double Opacity);

// ---- デカール: ステッカー的な追加画像 + 写真の縁取り用の枠線。位置/サイズ/回転は
//      アバター配置モードと同じ移動+リサイズハンドル+回転ギズモ。レイヤー順は
//      DecalLayerStrip 上の並び順そのもの(右ほど手前)で、削除できない「アバター」
//      マーカー(null エントリ)より左に動かすとアバターの後ろに合成される。既存
//      デカールをもう一度ドラッグ編集するモードは無い(再編集は削除して追加し直す)。
//      Undo/Redo には CompositeSnapshot.Decals 経由で参加する(追加/削除/移動/
//      リサイズ/回転/色/太さ/並べ替えを1操作=1ステップでラップ)。アプリ再起動時の
//      永続化は今回のスコープ外(セッション内のみ)。 ----
public partial class ControlPanelWindow
{
    private sealed class DecalLayer
    {
        // 枠線デカール(IsFrame)はサイズ/色/太さ変更のたびに ShapeRasterizer で
        // 焼き直して差し替えるので init ではなく set。
        public required ImageAdjustment.PixelBuffer Pixels { get; set; }
        public required BitmapSource Thumbnail { get; set; }
        public double X, Y, Width, Height; // フル解像度の写真ピクセル空間、_compositePlaceX/Y/Width/Height と同じ規約
        public double Rotation; // 度、_compositeRotation と同じ規約(正 = 時計回り)
        public double Opacity = 1.0; // 0..1、このデカール個別の不透明度
        public string? SourcePath; // 画像デカールの元ファイル(プロジェクト保存用)。枠線は null

        // ---- 枠線デカール専用。IsFrame == false = 従来どおりの画像デカール(以降の
        //      分岐はすべて「IsFrame でなければ画像として扱う」で画像経路は不変)。 ----
        public bool IsFrame;
        public System.Windows.Media.Color ShapeColor = System.Windows.Media.Colors.White;
        public double ShapeStrokePercent = 3; // 枠線の短辺に対する線幅%

        // 枠線を「合成の出力解像度」で焼いたキャッシュ(BlendDecalOnto の最近傍拡大に
        // よる細い枠のボケ対策)。出力寸法 or 色/太さが変わればミスして焼き直す。
        // ドラッグ中は使わず Pixels を引き伸ばす(RenderTargetBitmap コスト回避)。
        public ImageAdjustment.PixelBuffer? FrameRenderCache;
        public int FrameRenderCacheW, FrameRenderCacheH;
        public System.Windows.Media.Color FrameRenderCacheColor;
        public double FrameRenderCacheStroke;
    }

    // null = 削除不可の「アバター」マーカー。これより前(手前=右に置く前)の
    // デカールはアバターの後ろに、後の(右の)デカールは前に描画される。
    private readonly List<DecalLayer?> _decalLayerOrder = new() { null };

    private bool _isDecalPlacementModeActive;
    private DecalLayer? _placingDecal;

    // ---- 既存デカールの再編集: ストリップのエントリを(並べ替えずに)クリックすると
    //      そのデカールの配置モードへ再入場する。新規追加からの配置と違い、キャンセルは
    //      「削除」ではなく「編集開始時のレイヤー順・全デカール状態へ復元」。
    //      スナップショットは Undo と同じ CaptureDecalSnapshot/ApplyDecalSnapshot を再利用。 ----
    private bool _editingExistingDecal;
    private EquatableArray<DecalEntrySnapshot>? _decalEditEntrySnapshot;

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
            SourcePath = path,
            X = crop.Left + crop.Width / 2 - width / 2,
            Y = crop.Top + crop.Height / 2 - height / 2,
            Width = width,
            Height = height,
        };
        _undo.BeginChange(); // 追加を1 Undo ステップに
        _decalLayerOrder.Add(decal);
        RebuildDecalStrip();
        EnterDecalPlacementMode(decal);
        _undo.CommitChange();
    }

    private void EnterDecalPlacementMode(DecalLayer decal, bool editingExisting = false)
    {
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
        _placingDecal = decal;
        _isDecalPlacementModeActive = true;
        _editingExistingDecal = editingExisting;
        _decalEditEntrySnapshot = editingExisting ? CaptureDecalSnapshot() : null;
        RefreshSliderLockState();
        ScheduleCompositeRender();
        RebuildDecalStrip(); // 編集中エントリのハイライト更新
        UpdateDecalHandles();
        UpdateShapeDecalPanel();
    }

    private void ExitDecalPlacementMode()
    {
        _isDecalPlacementModeActive = false;
        _placingDecal = null;
        _editingExistingDecal = false;
        _decalEditEntrySnapshot = null;
        RefreshSliderLockState();
        ScheduleCompositeRender();
        RebuildDecalStrip();
        UpdateDecalHandles();
        UpdateShapeDecalPanel();
    }

    /// <summary>確定バーのキャンセル(PreviewModeCancelButton_Click)から呼ばれる。
    /// 新規追加からの配置なら追加そのものを取り消す(戻すスナップショットが無い)。
    /// ストリップから選び直した既存デカールなら、編集開始時の状態へ戻して抜ける
    /// (クロップ/アバター配置モードのキャンセルと同じ、1 Undo ステップ)。</summary>
    private void CancelDecalPlacement()
    {
        if (_placingDecal is not { } decal)
        {
            ExitDecalPlacementMode();
            return;
        }
        if (_editingExistingDecal && _decalEditEntrySnapshot is { } snap)
        {
            // 編集開始時のレイヤー順・全デカール状態(並べ替えも含む)へ戻す。
            _undo.BeginChange();
            ApplyDecalSnapshot(snap);
            ExitDecalPlacementMode(); // フラグ解除 + ストリップ再構築 + 再描画
            _undo.CommitChange();
            return;
        }
        RemoveDecal(decal);
    }

    /// <summary>ストリップのエントリを(並べ替えずに)クリックしたときに呼ばれる
    /// -- そのデカールの配置モードへ再入場して編集できるようにする。別のデカールを
    /// 編集/配置中なら、それは確定して(各ジェスチャは既にコミット済み)切り替える。</summary>
    private void SelectDecalForEdit(DecalLayer decal)
    {
        if (!_decalLayerOrder.Contains(decal) || _placingDecal == decal) return;
        if (_isDecalPlacementModeActive) ExitDecalPlacementMode();
        EnterDecalPlacementMode(decal, editingExisting: true);
    }

    private void RemoveDecal(DecalLayer decal)
    {
        if (!_decalLayerOrder.Contains(decal)) return;
        _undo.BeginChange(); // 削除(✕ / キャンセル / 未確定入れ替え)を1 Undo ステップに
        _decalLayerOrder.Remove(decal);
        bool wasPlacing = _placingDecal == decal;
        if (wasPlacing) ExitDecalPlacementMode();
        RebuildDecalStrip();
        ScheduleCompositeRender();
        _undo.CommitChange();
    }

    // ---- Undo/Redo 連携: _decalLayerOrder を CompositeSnapshot に畳み込む
    //      (CaptureCompositeSnapshot / ApplyCompositeSnapshot から呼ばれる)。 ----

    private EquatableArray<DecalEntrySnapshot> CaptureDecalSnapshot() =>
        new(_decalLayerOrder.Select(l =>
        {
            if (l is null)
                return new DecalEntrySnapshot(true, null, null, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 1);
            // 枠線はパラメータのみ(焼き直しは Apply 側)、画像はバッファ参照を共有。
            bool isShape = l.IsFrame;
            return new DecalEntrySnapshot(false,
                isShape ? null : l.Pixels,
                isShape ? null : l.Thumbnail,
                l.X, l.Y, l.Width, l.Height, l.Rotation,
                l.IsFrame, l.ShapeColor.R, l.ShapeColor.G, l.ShapeColor.B, l.ShapeStrokePercent,
                l.Opacity);
        }).ToArray());

    /// <summary>スナップショット1エントリから DecalLayer を再構築する。枠線は
    /// パラメータから焼き直し、画像はバッファ参照を共有(CaptureDecalSnapshot と対)。</summary>
    private DecalLayer BuildDecalFromSnapshot(DecalEntrySnapshot e)
    {
        var color = Color.FromRgb(e.ColorR, e.ColorG, e.ColorB);
        ImageAdjustment.PixelBuffer pixels;
        BitmapSource thumb;
        if (e.IsFrame)
        {
            var (rw, rh) = ShapeRasterizer.RasterSizeFor(e.Width, e.Height);
            pixels = ShapeRasterizer.RasterizeFrame(rw, rh, color, e.StrokePercent);
            thumb = MakeShapeThumbnail(pixels);
        }
        else
        {
            pixels = e.Pixels!;
            thumb = e.Thumbnail!;
        }
        return new DecalLayer
        {
            Pixels = pixels,
            Thumbnail = thumb,
            X = e.X, Y = e.Y, Width = e.Width, Height = e.Height, Rotation = e.Rotation,
            Opacity = e.Opacity,
            IsFrame = e.IsFrame,
            ShapeColor = color,
            ShapeStrokePercent = e.StrokePercent,
        };
    }

    private void ApplyDecalSnapshot(EquatableArray<DecalEntrySnapshot> snap)
    {
        var entries = snap.AsArray();
        if (entries.Length == 0) return; // 空 = 未初期化スナップショット。触らない

        int prevPlacingIndex = _placingDecal is { } pp ? _decalLayerOrder.IndexOf(pp) : -1;
        _decalLayerOrder.Clear();
        foreach (var e in entries)
        {
            if (e.IsAvatarMarker
                || (!e.IsFrame && (e.Pixels is null || e.Thumbnail is null))) // 壊れたエントリは通常あり得ない
            {
                _decalLayerOrder.Add(null);
                continue;
            }
            _decalLayerOrder.Add(BuildDecalFromSnapshot(e));
        }
        if (!_decalLayerOrder.Contains(null)) _decalLayerOrder.Insert(0, null); // アバターマーカーは必ず1つ

        // 配置中だった場合: Undo/Redo が同じ位置のデカールのプロパティを戻した
        // だけなら、その位置の新インスタンスへ張り替えて配置モードを維持する。
        // デカール自体が追加/削除された(= その位置に無い)なら配置モードを抜ける。
        if (_isDecalPlacementModeActive)
        {
            if (prevPlacingIndex >= 0 && prevPlacingIndex < _decalLayerOrder.Count
                && _decalLayerOrder[prevPlacingIndex] is { } stillThere)
            {
                _placingDecal = stillThere;
            }
            else
            {
                _isDecalPlacementModeActive = false;
                _placingDecal = null;
            }
        }
        RebuildDecalStrip();
        RefreshSliderLockState();
        UpdateDecalHandles();
        UpdateShapeDecalPanel();
    }

    // ---- レイヤーストリップ(横並びサムネイル、ドラッグで並べ替え) ----

    private bool _isDraggingStripEntry;
    private DecalLayer? _draggingStripDecal;
    private bool _draggingStripIsAvatarEntry;
    private bool _stripPressPending; // マウス押下〜しきい値超え前(クリックか並べ替えか未確定)
    private Point _stripPressPos;
    private DecalLayer? _stripPressDecal;
    private const double StripDragThreshold = 4;

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
        // 図形デカール(特に枠/線)はサムネの大半が透明なので、無地の下地を
        // 敷いて白い図形でも見えるようにする。
        if (decal.IsFrame)
            grid.Background = new SolidColorBrush(Color.FromRgb(60, 60, 68));
        grid.Children.Add(new Image
        {
            Source = decal.Thumbnail,
            // 図形は端まで意味がある(枠/線)ので letterbox、画像は従来どおり
            // 正方セルを埋める。
            Stretch = decal.IsFrame ? Stretch.Uniform : Stretch.UniformToFill,
        });

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

        bool editing = decal == _placingDecal;
        var container = new Border
        {
            Width = StripEntrySize,
            Height = StripEntrySize,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = Cursors.Hand, // クリック=編集 / ドラッグ=並べ替え
            ToolTip = "クリックで編集、ドラッグで並べ替え",
            BorderBrush = editing ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("HairlineBrush"),
            BorderThickness = new Thickness(editing ? 2 : 1),
            Child = grid,
        };
        // ホバーで薄く枠を出して「触れる」ことを示す(編集中エントリはそのまま)。
        container.MouseEnter += (_, _) =>
        {
            if (decal != _placingDecal) container.BorderBrush = (Brush)FindResource("TextSecondaryBrush");
        };
        container.MouseLeave += (_, _) =>
        {
            if (decal != _placingDecal) container.BorderBrush = (Brush)FindResource("HairlineBrush");
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
            _stripPressPending = true; // まだクリックか並べ替えか未確定
            _stripPressPos = e.GetPosition(DecalLayerStrip);
            _stripPressDecal = decal;
            _draggingStripDecal = decal;
            _draggingStripIsAvatarEntry = decal is null;
            entry.CaptureMouse();
            e.Handled = true;
        };
        entry.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(DecalLayerStrip);

            // しきい値を超えて初めて「並べ替えドラッグ」に昇格。超えなければクリック。
            if (_stripPressPending && (pos - _stripPressPos).Length > StripDragThreshold)
            {
                _stripPressPending = false;
                _isDraggingStripEntry = true;
                _undo.BeginChange(); // 並べ替えを1 Undo ステップに(動かさなければ CommitChange 側が no-op)
            }
            if (!_isDraggingStripEntry) return;

            int targetIndex = HitTestStripIndex(pos.X);
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
        entry.MouseLeftButtonUp += (_, e) =>
        {
            entry.ReleaseMouseCapture();
            e.Handled = true;
            if (_isDraggingStripEntry)
            {
                _isDraggingStripEntry = false;
                _undo.CommitChange();
            }
            else if (_stripPressPending && _stripPressDecal is { } d)
            {
                SelectDecalForEdit(d); // ドラッグせずクリック = 編集
            }
            _stripPressPending = false;
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

        // 辺ハンドルは枠線デカールのみ(角=アス比固定 / 辺=その軸だけ変更)。
        bool showEdges = decal.IsFrame;
        var edgeVis = showEdges ? Visibility.Visible : Visibility.Collapsed;
        DecalHandleT.Visibility = DecalHandleB.Visibility = DecalHandleL.Visibility = DecalHandleR.Visibility = edgeVis;
        if (showEdges)
        {
            PlaceAvatarHandle(DecalHandleT, width / 2 - half, -half);
            PlaceAvatarHandle(DecalHandleB, width / 2 - half, height - half);
            PlaceAvatarHandle(DecalHandleL, -half, height / 2 - half);
            PlaceAvatarHandle(DecalHandleR, width - half, height / 2 - half);
        }

        double gizmoHalf = AvatarRotateGizmoSize / 2;
        double gizmoCenterY = -AvatarRotateGizmoOffset;
        DecalRotateGizmoLine.X1 = width / 2;
        DecalRotateGizmoLine.Y1 = 0;
        DecalRotateGizmoLine.X2 = width / 2;
        DecalRotateGizmoLine.Y2 = gizmoCenterY + gizmoHalf;
        PlaceAvatarHandle(DecalRotateGizmoHandle, width / 2 - gizmoHalf, gizmoCenterY - gizmoHalf);

        DecalHandlesLayer.Visibility = Visibility.Visible;
    }

    // 枠線デカールは 角=アス比固定の拡縮 / 辺=その軸だけ変更。画像デカールは
    // 角のみ(TL..BR)で従来どおり。XAML の Tag 文字列をこれに Parse する。
    private enum DecalHandleKind { TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right }

    private bool _isDraggingDecalHandle;
    private DecalHandleKind _decalDragHandle;
    private Point _decalHandleDragStartMouse;
    private double _decalHandleStartX, _decalHandleStartY, _decalHandleStartWidth, _decalHandleStartHeight;

    private void DecalHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_placingDecal is not { } decal) return;
        var element = (FrameworkElement)sender;
        _decalDragHandle = Enum.Parse<DecalHandleKind>((string)element.Tag);
        _isDraggingDecalHandle = true;
        _decalHandleDragStartMouse = e.GetPosition(PreviewBorder);
        _decalHandleStartX = decal.X;
        _decalHandleStartY = decal.Y;
        _decalHandleStartWidth = decal.Width;
        _decalHandleStartHeight = decal.Height;
        _isCompositeDragging = true; // ドラッグ中は縮小解像度で合成(大きい四角の重さ対策)
        _undo.BeginChange(); // リサイズ1ジェスチャ=1 Undo ステップ
        element.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>枠線デカール: 中心を固定したまま拡縮する。角ハンドル=開始時の
    /// アス比を保ったまま拡縮、辺ハンドル=その軸だけ(左右=幅、上下=高さ)。
    /// 画像デカール: 従来どおり角のみ・中心基準のアス比固定リサイズ。いずれも
    /// 回転に対応(デカール自身のローカル軸 ux/uy へ射影して計算)し、結果は
    /// キャンバス(クロップ範囲)内へクランプする。</summary>
    private void DecalHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingDecalHandle || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width * _previewZoom;
        if (scale <= 0) return;

        var current = e.GetPosition(PreviewBorder);
        double screenDx = (current.X - _decalHandleDragStartMouse.X) / scale;
        double screenDy = (current.Y - _decalHandleDragStartMouse.Y) / scale;

        var kind = _decalDragHandle;
        bool left = kind is DecalHandleKind.TopLeft or DecalHandleKind.BottomLeft;
        bool top = kind is DecalHandleKind.TopLeft or DecalHandleKind.TopRight;
        bool isEdge = kind is DecalHandleKind.Top or DecalHandleKind.Bottom or DecalHandleKind.Left or DecalHandleKind.Right;

        double r = decal.Rotation * Math.PI / 180.0;
        double uxx = Math.Cos(r), uxy = Math.Sin(r);   // ローカル +X 軸(写真ピクセル空間)
        double uyx = -Math.Sin(r), uyy = Math.Cos(r);  // ローカル +Y 軸
        double cx0 = _decalHandleStartX + _decalHandleStartWidth / 2;
        double cy0 = _decalHandleStartY + _decalHandleStartHeight / 2;
        double hw0 = _decalHandleStartWidth / 2, hh0 = _decalHandleStartHeight / 2;

        // ---- 枠線デカール: 中心(cx0, cy0)固定で拡縮 ----
        if (decal.IsFrame)
        {
            // マウス移動量をデカールのローカル軸へ射影(その軸方向の移動量)。
            double dLocalX = screenDx * uxx + screenDy * uxy;
            double dLocalY = screenDx * uyx + screenDy * uyy;

            double newW = _decalHandleStartWidth;
            double newH = _decalHandleStartHeight;

            if (isEdge && kind is DecalHandleKind.Left or DecalHandleKind.Right)
            {
                double sx = kind == DecalHandleKind.Left ? -1.0 : 1.0;
                double halfW = Math.Abs(sx * hw0 + dLocalX); // 中心 → 掴んだ辺
                newW = Math.Clamp(2 * halfW, 12, crop.Width);
            }
            else if (isEdge) // 上/下
            {
                double sy = kind == DecalHandleKind.Top ? -1.0 : 1.0;
                double halfH = Math.Abs(sy * hh0 + dLocalY);
                newH = Math.Clamp(2 * halfH, 12, crop.Height);
            }
            else // 角: 開始アス比を保ったまま拡縮
            {
                double sx = left ? -1.0 : 1.0, sy = top ? -1.0 : 1.0;
                double localX = sx * hw0 + dLocalX; // 中心 → 掴んだ角(ローカル)
                double localY = sy * hh0 + dLocalY;
                double halfDiag0 = Math.Sqrt(hw0 * hw0 + hh0 * hh0);
                if (halfDiag0 <= 0) return;
                // 開始対角の単位ベクトルへ射影 = 中心からの新しい半対角長
                double proj = (localX * (sx * hw0) + localY * (sy * hh0)) / halfDiag0;
                double ratio = proj / halfDiag0;
                double minRatio = Math.Max(12.0 / _decalHandleStartWidth, 12.0 / _decalHandleStartHeight);
                double maxRatio = Math.Min(crop.Width / _decalHandleStartWidth, crop.Height / _decalHandleStartHeight);
                if (maxRatio < minRatio) return;
                ratio = Math.Clamp(ratio, minRatio, maxRatio);
                newW = _decalHandleStartWidth * ratio;
                newH = _decalHandleStartHeight * ratio;
            }

            // 中心は開始時の中心のまま(キャンバス外に出る場合だけ ApplyDecalRect が寄せる)。
            ApplyDecalRect(decal, cx0, cy0, newW, newH, crop);
            UpdateDecalHandles();
            ScheduleCompositeRender();
            return;
        }

        // ---- 画像デカール: 従来の中心基準アス比固定(角ハンドルのみ) ----
        double rad = -decal.Rotation * Math.PI / 180.0;
        double rotCos = Math.Cos(rad), rotSin = Math.Sin(rad);
        double dx = screenDx * rotCos - screenDy * rotSin;
        double dy = screenDx * rotSin + screenDy * rotCos;

        double cornerDist0 = Math.Sqrt(hw0 * hw0 + hh0 * hh0);
        if (cornerDist0 <= 0) return;
        double dirX = (left ? -hw0 : hw0) / cornerDist0;
        double dirY = (top ? -hh0 : hh0) / cornerDist0;
        double projected = dx * dirX + dy * dirY;
        double dragScale = (cornerDist0 + projected) / cornerDist0;
        if (dragScale <= 0) return;

        double aspect = decal.Pixels.Width / (double)decal.Pixels.Height;
        double newWidth = _decalHandleStartWidth * dragScale;
        double newHeight = newWidth / aspect;
        if (newWidth < 12 || newHeight < 12) return;

        decal.Width = newWidth;
        decal.Height = newHeight;
        decal.X = cx0 - newWidth / 2;
        decal.Y = cy0 - newHeight / 2;

        UpdateDecalHandles();
        ScheduleCompositeRender();
    }

    /// <summary>デカールの中心・サイズをセットしつつ、未回転バウンズを
    /// キャンバス(クロップ範囲)内へクランプする。回転している枠は端が
    /// 多少はみ出し得るが、写真の縁取り用途(回転0)では端にぴったり合う。</summary>
    private static void ApplyDecalRect(DecalLayer decal, double centerX, double centerY, double width, double height,
        (double Left, double Top, double Width, double Height) crop)
    {
        double maxX = crop.Left + Math.Max(0, crop.Width - width);
        double maxY = crop.Top + Math.Max(0, crop.Height - height);
        decal.Width = width;
        decal.Height = height;
        decal.X = Math.Clamp(centerX - width / 2, crop.Left, maxX);
        decal.Y = Math.Clamp(centerY - height / 2, crop.Top, maxY);
    }

    private void DecalHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingDecalHandle = false;
        _isCompositeDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
        // 図形はドラッグ中は引き伸ばしただけなので、確定した新サイズで焼き直す
        // (RerasterizeShapeDecal 内で本解像度の再合成もかかる)。画像デカールは
        // ここで本解像度に戻すため明示的に再合成。
        if (_placingDecal is { IsFrame: true } shape) RerasterizeShapeDecal(shape);
        else ScheduleCompositeRender();
        _undo.CommitChange(); // 焼き直し後の状態でコミット(新しい Pixels 参照を含める)
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
        _isCompositeDragging = true; // 回転中も縮小解像度で合成
        _undo.BeginChange(); // 回転1ジェスチャ=1 Undo ステップ
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
        _isCompositeDragging = false;
        DecalRotateGizmoHandle.ReleaseMouseCapture();
        e.Handled = true;
        ScheduleCompositeRender(); // 本解像度で描き直す
        _undo.CommitChange();
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
        _undo.BeginChange(); // 移動1ジェスチャ=1 Undo ステップ
        PreviewImage.CaptureMouse();
        return true;
    }

    private bool TryContinueDecalBodyDrag(MouseEventArgs e)
    {
        if (!_isDraggingDecalPlacement || _placingDecal is not { } decal || _photoPixelBuffer is not { } photo) return false;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double scale = PreviewBorder.Width / crop.Width * _previewZoom;
        var current = e.GetPosition(PreviewBorder);
        double nx = _decalBodyDragStartX + (current.X - _decalBodyDragStartMouse.X) / scale;
        double ny = _decalBodyDragStartY + (current.Y - _decalBodyDragStartMouse.Y) / scale;
        // 枠線デカールはキャンバス外へ出さない。画像デカールは従来どおり端を
        // はみ出させて配置できる。
        if (decal.IsFrame)
        {
            double maxX = crop.Left + Math.Max(0, crop.Width - decal.Width);
            double maxY = crop.Top + Math.Max(0, crop.Height - decal.Height);
            nx = Math.Clamp(nx, crop.Left, maxX);
            ny = Math.Clamp(ny, crop.Top, maxY);
        }
        decal.X = nx;
        decal.Y = ny;
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
        _undo.CommitChange();
        return true;
    }

    /// <summary>選択中デカールの矢印キー移動(1操作=1 Undo ステップ)。枠線は
    /// キャンバス内へクランプ、画像は端をはみ出させて置ける。</summary>
    private void NudgeDecal(DecalLayer decal, double dx, double dy)
    {
        if (_photoPixelBuffer is not { } photo) return;
        var crop = GetDisplayedCropRect(photo.Width, photo.Height);
        double nx = decal.X + dx, ny = decal.Y + dy;
        if (decal.IsFrame)
        {
            nx = Math.Clamp(nx, crop.Left, crop.Left + Math.Max(0, crop.Width - decal.Width));
            ny = Math.Clamp(ny, crop.Top, crop.Top + Math.Max(0, crop.Height - decal.Height));
        }
        if (nx == decal.X && ny == decal.Y) return;
        _undo.BeginChange();
        decal.X = nx;
        decal.Y = ny;
        UpdateDecalHandles();
        ScheduleCompositeRender();
        _undo.CommitChange();
    }

    /// <summary>テキスト入力/コンボにフォーカスがあるか -- ここに当たるときは
    /// デカールの矢印/Delete ショートカットを横取りしない(キャレット移動・
    /// 候補送り・文字削除を優先)。スライダー等は「デカール選択中に矢印」の
    /// 意図が明確なので横取り対象にしない。</summary>
    private static bool IsTextEntryFocused() =>
        System.Windows.Input.Keyboard.FocusedElement is
            System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.Primitives.Selector;

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

    private readonly record struct DecalRenderEntry(ImageAdjustment.PixelBuffer Pixels, double X, double Y, double Width, double Height, double Rotation, double Opacity);

    private List<DecalRenderEntry> CaptureBehindAvatarDecals(double scale, bool dragging) =>
        _decalLayerOrder.TakeWhile(l => l is not null)
            .Select(l => ToRenderEntry(l!, scale, dragging))
            .ToList();

    private List<DecalRenderEntry> CaptureInFrontOfAvatarDecals(double scale, bool dragging)
    {
        int avatarIndex = _decalLayerOrder.IndexOf(null);
        var after = avatarIndex < 0 ? _decalLayerOrder : _decalLayerOrder.Skip(avatarIndex + 1);
        return after.Where(l => l is not null).Select(l => ToRenderEntry(l!, scale, dragging)).ToList();
    }

    /// <summary>枠線デカールは(ドラッグ中でなければ)合成の出力解像度で焼いた
    /// バッファを使い、BlendDecalOnto の最近傍拡大でボケないようにする。画像
    /// デカールとドラッグ中はそのまま Pixels。UI スレッドから呼ぶこと。</summary>
    private static DecalRenderEntry ToRenderEntry(DecalLayer l, double scale, bool dragging)
    {
        var pixels = l.Pixels;
        if (l.IsFrame && !dragging)
        {
            int outW = (int)Math.Round(l.Width * scale);
            int outH = (int)Math.Round(l.Height * scale);
            if (outW >= 2 && outH >= 2) pixels = EnsureFrameRenderBuffer(l, outW, outH);
        }
        return new DecalRenderEntry(pixels, l.X, l.Y, l.Width, l.Height, l.Rotation, l.Opacity);
    }

    private static ImageAdjustment.PixelBuffer EnsureFrameRenderBuffer(DecalLayer d, int outW, int outH)
    {
        outW = Math.Clamp(outW, 2, 4096);
        outH = Math.Clamp(outH, 2, 4096);
        if (d.FrameRenderCache is { } cache
            && d.FrameRenderCacheW == outW && d.FrameRenderCacheH == outH
            && d.FrameRenderCacheColor == d.ShapeColor && d.FrameRenderCacheStroke == d.ShapeStrokePercent)
            return cache;

        var baked = ShapeRasterizer.RasterizeFrame(outW, outH, d.ShapeColor, d.ShapeStrokePercent);
        d.FrameRenderCache = baked;
        d.FrameRenderCacheW = outW;
        d.FrameRenderCacheH = outH;
        d.FrameRenderCacheColor = d.ShapeColor;
        d.FrameRenderCacheStroke = d.ShapeStrokePercent;
        return baked;
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
        if (decal.Opacity <= 0) return;
        double opacity = Math.Clamp(decal.Opacity, 0, 1);
        var src = decal.Pixels;
        double centerX = x + width / 2;
        double centerY = y + height / 2;
        double halfW = width / 2, halfH = height / 2;
        double rad = decal.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        // 走査する回転後のバウンディングボックス ── ImageAdjustment.RenderOverlayForComposite が
        // アバターの回転後範囲に使うのと同じパディング式。
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
                // 逆回転(スクリーン空間 → デカールのローカル空間)。
                double localX = relX * cos + relY * sin;
                double localY = -relX * sin + relY * cos;
                if (localX < -halfW || localX >= halfW || localY < -halfH || localY >= halfH) continue;

                int srcX = Math.Clamp((int)((localX + halfW) / width * src.Width), 0, src.Width - 1);
                int srcY = Math.Clamp((int)((localY + halfH) / height * src.Height), 0, src.Height - 1);
                int srcIdx = srcY * src.Stride + srcX * 4;
                byte sa = src.Pixels[srcIdx + 3];
                if (sa == 0) continue;
                int dstIdx = destRow + dx * 4;
                double effSa = sa * opacity; // デカール個別の不透明度を実効アルファに乗せる
                double a = effSa / 255.0;
                double inv = 1 - a;
                dest.Pixels[dstIdx] = (byte)(src.Pixels[srcIdx] * a + dest.Pixels[dstIdx] * inv);
                dest.Pixels[dstIdx + 1] = (byte)(src.Pixels[srcIdx + 1] * a + dest.Pixels[dstIdx + 1] * inv);
                dest.Pixels[dstIdx + 2] = (byte)(src.Pixels[srcIdx + 2] * a + dest.Pixels[dstIdx + 2] * inv);
                dest.Pixels[dstIdx + 3] = (byte)(effSa + dest.Pixels[dstIdx + 3] * inv);
            }
        }
    }
}
