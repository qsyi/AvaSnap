using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- 8c. レタッチモードの初回コーチマークツアー: 対象UIをハイライトし、その隣に
//      説明カードを出して順番に送る。常時「スキップ」。スキップ/完了で既読、途中で
//      ウィンドウを閉じたら次回また最初から(既読フラグを立てないだけ)。 ----
public partial class ControlPanelWindow
{
    private sealed record TourStep(string Title, string Body, Func<FrameworkElement?> Target);

    private List<TourStep>? _tourSteps;
    private int _tourIndex;
    private bool _retouchTourShownThisSession;

    private List<TourStep> BuildTourSteps() => new()
    {
        new("アバター画像を読み込む",
            "Unityなどで書き出した透過アバター画像を、このボタンから読み込みます。",
            () => CompositeLoadImageButton),
        new("背景写真を選ぶ",
            "アバターの背景になる写真を読み込みます。",
            () => PickPhotoButton),
        new("色を自動で合わせる",
            "「アバターに近づける」または「背景に近づける」で、色調をワンクリックで寄せられます。あとは各スライダーや仕上げエフェクトで自由に調整。編集内容は自動保存され、仕上げた画像は「この合成結果を保存」で書き出します。",
            () => MatchAvatarToPhotoButton),
    };

    private void OpenRetouchTourButton_Click(object sender, RoutedEventArgs e) => StartRetouchTour(manual: true);

    private void StartRetouchTour(bool manual = false)
    {
        if (RetouchTourOverlay.Visibility == Visibility.Visible) return;
        if (!manual && _retouchTourShownThisSession) return;
        _retouchTourShownThisSession = true;

        _tourSteps ??= BuildTourSteps();
        _tourIndex = 0;
        RetouchTourOverlay.Visibility = Visibility.Visible;
        // レイアウトが1パス終わるたびに位置決めをやり直す ── リサイズ/スクロールが
        // 何段階かに分かれても、最後に落ち着いた状態で必ず正しく置ける。
        LayoutUpdated -= Tour_LayoutUpdated;
        LayoutUpdated += Tour_LayoutUpdated;
        RefreshEmptyPreviewHint();
        ShowTourStep();
    }

    /// <summary>直近で位置決めに使った対象矩形。変化が無ければ再配置しない(LayoutUpdated
    /// は高頻度なので)。</summary>
    private Rect _lastTourRect = Rect.Empty;
    private double _lastCardH = -1;

    private void Tour_LayoutUpdated(object? sender, EventArgs e)
    {
        if (RetouchTourOverlay.Visibility == Visibility.Visible) PositionTour();
    }

    private void ShowTourStep()
    {
        if (_tourSteps is not { Count: > 0 } steps) { EndRetouchTour(markSeen: true); return; }
        _tourIndex = Math.Clamp(_tourIndex, 0, steps.Count - 1);
        var step = steps[_tourIndex];

        TourTitleText.Text = step.Title;
        TourBodyText.Text = step.Body;
        TourProgressText.Text = $"{_tourIndex + 1} / {steps.Count}";
        TourBackButton.IsEnabled = _tourIndex > 0;
        TourNextButton.Content = _tourIndex == steps.Count - 1 ? "完了" : "次へ";
        _lastTourRect = Rect.Empty; // ステップが変わったので必ず置き直す

        FrameworkElement? target = null;
        try { target = step.Target(); } catch { /* 未実現なら中央フォールバック */ }
        try
        {
            if (target is { } t) CenterTargetInScroller(t);
            PositionTour(); // まず1回(スクロール反映前の暫定描画)
        }
        catch { /* レイアウト途中の一時例外は握りつぶす(LayoutUpdated で置き直る) */ }
        Dispatcher.InvokeAsync(PositionTour, DispatcherPriority.Loaded); // スクロール後のレイアウトで置き直す
    }

    /// <summary>ハイライト対象がコントロール列のスクロールビューの縦中央に来るように
    /// スクロールする(端に貼り付くと見づらい)。</summary>
    private void CenterTargetInScroller(FrameworkElement target)
    {
        var sv = CompositeCardsScrollViewer;
        try
        {
            var p = target.TransformToVisual(sv).Transform(new Point(0, 0));
            double targetCenter = p.Y + target.ActualHeight / 2;
            double delta = targetCenter - sv.ViewportHeight / 2;
            double newOffset = Math.Clamp(sv.VerticalOffset + delta, 0, sv.ScrollableHeight);
            sv.ScrollToVerticalOffset(newOffset);
        }
        catch { }
    }

