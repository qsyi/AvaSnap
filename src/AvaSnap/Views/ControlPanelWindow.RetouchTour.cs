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
            "「アバターに近づける」または「背景に近づける」で、色調をワンクリックで寄せられます。",
            () => MatchAvatarToPhotoButton),
        new("色を微調整",
            "必要なら明るさ・彩度などのスライダーで整えます。",
            () => AvatarLookCard),
        new("背景をぼかす",
            "背景を少しぼかすとアバターが馴染みます。",
            () => PhotoBlurSlider),
        new("ドロップシャドウ",
            "アバターの影の強さを調整します。",
            () => DropShadowSlider),
        new("トーングラデーション",
            "「自動判定」でベースを作ってから、強さ・向きを微調整します。",
            () => ToneGradientAutoDetectButton),
        new("ライトリーク",
            "光が差し込んだような効果。強さを調整します。",
            () => LightLeakSlider),
        new("肌色補正",
            "スポイトでアバターの肌の色を拾うと、その色が白になるように合成全体を補正します。",
            () => SkinWbEyedropperButton),
        new("被写界深度をオン",
            "深度を推定して、ピント面から外れた部分をぼかします。ここでオンにします。",
            () => DepthBlurEnableButton),
        new("フォーカス位置を選択",
            "オンにしてから、このボタンを押してプレビュー上で顔をクリックするとそこにピントが合います。一度計算すれば以降は自動で再計算されます。",
            () => DepthFocusPickButton),
        new("枠線を追加",
            "写真の縁取り枠を追加できます。追加後、サムネイルをドラッグして「アバター」マーカーより左に置くと、アバターの後ろに回せます。",
            () => AddRectangleFrameDecalButton),
        new("マスクレイヤー",
            "「マスクを追加」で、効果を効かせる範囲をブラシで指定できます。編集内容は自動保存され、仕上げた画像は「この合成結果を保存」で書き出します。",
            () => AddMaskButton),
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
        try { target?.BringIntoView(); } catch { }
        PositionTour(); // LayoutUpdated を待たず即1回(見えている間の初期表示用)
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
            if (t is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 })
            {
                var r = t.TransformToVisual(RetouchTourOverlay)
                         .TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                // 画面内に少しでも入っているときだけハイライト対象にする。
                if (r.Right > 0 && r.Bottom > 0 && r.Left < ow && r.Top < oh)
                    targetRect = Rect.Intersect(r, new Rect(0, 0, ow, oh));
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
            TourCard.Measure(new Size(ow, oh));
            double cw0 = TourCard.DesiredSize.Width, ch0 = TourCard.DesiredSize.Height;
            TourCard.Margin = new Thickness(Math.Max(0, (ow - cw0) / 2), Math.Max(0, (oh - ch0) / 2), 0, 0);
            return;
        }

        // 前回と同じ位置なら何もしない(LayoutUpdated は高頻度)。
        if (RectsClose(rect, _lastTourRect)) return;
        _lastTourRect = rect;

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

        // カード位置。対象が画面右寄りなら左隣、左寄りなら右隣、それ以外は下(入らなければ上)。
        TourCard.HorizontalAlignment = HorizontalAlignment.Left;
        TourCard.VerticalAlignment = VerticalAlignment.Top;
        TourCard.Measure(new Size(ow, oh));
        double cw = TourCard.DesiredSize.Width, ch = TourCard.DesiredSize.Height;
        const double gap = 14;
        double targetCx = rect.X + rect.Width / 2;

        double cx, cy;
        if (targetCx > ow * 0.55 && rect.X - gap - cw >= 8)
        {
            cx = rect.X - gap - cw;                    // 右寄りの対象 → 左隣
            cy = rect.Y;
        }
        else if (targetCx < ow * 0.45 && rect.Right + gap + cw <= ow - 8)
        {
            cx = rect.Right + gap;                     // 左寄りの対象 → 右隣
            cy = rect.Y;
        }
        else
        {
            cx = targetCx - cw / 2;                    // 中央付近 → 下、入らなければ上
            cy = hy + hh + gap;
            if (cy + ch > oh) cy = hy - gap - ch;
        }
        cx = Math.Clamp(cx, 8, Math.Max(8, ow - cw - 8));
        cy = Math.Clamp(cy, 8, Math.Max(8, oh - ch - 8));
        TourCard.Margin = new Thickness(cx, cy, 0, 0);
    }

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
