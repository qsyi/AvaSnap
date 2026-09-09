using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- アプリ内ワークフロー案内: 空状態ヒント / サブモード上端バナー /
//      初回ガイド(ウェルカム・位置合わせモード)。レタッチモードの順送りツアーは
//      ControlPanelWindow.RetouchTour.cs。 ----
public partial class ControlPanelWindow
{
    // ---- 2. レタッチモード空状態のヒント ----

    /// <summary>アバター・背景とも未読み込みで「なし」も未選択のときだけ、プレビュー
    /// 中央に読み込み案内を出す。何か読み込む/「なし」を選ぶ/ドラッグ開始で消える。</summary>
    private void RefreshEmptyPreviewHint()
    {
        bool empty =
            CompositePanel.Visibility == Visibility.Visible
            && _overlayWindow.OriginalPixelBuffer is null
            && _photoPixelBuffer is null
            && !_isBlankCanvasActive
            && !_compositeSkipAvatar
            && DropGuideOverlay.Visibility != Visibility.Visible
            && RetouchTourOverlay.Visibility != Visibility.Visible;
        EmptyPreviewHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 6. サブモード中の上端バナー ----

    /// <summary>切り抜き / アバター配置 / デカール編集 / マスク編集 のいずれかに
    /// 入っている間、プレビュー上端に何のモードかと確定/キャンセルの場所を出す。
    /// これらは排他なので単純な優先順位で判定。RefreshSliderLockState から呼ばれる。</summary>
    private void RefreshSubModeBanner()
    {
        string? text =
            _isCropModeActive ? "切り抜き中 — プレビューをドラッグして範囲を調整。下のボタンで確定 / キャンセル"
            : _isAvatarPlacementModeActive ? "アバター配置中 — ドラッグとハンドルで位置・サイズ・回転。下のボタンで確定 / キャンセル"
            : _isDecalPlacementModeActive ? "デカール編集中 — ドラッグとハンドルで調整。下のボタンで確定 / キャンセル"
            : _isMaskEditModeActive ? "マスク編集中 — ブラシで塗って範囲を作成。下のボタンで確定 / キャンセル"
            : null;

        if (text is null)
        {
            SubModeBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubModeBannerText.Text = text;
            SubModeBanner.Visibility = Visibility.Visible;
        }
        RefreshEmptyPreviewHint();
    }

    // ---- 8a / 8b. スライドイン初回ガイド(Unity連携ガイドと同じ作り) ----

    private static void SlideGuideIn(UIElement overlay, TranslateTransform transform)
    {
        overlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(UnityIntegrationGuideOffscreenY, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private static void SlideGuideOut(UIElement overlay, TranslateTransform transform)
    {
        var anim = new DoubleAnimation(0, UnityIntegrationGuideOffscreenY, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => overlay.Visibility = Visibility.Collapsed;
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // 8a. ウェルカムガイド

    private void ShowWelcomeGuide()
    {
        RefreshWatchFolderText(); // ガイド内の監視フォルダ表示を最新に
        SlideGuideIn(WelcomeGuideOverlay, WelcomeGuideTransform);
    }

    private void OpenWelcomeGuideButton_Click(object sender, RoutedEventArgs e) => ShowWelcomeGuide();
    private void OpenAlignGuideButton_Click(object sender, RoutedEventArgs e) => ShowAlignGuide();

    private void CloseWelcomeGuide()
    {
        SettingsService.MarkGuideSeen(GuideKind.Welcome);
        SlideGuideOut(WelcomeGuideOverlay, WelcomeGuideTransform);
    }

    private void CloseWelcomeGuideButton_Click(object sender, RoutedEventArgs e) => CloseWelcomeGuide();
    private void WelcomeGuideScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseWelcomeGuide();

    // 8b. 位置合わせモードガイド

    private void ShowAlignGuide() => SlideGuideIn(AlignGuideOverlay, AlignGuideTransform);

    private void CloseAlignGuide()
    {
        SettingsService.MarkGuideSeen(GuideKind.Align);
        SlideGuideOut(AlignGuideOverlay, AlignGuideTransform);
    }

    private void CloseAlignGuideButton_Click(object sender, RoutedEventArgs e) => CloseAlignGuide();
    private void AlignGuideScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseAlignGuide();
}
