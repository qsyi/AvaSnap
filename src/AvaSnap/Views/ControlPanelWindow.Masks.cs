using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- マスクレイヤー: 「効果が空間的にどれだけ効くか」を塗って作るグレースケール。
//      合成モード最下段の MaskLayerCard から「マスクを追加」→ デカールと同じ枠組みの
//      編集モード(切り抜き後キャンバスを表示 + 下部の確定/キャンセルバー = 既存
//      PreviewModeConfirmBar を流用)へ。既定は全面 白(効果1)。ツールは
//      ペン(黒を塗る=効果0) / 消しゴム(黒を消す=効果1へ戻す) / グラデーション
//      (線形・円形) / 全消去(全面 効果1=既定) / 全塗り(全面 効果0)。
//      レイヤー順・範囲選択は無い。
//
//      作ったマスクは、アバター/背景の色調補正スライダー・トーングラデーション・
//      ライトリークの各行の「マスク」欄から割り当てる。割り当てられたスライダーの効果は、
//      そのマスクのカバレッジで画素ごとに重み付けされる(黒=効果0 / 白=効果1)。
//
//      マスクは MaskOp の順序付きリスト(MaskRasterizer.Bake で R8 へ焼く)。Undo は
//      リストの切り詰め。CompositeSnapshot.Masks 経由で Undo/Redo の1タイムラインに乗る。
//      永続化は今回のスコープ外(セッション内のみ)。写真ソース変更・背景なしキャンバス
//      作成で破棄する(_decalLayerOrder リセットと同じタイミング)。 ----

/// <summary>マスク欄を付けられるスライダーの識別子。ティント色/強度は1つにまとめる。
/// Photo* = 背景写真の色調補正、Avatar* = アバター(紙立て看板)の色調補正、
/// ToneGradient/LightLeak = 仕上げエフェクト。</summary>
public enum MaskTarget
{
    PhotoBrightness, PhotoContrast, PhotoSaturation, PhotoVibrance,
    PhotoTemperature, PhotoTint, PhotoHue,
    PhotoHighlights, PhotoShadows, PhotoWhites, PhotoBlacks,
    PhotoColorTint,
    ToneGradient, LightLeak,

    AvatarBrightness, AvatarContrast, AvatarSaturation, AvatarVibrance,
    AvatarTemperature, AvatarTint, AvatarHue,
    AvatarHighlights, AvatarShadows, AvatarWhites, AvatarBlacks,
    AvatarColorTint,
}

public readonly record struct MaskAssignment(int Target, int MaskId) : IEquatable<MaskAssignment>;

/// <summary>マスク1枚の不変スナップショット。ベイク結果は持たず op リストから焼き直す
/// (<see cref="DecalEntrySnapshot"/> の枠線と同じ方針)。</summary>
public sealed record MaskLayerSnapshot(
    int Id, string Name, bool Invert,
    EquatableArray<MaskOp> Ops);

/// <summary><see cref="CompositeSnapshot"/> に畳み込むマスク全体 + スライダーへの割り当て。</summary>
public sealed record CompositeMasks(
    EquatableArray<MaskLayerSnapshot> Masks,
    EquatableArray<MaskAssignment> Assignments);

public partial class ControlPanelWindow
{
    private sealed class MaskLayer
    {
        public required int Id { get; init; }
        public string Name = "";
        public bool Invert;
        public readonly List<MaskOp> Ops = new();

        /// <summary>Ops を変えるたびに ++。ベイクキャッシュの鍵。</summary>
        public int Revision;

        private byte[]? _baked;
        private int _bakedW, _bakedH, _bakedRevision = -1;

        public void BumpRevision() => Revision++;

        /// <summary>指定解像度の R8 カバレッジ(0=効果0 / 255=効果1)を返す。必要なら焼き直す。
        /// <c>反転</c>はここでは適用しない -- サンプル時に評価する。</summary>
        public byte[] EnsureBaked(int w, int h)
        {
            if (_baked is not null && _bakedW == w && _bakedH == h && _bakedRevision == Revision)
                return _baked;
            _baked = MaskRasterizer.Bake(Ops, w, h);
            _bakedW = w;
            _bakedH = h;
            _bakedRevision = Revision;
            return _baked;
        }

        public MaskLayerSnapshot ToSnapshot() =>
            new(Id, Name, Invert, new EquatableArray<MaskOp>(Ops.ToArray()));
    }

    private enum MaskTool { Pen, Erase, Gradient }

    private readonly List<MaskLayer> _maskLayers = new();
    private int _nextMaskId = 1;

    /// <summary>スライダー -> マスクId。無ければ「なし」。</summary>
    private readonly Dictionary<MaskTarget, int> _maskAssignments = new();

    private const int MaxMaskCount = 3;

    private bool _isMaskEditModeActive;
    private bool _isEditingExistingMask;
    private MaskLayer? _editingMask;

    private MaskTool _maskTool = MaskTool.Pen;
    private bool _maskGradientRadial;
    private double _maskBrushSize = 0.14;    // キャンバス短辺に対する直径の割合
    private double _maskBrushFeather = 0.5;  // 0 = くっきり / 1 = なだらか

    private int _maskEditEntryOpCount;
    private bool _maskEditEntryInvert;

    private bool _isMaskStroking;
    private readonly List<MaskStrokePoint> _maskStrokePoints = new();

    // 0=なし / 1=ハンドルA / 2=ハンドルB / 3=新規グラデーション
    private int _maskGradHandleDrag;
    private (double ax, double ay, double bx, double by) _maskGradDrag;