    private void PositionTour()
    {
        if (RetouchTourOverlay.Visibility != Visibility.Visible) return;
        if (_tourSteps is not { Count: > 0 } steps) return;

        double ow = RetouchTourOverlay.ActualWidth, oh = RetouchTourOverlay.ActualHeight;
        if (ow <= 1 || oh <= 1) return;

        Rect? targetRect = null;
        try
        {
            var t = steps[_tourIndex].Target();
            // IsVisible は使わない(スクロール外/未実現でも tree にあれば座標は取れる)。
            // tree に繋がっていなければ TransformToVisual が投げるので catch で拾う。
            if (t is { ActualWidth: > 0, ActualHeight: > 0 })
            {
                var r = t.TransformToVisual(RetouchTourOverlay)
                         .TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                // 画面内に十分入っているときだけハイライト対象にする(スクロール反映前は弾く)。
                var vis = Rect.Intersect(r, new Rect(0, 0, ow, oh));
                if (vis.Width >= r.Width * 0.5 && vis.Height >= r.Height * 0.5)
                    targetRect = vis;
            }
        }
        catch { }

        if (targetRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            // このステップを一度でも対象付きで置けているなら、途中の取得失敗では動かさない。
            if (!_lastTourRect.IsEmpty) return;

            // フォールバック: 全面ディム + カード中央、ハイライト無し。
            SetDim(TourDimTop, 0, 0, ow, oh);
            SetDim(TourDimBottom, 0, 0, 0, 0);
            SetDim(TourDimLeft, 0, 0, 0, 0);
            SetDim(TourDimRight, 0, 0, 0, 0);
            TourHighlightRing.Visibility = Visibility.Collapsed;
            TourCard.HorizontalAlignment = HorizontalAlignment.Left;
            TourCard.VerticalAlignment = VerticalAlignment.Top;
            double cw0 = CardW(), ch0 = CardH();
            TourCard.Margin = new Thickness(Math.Max(0, (ow - cw0) / 2), Math.Max(0, (oh - ch0) / 2), 0, 0);
            return;
        }

        // 前回と同じ位置・同じカード高さなら何もしない(LayoutUpdated は高頻度)。
        if (RectsClose(rect, _lastTourRect) && Math.Abs(CardH() - _lastCardH) < 2) return;
        _lastTourRect = rect;
        _lastCardH = CardH();

        const double pad = 4;
        double hx = Math.Max(0, rect.X - pad), hy = Math.Max(0, rect.Y - pad);
        double hw = Math.Min(ow - hx, rect.Width + pad * 2);
        double hh = Math.Min(oh - hy, rect.Height + pad * 2);

        // 穴の周りを4枚で覆う。
        SetDim(TourDimTop, 0, 0, ow, hy);
        SetDim(TourDimBottom, 0, hy + hh, ow, Math.Max(0, oh - (hy + hh)));
        SetDim(TourDimLeft, 0, hy, hx, hh);
        SetDim(TourDimRight, hx + hw, hy, Math.Max(0, ow - (hx + hw)), hh);

        TourHighlightRing.Visibility = Visibility.Visible;
        TourHighlightRing.Margin = new Thickness(hx, hy, 0, 0);
        TourHighlightRing.Width = hw;
        TourHighlightRing.Height = hh;

        // カードはハイライトのすぐ左隣(プレビュー領域側)に置く。縦はハイライトの
        // 中央に合わせる。ハイライトは縦中央付近へスクロール済みなので、対象の真横に
        // 出る形になり、コントロールを覆わない。
        // ※ LayoutUpdated 中に Measure() を呼ぶと DesiredSize が不安定なので、
        //   確定済みの ActualWidth/Height を使う(未確定時は既定値でフォールバック)。
        TourCard.HorizontalAlignment = HorizontalAlignment.Left;
        TourCard.VerticalAlignment = VerticalAlignment.Top;
        double cw = CardW(), ch = CardH();

        // プレビュー領域の矩形(取れなければ画面左 ~55% を使う)。左端の下限に使う。
        Rect area = new(0, 0, ow * 0.55, oh);
        try
        {
            var pv = PreviewHost.TransformToVisual(RetouchTourOverlay)
                                .TransformBounds(new Rect(0, 0, PreviewHost.ActualWidth, PreviewHost.ActualHeight));
            if (pv.Width > 100 && pv.Height > 100) area = pv;
        }
        catch { }

        const double gap = 16;
        double leftMin = Math.Max(8, area.X + 8);
        // まず対象の左隣。入らなければ対象の右隣、それも無理なら画面内へクランプ。
        double cx = rect.X - gap - cw;
        if (cx < leftMin)
        {
            double right = rect.Right + gap;
            cx = right + cw <= ow - 8 ? right : leftMin;
        }
        double cy = rect.Y + rect.Height / 2 - ch / 2;   // ハイライトの縦中央
        cx = Math.Clamp(cx, 8, Math.Max(8, ow - cw - 8));
        cy = Math.Clamp(cy, 8, Math.Max(8, oh - ch - 8));
        TourCard.Margin = new Thickness(cx, cy, 0, 0);
    }

    private double CardW() => TourCard.ActualWidth > 20 ? TourCard.ActualWidth : 340;
    private double CardH() => TourCard.ActualHeight > 20 ? TourCard.ActualHeight : 150;

    private static bool RectsClose(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1
        && Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;

    private static void SetDim(FrameworkElement el, double x, double y, double w, double h)
    {
        el.Margin = new Thickness(x, y, 0, 0);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
    }

    private void TourNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tourSteps is not { Count: > 0 } steps) { EndRetouchTour(markSeen: true); return; }
        if (_tourIndex >= steps.Count - 1) { EndRetouchTour(markSeen: true); return; }
        _tourIndex++;
        ShowTourStep();
    }

    private void TourBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tourIndex <= 0) return;
        _tourIndex--;
        ShowTourStep();
    }

    private void TourSkipButton_Click(object sender, RoutedEventArgs e) => EndRetouchTour(markSeen: true);

    // ディム部分のクリックは吸収するだけ(順送りを崩さない)。
    private void TourDim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void EndRetouchTour(bool markSeen)
    {
        LayoutUpdated -= Tour_LayoutUpdated;
        RetouchTourOverlay.Visibility = Visibility.Collapsed;
        _lastTourRect = Rect.Empty;
        if (markSeen) SettingsService.MarkGuideSeen(GuideKind.Retouch);
        RefreshEmptyPreviewHint();
    }
}