    private MaskLayer? FindMask(int id) => _maskLayers.FirstOrDefault(m => m.Id == id);

    // ---- Undo/Redo 連携 ----

    private CompositeMasks CaptureMaskSnapshot()
    {
        var masks = _maskLayers.Select(m => m.ToSnapshot()).ToArray();
        var assigns = _maskAssignments
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => new MaskAssignment((int)kv.Key, kv.Value))
            .ToArray();
        return new CompositeMasks(new EquatableArray<MaskLayerSnapshot>(masks), new EquatableArray<MaskAssignment>(assigns));
    }

    private void ApplyMaskSnapshot(CompositeMasks snap)
    {
        var maskEntries = snap.Masks.AsArray();
        var assignEntries = snap.Assignments.AsArray();

        int prevEditingId = _editingMask?.Id ?? -1;

        _maskLayers.Clear();
        foreach (var e in maskEntries)
        {
            var layer = new MaskLayer { Id = e.Id, Name = e.Name, Invert = e.Invert };
            layer.Ops.AddRange(e.Ops.AsArray());
            layer.BumpRevision();
            _maskLayers.Add(layer);
            if (e.Id >= _nextMaskId) _nextMaskId = e.Id + 1;
        }

        _maskAssignments.Clear();
        foreach (var a in assignEntries)
        {
            if (Enum.IsDefined(typeof(MaskTarget), a.Target) && _maskLayers.Any(m => m.Id == a.MaskId))
                _maskAssignments[(MaskTarget)a.Target] = a.MaskId;
        }

        if (_isMaskEditModeActive)
        {
            _editingMask = prevEditingId >= 0 ? FindMask(prevEditingId) : null;
            if (_editingMask is null) ExitMaskEditMode();
        }

        RebuildMaskList();
        RefreshMaskChips();
        UpdateMaskEditOverlay();
        ScheduleCompositeRender();
    }

    /// <summary>写真ソース変更・背景なしキャンバス作成時。マスクと割り当てを全消去
    /// (座標は今のキャンバスに紐づくので写真が変われば意味を失う)。</summary>
    private void ClearMasks()
    {
        if (_isMaskEditModeActive) ExitMaskEditMode();
        _maskLayers.Clear();
        _maskAssignments.Clear();
        _nextMaskId = 1;
        RebuildMaskList();
        RefreshMaskChips();
    }

    // ---- カード: マスク一覧 + 追加 ----

    private void AddMaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_photoPixelBuffer is null) return;
        if (_maskLayers.Count >= MaxMaskCount)
        {
            MaskLimitNotice.Visibility = Visibility.Visible;
            return;
        }
        MaskLimitNotice.Visibility = Visibility.Collapsed;

        _undo.BeginChange();
        var mask = new MaskLayer { Id = _nextMaskId++, Name = $"マスク {_maskLayers.Count + 1}" };
        _maskLayers.Add(mask);
        RebuildMaskList();
        RefreshMaskChips();
        EnterMaskEditMode(mask, isNew: true);
        _undo.CommitChange();
    }

    private void RemoveMask(MaskLayer mask)
    {
        if (!_maskLayers.Contains(mask)) return;
        _undo.BeginChange();
        _maskLayers.Remove(mask);
        foreach (var key in _maskAssignments.Where(kv => kv.Value == mask.Id).Select(kv => kv.Key).ToList())
            _maskAssignments.Remove(key);
        if (_editingMask == mask) ExitMaskEditMode();
        RebuildMaskList();
        RefreshMaskChips();
        ScheduleCompositeRender();
        _undo.CommitChange();
    }

    private void SelectMaskForEdit(MaskLayer mask)
    {
        if (!_maskLayers.Contains(mask) || _editingMask == mask) return;
        if (_isMaskEditModeActive) ExitMaskEditMode();
        EnterMaskEditMode(mask, isNew: false);
    }

    // ---- 編集モード ----

    private void EnterMaskEditMode(MaskLayer mask, bool isNew)
    {
        if (_isCropModeActive) CropModeToggle.IsChecked = false;
        if (_isAvatarPlacementModeActive) AvatarPlacementModeToggle.IsChecked = false;
        if (_isDecalPlacementModeActive) ExitDecalPlacementMode();

        _editingMask = mask;
        _isMaskEditModeActive = true;
        _isEditingExistingMask = !isNew;
        _maskEditEntryOpCount = mask.Ops.Count;
        _maskEditEntryInvert = mask.Invert;
        _isMaskStroking = false;
        _maskStrokePoints.Clear();
        _maskGradHandleDrag = 0;

        MaskEditToolbar.Visibility = Visibility.Visible;
        MaskEditOverlay.Visibility = Visibility.Visible;
        MaskEditSurface.Visibility = Visibility.Visible;
        MaskEditLayer.Visibility = Visibility.Visible;

        SyncMaskToolbarUI();
        RefreshSliderLockState();
        RebuildMaskList();
        UpdateMaskEditOverlay();
        ScheduleCompositeRender();
        Dispatcher.BeginInvoke(new Action(() => MaskLayerCard.BringIntoView()), DispatcherPriority.Loaded);
    }

    private void ExitMaskEditMode()
    {
        _isMaskEditModeActive = false;
        _editingMask = null;
        _isEditingExistingMask = false;
        _isMaskStroking = false;
        _maskStrokePoints.Clear();
        _maskGradHandleDrag = 0;

        MaskEditToolbar.Visibility = Visibility.Collapsed;
        MaskEditOverlay.Visibility = Visibility.Collapsed;
        MaskEditOverlay.Source = null;
        MaskEditSurface.Visibility = Visibility.Collapsed;
        MaskEditLayer.Visibility = Visibility.Collapsed;

        RefreshSliderLockState();
        RebuildMaskList();
        ScheduleCompositeRender();
    }

    private void ConfirmMaskEdit() => ExitMaskEditMode();

    private void CancelMaskEdit()
    {
        if (_editingMask is not { } mask) { ExitMaskEditMode(); return; }
        if (_isEditingExistingMask)
        {
            _undo.BeginChange();
            while (mask.Ops.Count > _maskEditEntryOpCount) mask.Ops.RemoveAt(mask.Ops.Count - 1);
            mask.Invert = _maskEditEntryInvert;
            mask.BumpRevision();
            ExitMaskEditMode();
            _undo.CommitChange();
        }
        else
        {
            RemoveMask(mask); // 新規追加ごと取り消し
        }
    }

    // ---- ツールバー ----

    private void SyncMaskToolbarUI()
    {
        _suppressEventsDepth++;
        MaskToolPen.IsChecked = _maskTool == MaskTool.Pen;
        MaskToolErase.IsChecked = _maskTool == MaskTool.Erase;
        MaskToolGradient.IsChecked = _maskTool == MaskTool.Gradient;
        MaskGradientKindPanel.Visibility = _maskTool == MaskTool.Gradient ? Visibility.Visible : Visibility.Collapsed;
        MaskGradLinear.IsChecked = !_maskGradientRadial;
        MaskGradRadial.IsChecked = _maskGradientRadial;

        bool brush = _maskTool != MaskTool.Gradient;
        MaskBrushSizeRow.IsEnabled = brush;
        MaskBrushFeatherRow.IsEnabled = brush;
        MaskEditSurface.Cursor = brush ? Cursors.None : Cursors.Cross; // ブラシは自前の円、グラデーションは十字
        if (!brush) MaskBrushCursor.Visibility = MaskBrushInnerCursor.Visibility = Visibility.Collapsed;
        MaskBrushSizeSlider.Value = _maskBrushSize * 100;
        MaskBrushFeatherSlider.Value = _maskBrushFeather * 100;
        MaskInvertCheck.IsChecked = _editingMask?.Invert ?? false;
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
    }

    private void MaskTool_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ReferenceEquals(sender, MaskToolPen)) _maskTool = MaskTool.Pen;
        else if (ReferenceEquals(sender, MaskToolErase)) _maskTool = MaskTool.Erase;
        else if (ReferenceEquals(sender, MaskToolGradient)) _maskTool = MaskTool.Gradient;
        SyncMaskToolbarUI();
        UpdateMaskEditOverlay();
    }

    private void MaskGradKind_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _maskGradientRadial = ReferenceEquals(sender, MaskGradRadial);
        SyncMaskToolbarUI();
    }

    private void MaskBrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _maskBrushSize = Math.Clamp(e.NewValue / 100.0, 0.01, 1.0);
        UpdateMaskEditOverlay();
    }

    private void MaskBrushFeatherSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _maskBrushFeather = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
    }

    private void MaskInvertCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _editingMask is not { } m) return;
        _undo.BeginChange();
        m.Invert = MaskInvertCheck.IsChecked == true;
        _undo.CommitChange();
        UpdateMaskEditOverlay();
        RebuildMaskList();
        ScheduleCompositeRender();
    }

    private void MaskClearAllButton_Click(object sender, RoutedEventArgs e) => AppendMaskOp(MaskOp.MakeFill(1.0)); // 全消去 = 全面 効果1
    private void MaskFillAllButton_Click(object sender, RoutedEventArgs e) => AppendMaskOp(MaskOp.MakeFill(0.0));   // 全塗り = 全面 効果0

    private void AppendMaskOp(MaskOp op)
    {
        if (_editingMask is not { } m) return;
        _undo.BeginChange();
        m.Ops.Add(op);
        m.BumpRevision();
        _undo.CommitChange();
        UpdateMaskEditOverlay();
        RebuildMaskList();
        RefreshMaskChips();
        ScheduleCompositeRender();
    }

    // ---- プレビュー上の描画入力 ----

    private (int W, int H) MaskBakeSize()
    {
        if (_photoPixelBuffer is not { } photo) return (MaskRasterizer.MaxDimension, MaskRasterizer.MaxDimension);
        var crop = GetCanvasCropRect(photo.Width, photo.Height);
        return MaskRasterizer.BakeSizeFor(crop.Width, crop.Height);
    }

    private Point MaskNormPos(Point screen)
    {
        double w = Math.Max(1, MaskEditSurface.ActualWidth);
        double h = Math.Max(1, MaskEditSurface.ActualHeight);
        return new Point(Math.Clamp(screen.X / w, 0, 1), Math.Clamp(screen.Y / h, 0, 1));
    }

    private static MaskOp? CurrentGradientOp(MaskLayer m)
    {
        for (int i = m.Ops.Count - 1; i >= 0; i--)
            if (m.Ops[i].Kind is MaskOpKind.LinearGradient or MaskOpKind.RadialGradient) return m.Ops[i];
        return null;
    }

    private int HitMaskGradHandle(Point norm)
    {
        if (_editingMask is not { } m || CurrentGradientOp(m) is not { } g) return 0;
        const double tol = 0.045;
        if (Math.Sqrt(Sq(norm.X - g.Ax) + Sq(norm.Y - g.Ay)) < tol) return 1;
        if (Math.Sqrt(Sq(norm.X - g.Bx) + Sq(norm.Y - g.By)) < tol) return 2;
        return 0;
    }

    private static double Sq(double v) => v * v;

    private void MaskEditSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingMask is not { } m) return;
        var p = MaskNormPos(e.GetPosition(MaskEditSurface));

        if (_maskTool == MaskTool.Gradient)
        {
            int hit = HitMaskGradHandle(p);
            if (hit != 0 && CurrentGradientOp(m) is { } g)
            {
                _maskGradHandleDrag = hit;
                _maskGradDrag = (g.Ax, g.Ay, g.Bx, g.By);
            }
            else
            {
                _maskGradHandleDrag = 3;
                _maskGradDrag = (p.X, p.Y, p.X, p.Y);
            }
            _undo.BeginChange();
            MaskEditSurface.CaptureMouse();
            e.Handled = true;
            PreviewLiveGradient();
            return;
        }

        _isMaskStroking = true;
        _maskStrokePoints.Clear();
        _maskStrokePoints.Add(new MaskStrokePoint(p.X, p.Y));
        _undo.BeginChange();
        MaskEditSurface.CaptureMouse();
        e.Handled = true;
        PreviewPendingStroke();
    }

    private void MaskEditSurface_MouseMove(object sender, MouseEventArgs e)
    {
        var screen = e.GetPosition(MaskEditSurface);
        PositionBrushCursor(screen);
        var p = MaskNormPos(screen);

        if (_maskGradHandleDrag != 0)
        {
            if (_maskGradHandleDrag == 1) { _maskGradDrag.ax = p.X; _maskGradDrag.ay = p.Y; }
            else { _maskGradDrag.bx = p.X; _maskGradDrag.by = p.Y; }
            PreviewLiveGradient();
            return;
        }

        if (!_isMaskStroking) return;
        var last = _maskStrokePoints[^1];
        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 0.0015) return;
        _maskStrokePoints.Add(new MaskStrokePoint(p.X, p.Y));
        PreviewPendingStroke();
    }

    private void MaskEditSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        MaskEditSurface.ReleaseMouseCapture();
        e.Handled = true;
        if (_editingMask is not { } m) return;

        if (_maskGradHandleDrag != 0)
        {
            _maskGradHandleDrag = 0;
            var (ax, ay, bx, by) = _maskGradDrag;
            if (Math.Sqrt(Sq(ax - bx) + Sq(ay - by)) < 0.01)
            {
                _undo.CommitChange(); // 実質何も動かなかった
                UpdateMaskEditOverlay();
                return;
            }
            m.Ops.RemoveAll(o => o.Kind is MaskOpKind.LinearGradient or MaskOpKind.RadialGradient); // 1マスク1グラデ
            m.Ops.Add(MaskOp.MakeGradient(_maskGradientRadial, ax, ay, bx, by));
            m.BumpRevision();
            _undo.CommitChange();
            UpdateMaskEditOverlay();
            RebuildMaskList();
            RefreshMaskChips();
            ScheduleCompositeRender();
            return;
        }

        if (!_isMaskStroking) return;
        _isMaskStroking = false;
        if (_maskStrokePoints.Count > 0)
        {
            m.Ops.Add(MaskOp.MakeStroke(_maskTool == MaskTool.Erase, _maskStrokePoints.ToArray(), _maskBrushSize, _maskBrushFeather));
            m.BumpRevision();
        }
        _maskStrokePoints.Clear();
        _undo.CommitChange();
        UpdateMaskEditOverlay();
        RebuildMaskList();
        RefreshMaskChips();
        ScheduleCompositeRender();
    }

    private void MaskEditSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        MaskBrushCursor.Visibility = _maskTool == MaskTool.Gradient ? Visibility.Collapsed : Visibility.Visible;
        PositionBrushCursor(e.GetPosition(MaskEditSurface));
    }

    private void MaskEditSurface_MouseLeave(object sender, MouseEventArgs e) =>
        MaskBrushCursor.Visibility = MaskBrushInnerCursor.Visibility = Visibility.Collapsed;

    // ---- オーバーレイ描画 ----

    private void PositionBrushCursor(Point screen)
    {
        if (_maskTool == MaskTool.Gradient)
        {
            MaskBrushCursor.Visibility = MaskBrushInnerCursor.Visibility = Visibility.Collapsed;
            return;
        }
        double shortSide = Math.Min(MaskEditSurface.ActualWidth, MaskEditSurface.ActualHeight);
        double r = Math.Max(2, _maskBrushSize * shortSide * 0.5);
        MaskBrushCursor.Width = MaskBrushCursor.Height = r * 2;
        Canvas.SetLeft(MaskBrushCursor, screen.X - r);
        Canvas.SetTop(MaskBrushCursor, screen.Y - r);
        MaskBrushCursor.Visibility = Visibility.Visible;

        // ぼかしの芯(内円)。feather 0 で外円と一致、feather 1 で半径0。
        double inner = r * Math.Clamp(1.0 - _maskBrushFeather, 0.0, 1.0);
        MaskBrushInnerCursor.Width = MaskBrushInnerCursor.Height = inner * 2;
        Canvas.SetLeft(MaskBrushInnerCursor, screen.X - inner);
        Canvas.SetTop(MaskBrushInnerCursor, screen.Y - inner);
        MaskBrushInnerCursor.Visibility = _maskBrushFeather > 0.02 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviewPendingStroke()
    {
        if (_editingMask is not { } m) return;
        var (bw, bh) = MaskBakeSize();
        var committed = m.EnsureBaked(bw, bh); // Revision キャッシュ。確定済み op はここで焼き済み
        var cov = _maskStrokePoints.Count == 0
            ? committed
            : MaskRasterizer.WithPendingStroke(committed, bw, bh, _maskTool == MaskTool.Erase, _maskStrokePoints, _maskBrushSize, _maskBrushFeather);
        SetMaskOverlayFromCoverage(cov, bw, bh, m.Invert);
    }

    private void PreviewLiveGradient()
    {
        if (_editingMask is not { } m) return;
        var (bw, bh) = MaskBakeSize();
        var ops = m.Ops.Where(o => o.Kind is not (MaskOpKind.LinearGradient or MaskOpKind.RadialGradient)).ToList();
        var (ax, ay, bx, by) = _maskGradDrag;
        ops.Add(MaskOp.MakeGradient(_maskGradientRadial, ax, ay, bx, by));
        SetMaskOverlayFromCoverage(MaskRasterizer.Bake(ops, bw, bh), bw, bh, m.Invert);
        PositionGradientHandles(ax, ay, bx, by);
    }

    private void UpdateMaskEditOverlay()
    {
        if (!_isMaskEditModeActive || _editingMask is not { } m)
        {
            MaskEditOverlay.Source = null;
            MaskGradLine.Visibility = MaskGradRing.Visibility = MaskGradHandleA.Visibility = MaskGradHandleB.Visibility = Visibility.Collapsed;
            return;
        }
        var (bw, bh) = MaskBakeSize();
        SetMaskOverlayFromCoverage(m.EnsureBaked(bw, bh), bw, bh, m.Invert);

        if (_maskTool == MaskTool.Gradient && CurrentGradientOp(m) is { } g)
            PositionGradientHandles(g.Ax, g.Ay, g.Bx, g.By);
        else
            MaskGradLine.Visibility = MaskGradRing.Visibility = MaskGradHandleA.Visibility = MaskGradHandleB.Visibility = Visibility.Collapsed;
    }

    /// <summary>カバレッジ R8 -> 黒オーバーレイ(不透明度 = (1 - 効果) * 80%)。
    /// 効果 = invert ? 1 - cov : cov。</summary>
    private void SetMaskOverlayFromCoverage(byte[] cov, int w, int h, bool invert)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            double effect = invert ? 1.0 - cov[i] / 255.0 : cov[i] / 255.0;
            byte a = (byte)((1.0 - effect) * 0.8 * 255.0);
            int o = i * 4;
            // BGRA、黒 + 上のアルファ
            px[o + 3] = a;
        }
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        wb.Freeze();
        MaskEditOverlay.Source = wb;
    }

    private void PositionGradientHandles(double ax, double ay, double bx, double by)
    {
        double sw = MaskEditSurface.ActualWidth, sh = MaskEditSurface.ActualHeight;
        if (sw <= 0 || sh <= 0) return;
        double ax1 = ax * sw, ay1 = ay * sh, bx1 = bx * sw, by1 = by * sh;

        const double hs = 14;
        Canvas.SetLeft(MaskGradHandleA, ax1 - hs / 2); Canvas.SetTop(MaskGradHandleA, ay1 - hs / 2);
        Canvas.SetLeft(MaskGradHandleB, bx1 - hs / 2); Canvas.SetTop(MaskGradHandleB, by1 - hs / 2);
        MaskGradHandleA.Visibility = MaskGradHandleB.Visibility = Visibility.Visible;

        if (_maskGradientRadial)
        {
            double radius = Math.Sqrt(Sq(bx1 - ax1) + Sq(by1 - ay1));
            MaskGradRing.Width = MaskGradRing.Height = radius * 2;
            Canvas.SetLeft(MaskGradRing, ax1 - radius); Canvas.SetTop(MaskGradRing, ay1 - radius);
            MaskGradRing.Visibility = Visibility.Visible;
            MaskGradLine.Visibility = Visibility.Collapsed;
        }
        else
        {
            MaskGradLine.X1 = ax1; MaskGradLine.Y1 = ay1; MaskGradLine.X2 = bx1; MaskGradLine.Y2 = by1;
            MaskGradLine.Visibility = Visibility.Visible;
            MaskGradRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---- カード一覧の組み立て ----

    private void RebuildMaskList()
    {
        MaskLayerList.Children.Clear();
        foreach (var mask in _maskLayers)
            MaskLayerList.Children.Add(BuildMaskListEntry(mask));
        MaskEmptyHint.Visibility = _maskLayers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddMaskButton.IsEnabled = _maskLayers.Count < MaxMaskCount && _photoPixelBuffer is not null;
        if (_maskLayers.Count < MaxMaskCount) MaskLimitNotice.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement BuildMaskListEntry(MaskLayer mask)
    {
        bool editing = mask == _editingMask;

        var thumb = new Image
        {
            Source = MakeMaskThumbnail(mask),
            Width = 88,
            Height = 52,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
        };
        var thumbBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("HairlineBrush"),
            Child = thumb,
        };

        var nameText = new TextBlock
        {
            Text = mask.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var editBtn = new Button
        {
            Content = editing ? "編集中" : "編集",
            Style = (Style)FindResource("SecondaryPillButton"),
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = !editing,
        };
        editBtn.Click += (_, _) => SelectMaskForEdit(mask);

        var delBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            Background = (Brush)FindResource("PrimaryBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "このマスクを削除",
        };
        delBtn.Click += (_, _) => RemoveMask(mask);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(thumbBorder);
        row.Children.Add(nameText);
        row.Children.Add(editBtn);
        row.Children.Add(delBtn);

        return new Border
        {
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource(editing ? "PrimaryTintBrush" : "InputBackgroundBrush"),
            BorderBrush = (Brush)FindResource(editing ? "PrimaryBrush" : "HairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    private static BitmapSource MakeMaskThumbnail(MaskLayer mask)
    {
        const int tw = 132, th = 78;
        var cov = MaskRasterizer.Bake(mask.Ops, tw, th);
        var px = new byte[tw * th * 4];
        for (int i = 0; i < tw * th; i++)
        {
            byte v = mask.Invert ? (byte)(255 - cov[i]) : cov[i]; // 白 = 効果1 / 黒 = 効果0
            int o = i * 4;
            px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
        }
        var wb = new WriteableBitmap(tw, th, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, tw, th), px, tw * 4, 0);
        wb.Freeze();
        return wb;
    }

    // ---- スライダーへのマスク割り当て UI(MaskAssignPanel、マスクレイヤーカード内)。
    //      マスクが1つも無ければパネルごと隠す。 ----

    /// <summary>アバター(紙立て看板)の色調補正。トーングラデ回転・ライトリーク角度・
    /// ティントのRGBは「量/色」のマスクに従属するので個別には出さない。</summary>
    private static readonly (MaskTarget Target, string Label)[] MaskAvatarTargets =
    {
        (MaskTarget.AvatarBrightness, "明るさ"),
        (MaskTarget.AvatarContrast, "コントラスト"),
        (MaskTarget.AvatarSaturation, "彩度"),
        (MaskTarget.AvatarVibrance, "自然な彩度"),
        (MaskTarget.AvatarTemperature, "色温度"),
        (MaskTarget.AvatarTint, "色かぶり補正"),
        (MaskTarget.AvatarHue, "色相"),
        (MaskTarget.AvatarHighlights, "ハイライト"),
        (MaskTarget.AvatarShadows, "シャドウ"),
        (MaskTarget.AvatarWhites, "白レベル"),
        (MaskTarget.AvatarBlacks, "黒レベル"),
        (MaskTarget.AvatarColorTint, "ティント色"),
    };

    /// <summary>背景写真の色調補正 + 仕上げエフェクト(トーングラデ / ライトリーク)。</summary>
    private static readonly (MaskTarget Target, string Label)[] MaskBackgroundTargets =
    {
        (MaskTarget.PhotoBrightness, "明るさ"),
        (MaskTarget.PhotoContrast, "コントラスト"),
        (MaskTarget.PhotoSaturation, "彩度"),
        (MaskTarget.PhotoVibrance, "自然な彩度"),
        (MaskTarget.PhotoTemperature, "色温度"),
        (MaskTarget.PhotoTint, "色かぶり補正"),
        (MaskTarget.PhotoHue, "色相"),
        (MaskTarget.PhotoHighlights, "ハイライト"),
        (MaskTarget.PhotoShadows, "シャドウ"),
        (MaskTarget.PhotoWhites, "白レベル"),
        (MaskTarget.PhotoBlacks, "黒レベル"),
        (MaskTarget.PhotoColorTint, "ティント色"),
        (MaskTarget.ToneGradient, "トーングラデーション"),
        (MaskTarget.LightLeak, "ライトリーク"),
    };

    private bool _suppressMaskChipEvents;
    // マスクを作ったら中身が見えている方が自然なので既定は展開。
    private bool _maskAssignExpanded = true;

    /// <summary>割り当て一覧を「アバター列 / 背景列」の縦2列で組み立てる。
    /// マスクが1つも無ければパネルごと隠す。</summary>
    private void RefreshMaskChips()
    {
        bool hasMasks = _maskLayers.Count > 0;
        MaskAssignPanel.Visibility = hasMasks ? Visibility.Visible : Visibility.Collapsed;
        MaskAssignRows.Children.Clear();
        if (!hasMasks) return;

        _suppressMaskChipEvents = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatarCol = BuildMaskAssignColumn("アバター", MaskAvatarTargets);
        Grid.SetColumn(avatarCol, 0);
        grid.Children.Add(avatarCol);

        var bgCol = BuildMaskAssignColumn("背景", MaskBackgroundTargets);
        Grid.SetColumn(bgCol, 2);
        grid.Children.Add(bgCol);

        MaskAssignRows.Children.Add(grid);
        _suppressMaskChipEvents = false;

        MaskAssignRows.Visibility = _maskAssignExpanded ? Visibility.Visible : Visibility.Collapsed;
        MaskAssignToggleButton.Content = (_maskAssignExpanded ? "▾ " : "▸ ") + "スライダーに割り当て";
    }

    private FrameworkElement BuildMaskAssignColumn(string header, (MaskTarget Target, string Label)[] targets)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 5),
        });

        foreach (var (target, label) in targets)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(labelText, 0);
            row.Children.Add(labelText);

            var combo = new ComboBox
            {
                Style = (Style)FindResource("CompactComboBox"),
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 11,
                Tag = target,
            };
            combo.Items.Add(new ComboBoxItem { Content = "なし", Tag = 0 });
            foreach (var mask in _maskLayers)
                combo.Items.Add(new ComboBoxItem { Content = mask.Name, Tag = mask.Id });

            int assigned = _maskAssignments.TryGetValue(target, out var id) ? id : 0;
            combo.SelectedIndex = 0;
            for (int i = 0; i < combo.Items.Count; i++)
                if (((ComboBoxItem)combo.Items[i]).Tag is int t && t == assigned) { combo.SelectedIndex = i; break; }

            combo.SelectionChanged += MaskAssignCombo_SelectionChanged;
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            panel.Children.Add(row);
        }
        return panel;
    }

    private void ToggleMaskAssignSection(object sender, RoutedEventArgs e)
    {
        _maskAssignExpanded = !_maskAssignExpanded;
        MaskAssignRows.Visibility = _maskAssignExpanded ? Visibility.Visible : Visibility.Collapsed;
        MaskAssignToggleButton.Content = (_maskAssignExpanded ? "▾ " : "▸ ") + "スライダーに割り当て";
    }

    private void MaskAssignCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMaskChipEvents || sender is not ComboBox combo
            || combo.Tag is not MaskTarget target
            || combo.SelectedItem is not ComboBoxItem item || item.Tag is not int maskId)
            return;

        _undo.BeginChange();
        if (maskId == 0) _maskAssignments.Remove(target);
        else _maskAssignments[target] = maskId;
        _undo.CommitChange();
        ScheduleCompositeRender();
    }

    // ---- レンダー適用: マスク割り当てがある時、(1+N)回パイプラインを回して加算デルタ
    //      合成する(対象は全てポイントワイズなので GPU シェーダには手を入れない)。 ----

    private sealed record MaskPlanGroup(byte[] Coverage, int CovW, int CovH, bool Invert, MaskTarget[] Targets);

    /// <summary>現在の割り当てから、実際に参照されているマスクだけのプラン(ベイク済み
    /// カバレッジ + 対象スライダー)を作る。空なら従来どおりの1パス。UI スレッドで呼ぶこと。</summary>
    private List<MaskPlanGroup> BuildMaskPlan()
    {
        var plan = new List<MaskPlanGroup>();
        if (_maskAssignments.Count == 0 || _photoPixelBuffer is null) return plan;
        var (bw, bh) = MaskBakeSize();
        foreach (var mask in _maskLayers)
        {
            var targets = _maskAssignments.Where(kv => kv.Value == mask.Id).Select(kv => kv.Key).ToArray();
            if (targets.Length == 0) continue;
            plan.Add(new MaskPlanGroup(mask.EnsureBaked(bw, bh), bw, bh, mask.Invert, targets));
        }
        return plan;
    }

    private ImageAdjustment.ColorAdjustments AvatarAdjustments => new(
        _state.Brightness, _state.Contrast, _state.Saturation,
        _state.Vibrance, _state.Temperature, _state.Tint, _state.Hue,
        _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks,
        _state.ColorTintStrength, _state.ColorTintR, _state.ColorTintG, _state.ColorTintB);

    private static bool IsAvatarTarget(MaskTarget t) => t is >= MaskTarget.AvatarBrightness and <= MaskTarget.AvatarColorTint;

    private static ImageAdjustment.ColorAdjustments WithTargetZeroed(ImageAdjustment.ColorAdjustments a, MaskTarget t) => t switch
    {
        MaskTarget.PhotoBrightness => a with { Brightness = 0 },
        MaskTarget.PhotoContrast => a with { Contrast = 0 },
        MaskTarget.PhotoSaturation => a with { Saturation = 0 },
        MaskTarget.PhotoVibrance => a with { Vibrance = 0 },
        MaskTarget.PhotoTemperature => a with { Temperature = 0 },
        MaskTarget.PhotoTint => a with { Tint = 0 },
        MaskTarget.PhotoHue => a with { Hue = 0 },
        MaskTarget.PhotoHighlights => a with { Highlights = 0 },
        MaskTarget.PhotoShadows => a with { Shadows = 0 },
        MaskTarget.PhotoWhites => a with { Whites = 0 },
        MaskTarget.PhotoBlacks => a with { Blacks = 0 },
        MaskTarget.PhotoColorTint => a with { ColorTintStrength = 0 },
        _ => a,
    };

    private static ImageAdjustment.ColorAdjustments WithTargetRestored(
        ImageAdjustment.ColorAdjustments a, ImageAdjustment.ColorAdjustments full, MaskTarget t) => t switch
    {
        MaskTarget.PhotoBrightness => a with { Brightness = full.Brightness },
        MaskTarget.PhotoContrast => a with { Contrast = full.Contrast },
        MaskTarget.PhotoSaturation => a with { Saturation = full.Saturation },
        MaskTarget.PhotoVibrance => a with { Vibrance = full.Vibrance },
        MaskTarget.PhotoTemperature => a with { Temperature = full.Temperature },
        MaskTarget.PhotoTint => a with { Tint = full.Tint },
        MaskTarget.PhotoHue => a with { Hue = full.Hue },
        MaskTarget.PhotoHighlights => a with { Highlights = full.Highlights },
        MaskTarget.PhotoShadows => a with { Shadows = full.Shadows },
        MaskTarget.PhotoWhites => a with { Whites = full.Whites },
        MaskTarget.PhotoBlacks => a with { Blacks = full.Blacks },
        MaskTarget.PhotoColorTint => a with
        {
            ColorTintStrength = full.ColorTintStrength,
            ColorTintR = full.ColorTintR, ColorTintG = full.ColorTintG, ColorTintB = full.ColorTintB,
        },
        _ => a,
    };

    /// <summary>アバターの色調補正版。Avatar* を対応する <see cref="ImageAdjustment.ColorAdjustments"/>
    /// フィールドへ写す(Photo* と同じフィールドだが別 enum)。</summary>
    private static ImageAdjustment.ColorAdjustments WithAvatarTargetZeroed(ImageAdjustment.ColorAdjustments a, MaskTarget t) => t switch
    {
        MaskTarget.AvatarBrightness => a with { Brightness = 0 },
        MaskTarget.AvatarContrast => a with { Contrast = 0 },
        MaskTarget.AvatarSaturation => a with { Saturation = 0 },
        MaskTarget.AvatarVibrance => a with { Vibrance = 0 },
        MaskTarget.AvatarTemperature => a with { Temperature = 0 },
        MaskTarget.AvatarTint => a with { Tint = 0 },
        MaskTarget.AvatarHue => a with { Hue = 0 },
        MaskTarget.AvatarHighlights => a with { Highlights = 0 },
        MaskTarget.AvatarShadows => a with { Shadows = 0 },
        MaskTarget.AvatarWhites => a with { Whites = 0 },
        MaskTarget.AvatarBlacks => a with { Blacks = 0 },
        MaskTarget.AvatarColorTint => a with { ColorTintStrength = 0 },
        _ => a,
    };

    private static ImageAdjustment.ColorAdjustments WithAvatarTargetRestored(
        ImageAdjustment.ColorAdjustments a, ImageAdjustment.ColorAdjustments full, MaskTarget t) => t switch
    {
        MaskTarget.AvatarBrightness => a with { Brightness = full.Brightness },
        MaskTarget.AvatarContrast => a with { Contrast = full.Contrast },
        MaskTarget.AvatarSaturation => a with { Saturation = full.Saturation },
        MaskTarget.AvatarVibrance => a with { Vibrance = full.Vibrance },
        MaskTarget.AvatarTemperature => a with { Temperature = full.Temperature },
        MaskTarget.AvatarTint => a with { Tint = full.Tint },
        MaskTarget.AvatarHue => a with { Hue = full.Hue },
        MaskTarget.AvatarHighlights => a with { Highlights = full.Highlights },
        MaskTarget.AvatarShadows => a with { Shadows = full.Shadows },
        MaskTarget.AvatarWhites => a with { Whites = full.Whites },
        MaskTarget.AvatarBlacks => a with { Blacks = full.Blacks },
        MaskTarget.AvatarColorTint => a with
        {
            ColorTintStrength = full.ColorTintStrength,
            ColorTintR = full.ColorTintR, ColorTintG = full.ColorTintG, ColorTintB = full.ColorTintB,
        },
        _ => a,
    };

    /// <summary>マスク割り当てがある時の合成。<paramref name="run"/> は
    /// (背景色調補正, トーングラデ量, ライトリーク量, アバターpixel変種インデックス) を
    /// 差し替えて1回分の合成結果を返すクロージャ(他の全パラメータは呼び出し側が固定)。
    /// アバターの色調補正マスクは、色違いに焼いたアバターpixelの何番を使うかで表現する
    /// (0 = 中立)。<c>R0</c>(対象を中立化) から各マスク群の <c>Rk</c> を加算デルタで
    /// 重ねる: <c>acc += cov_k * (Rk - acc)</c>。重なったマスクはリスト順。Task.Run 内から呼ぶ。</summary>
    private static WriteableBitmap BlendMasked(
        Func<ImageAdjustment.ColorAdjustments, double, double, int, WriteableBitmap> run,
        ImageAdjustment.ColorAdjustments fullAdj, double fullTone, double fullLeak,
        IReadOnlyList<MaskPlanGroup> groups, IReadOnlyList<int> groupVariantIndex,
        double cropLeft, double cropTop, double cropW, double cropH, double scale)
    {
        var baseAdj = fullAdj;
        double baseTone = fullTone, baseLeak = fullLeak;
        foreach (var g in groups)
            foreach (var t in g.Targets)
            {
                baseAdj = WithTargetZeroed(baseAdj, t);
                if (t == MaskTarget.ToneGradient) baseTone = 0;
                if (t == MaskTarget.LightLeak) baseLeak = 0;
            }

        var r0 = run(baseAdj, baseTone, baseLeak, 0);
        int w = r0.PixelWidth, h = r0.PixelHeight, stride = w * 4;
        var acc = new byte[stride * h];
        r0.CopyPixels(acc, stride, 0);

        double invScale = scale > 0 ? 1.0 / scale : 1.0;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var gAdj = baseAdj;
            double gTone = baseTone, gLeak = baseLeak;
            foreach (var t in g.Targets)
            {
                gAdj = WithTargetRestored(gAdj, fullAdj, t);
                if (t == MaskTarget.ToneGradient) gTone = fullTone;
                if (t == MaskTarget.LightLeak) gLeak = fullLeak;
            }

            var rk = run(gAdj, gTone, gLeak, groupVariantIndex[gi]);
            if (rk.PixelWidth != w || rk.PixelHeight != h) continue;
            var rkPx = new byte[stride * h];
            rk.CopyPixels(rkPx, stride, 0);

            for (int y = 0; y < h; y++)
            {
                double v = cropH > 0 ? (y * invScale - cropTop) / cropH : 0;
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    double u = cropW > 0 ? (x * invScale - cropLeft) / cropW : 0;
                    double cov = MaskRasterizer.SampleBilinear(g.Coverage, g.CovW, g.CovH, u, v);
                    double effect = g.Invert ? 1.0 - cov : cov;
                    if (effect <= 0.0) continue;
                    int i = row + x * 4;
                    acc[i]     = (byte)Math.Clamp(acc[i]     + effect * (rkPx[i]     - acc[i]),     0, 255);
                    acc[i + 1] = (byte)Math.Clamp(acc[i + 1] + effect * (rkPx[i + 1] - acc[i + 1]), 0, 255);
                    acc[i + 2] = (byte)Math.Clamp(acc[i + 2] + effect * (rkPx[i + 2] - acc[i + 2]), 0, 255);
                }
            }
        }

        var result = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, w, h), acc, stride, 0);
        result.Freeze();
        return result;
    }
}

