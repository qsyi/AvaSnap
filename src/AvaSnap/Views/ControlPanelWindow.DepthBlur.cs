using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- 被写界深度(深度依存ぼかし): Depth Anything V2 Small で合成結果から相対深度を
//      推定し、ピント面から外れた画素をピラミッド補間でぼかす。深度マップは合成の
//      スナップショットなので「深度を計算」ボタンで明示的に更新し、合成に影響する
//      変更で「再計算が必要」を表示する(自動再計算はしない)。深度マップは .avasnap
//      には保存せず、有効なプロジェクトを開いた直後に1回だけ計算する。 ----
public partial class ControlPanelWindow
{
    /// <summary>合成に影響する変更が入ったら、キャッシュ済み深度マップを「古い」印にする。</summary>
    private void MarkDepthMapStale()
    {
        if (_depthMap is null || _depthMapStale) return;
        _depthMapStale = true;
        RefreshDepthBlurUi();
    }

    /// <summary>レンダーされた合成 <paramref name="composite"/> に、キャッシュ済み深度で
    /// 被写界深度ぼかしを適用する(レンダー用 Task スレッドから呼ばれる)。無効/未計算なら素通し。</summary>
    private WriteableBitmap ApplyDepthBlurToComposite(WriteableBitmap composite, double renderScale)
    {
        // 計算中は素の合成を推定入力にしたいのでぼかしを挟まない。
        if (!_depthBlurEnabled || _depthComputing || _depthMap is not { } dm) return composite;

        int w = composite.PixelWidth, h = composite.PixelHeight;
        if (w <= 0 || h <= 0) return composite;

        if (_depthStrength <= 0 || _depthMaxRadius <= 0) return composite;

        int stride = w * 4;
        var pixels = new byte[stride * h];
        composite.CopyPixels(pixels, stride, 0);

        double radius = Math.Max(1, _depthMaxRadius * Math.Clamp(renderScale, 0.05, 1.0));
        if (!GpuDepthBlur.TryApply(pixels, stride, w, h, dm, _depthFocus, _depthStrength, radius))
            return composite;

        var result = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        result.Freeze();
        return result;
    }

    private static WriteableBitmap DepthMapVisualization(DepthMap dm, int w, int h)
    {
        var gray = new byte[dm.Width * dm.Height];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)Math.Clamp(dm.Data[i] * 255f, 0, 255);
        var small = BitmapSource.Create(dm.Width, dm.Height, 96, 96, PixelFormats.Gray8, null, gray, dm.Width);
        var scaled = new TransformedBitmap(small, new ScaleTransform(w / (double)dm.Width, h / (double)dm.Height));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var wb = new WriteableBitmap(bgra);
        wb.Freeze();
        return wb;
    }

    /// <summary>表示専用オーバーレイ(保存には乗らない): 深度マップ表示、または
    /// ピント範囲ハイライト。<see cref="_depthFocusPreview"/> がある間(フォーカス選択中の
    /// カーソル位置)はその深度を基準にする。</summary>
    private WriteableBitmap ApplyDepthDisplayOverlay(WriteableBitmap composite)
    {
        if (!_depthBlurEnabled || _depthComputing || _depthMap is not { } dm) return composite;
        int w = composite.PixelWidth, h = composite.PixelHeight;
        if (w <= 0 || h <= 0) return composite;

        if (_depthShowMap) return DepthMapVisualization(dm, w, h);

        bool focusRange = _depthShowFocusRange || _colorPickTarget == ColorPickTarget.DepthFocus;
        if (!focusRange) return composite;

        return FocusRangeHighlight(composite, dm, _depthFocusPreview ?? _depthFocus, _depthStrength);
    }

    /// <summary>ピント面と「同じような深度」の画素はそのまま、外れた画素は暗くして、
    /// どこがピント内かひと目で分かるようにする。バンド幅は強さから導く(強いほど狭い)。
    /// 表示は縮小コピーで十分(オーバーレイ用)。</summary>
    private static WriteableBitmap FocusRangeHighlight(WriteableBitmap src, DepthMap dm, double focus, double strength)
    {
        const int cap = 1400;
        BitmapSource s = src;
        if (Math.Max(src.PixelWidth, src.PixelHeight) > cap)
        {
            double k = cap / (double)Math.Max(src.PixelWidth, src.PixelHeight);
            s = new TransformedBitmap(src, new ScaleTransform(k, k));
        }
        int w = s.PixelWidth, h = s.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        s.CopyPixels(px, stride, 0);

        double band = Math.Clamp(3.0 / Math.Max(strength, 1.0), 0.05, 0.5);
        for (int y = 0; y < h; y++)
        {
            double fv = h <= 1 ? 0 : (double)y / (h - 1) * (dm.Height - 1);
            int y0 = (int)fv, y1 = Math.Min(y0 + 1, dm.Height - 1);
            double ty = fv - y0;
            for (int x = 0; x < w; x++)
            {
                double fu = w <= 1 ? 0 : (double)x / (w - 1) * (dm.Width - 1);
                int x0 = (int)fu, x1 = Math.Min(x0 + 1, dm.Width - 1);
                double tx = fu - x0;
                double top = dm.Data[y0 * dm.Width + x0] + (dm.Data[y0 * dm.Width + x1] - dm.Data[y0 * dm.Width + x0]) * tx;
                double bot = dm.Data[y1 * dm.Width + x0] + (dm.Data[y1 * dm.Width + x1] - dm.Data[y1 * dm.Width + x0]) * tx;
                double d = top + (bot - top) * ty;

                if (Math.Abs(d - focus) > band)
                {
                    int i = y * stride + x * 4;
                    px[i] = (byte)(px[i] * 0.32);
                    px[i + 1] = (byte)(px[i + 1] * 0.32);
                    px[i + 2] = (byte)(px[i + 2] * 0.32);
                }
            }
        }
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>フル再合成せず、<see cref="_lastComposite"/> に表示オーバーレイだけ
    /// かけ直してプレビューへ反映する(フォーカス選択中のカーソル追従用)。</summary>
    private void RefreshDepthOverlayOnly()
    {
        if (_lastComposite is not { } clean) { ScheduleCompositeRender(); return; }
        UpdateComparisonPreview(ApplyDepthDisplayOverlay(clean), _lastBeforeComposite);
    }

    /// <summary>現在の合成結果から深度マップを計算してキャッシュする。計算用のレンダーは
    /// 深度ぼかしを一時的に切って(素の合成を推定入力にするため)行う。</summary>
    public async Task ComputeDepthMapAsync()
    {
        if (_depthComputing || _photoPixelBuffer is null) return;
        _depthComputing = true;
        RefreshDepthBlurUi();
        try
        {
            _depthEstimator ??= new DepthEstimator();
            if (!_depthEstimator.TryInitialize(out var err))
            {
                if (!DepthModel.IsAvailable())
                {
                    ShowCompositeSaveStatus("深度モデルをダウンロードしています…", success: true);
                    bool ok = await DepthModel.DownloadAsync(null);
                    if (!ok || !_depthEstimator.TryInitialize(out err))
                    {
                        ShowCompositeSaveStatus("深度モデルを取得できませんでした。", success: false);
                        return;
                    }
                }
                else
                {
                    ShowCompositeSaveStatus(err ?? "深度エンジンを初期化できませんでした。", success: false);
                    return;
                }
            }

            // _depthComputing の間 ApplyDepthBlurToComposite はぼかしを挟まないので
            // _lastComposite は素の合成になる。
            await RenderCompositePreview();
            var source = _lastComposite;

            if (source is not { PixelWidth: > 0, PixelHeight: > 0 })
            {
                ShowCompositeSaveStatus("合成結果を取得できませんでした。", success: false);
                return;
            }

            int w = source.PixelWidth, h = source.PixelHeight;
            var pixels = new byte[w * 4 * h];
            source.CopyPixels(pixels, w * 4, 0);
            bool hp = _depthHighPrecision;

            var map = await Task.Run(() => _depthEstimator!.Estimate(pixels, w, h, hp));
            if (map is null)
            {
                ShowCompositeSaveStatus("深度の推定に失敗しました。", success: false);
                return;
            }

            _depthMap = map;
            _depthMapStale = false;
            ShowCompositeSaveStatus($"深度を計算しました({(_depthEstimator!.UsingGpu ? "GPU" : "CPU")})。", success: true);
            DepthRerender(); // 合成は変えていないので stale にしない
        }
        finally
        {
            _depthComputing = false;
            RefreshDepthBlurUi();
        }
    }

    private void DisposeDepthEstimator()
    {
        _depthEstimator?.Dispose();
        _depthEstimator = null;
        _depthMap = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeDepthEstimator();
        base.OnClosed(e);
    }

    // ---- UI ----

    private bool _suppressDepthStale;
    private bool _depthShowMap;
    private bool _depthShowFocusRange;
    /// <summary>フォーカス選択モード中、カーソル下の深度(ライブのハイライト基準)。null で未ホバー。</summary>
    private double? _depthFocusPreview;

    /// <summary>被写界深度カードのボタン文言・状態表示・スライダー値を同期する。</summary>
    private void RefreshDepthBlurUi()
    {
        _suppressEventsDepth++;
        DepthBlurEnableButtonText.Text = _depthBlurEnabled ? "オン" : "オフ";
        DepthBlurBody.IsEnabled = _depthBlurEnabled;
        DepthBlurBody.Opacity = _depthBlurEnabled ? 1.0 : 0.5;
        DepthComputeButton.IsEnabled = !_depthComputing && _photoPixelBuffer is not null;
        DepthHighPrecisionButtonText.Text = _depthHighPrecision ? "高精度: オン" : "高精度: オフ";
        DepthShowMapButtonText.Text = _depthShowMap ? "深度マップを表示: オン" : "深度マップを表示: オフ";
        DepthShowFocusRangeButtonText.Text = _depthShowFocusRange ? "ピント範囲を表示: オン" : "ピント範囲を表示: オフ";

        if (_depthMapStale && !_depthComputing && _depthMap is not null)
        {
            DepthStatusText.Text = "⚠ 再計算が必要(合成が変わりました)";
            DepthStatusText.Foreground = (Brush)FindResource("AccentDarkBrush");
            DepthStatusText.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            DepthStatusText.Text = _depthComputing ? "計算中…"
                : _depthMap is null ? "未計算"
                : "計算済み";
            DepthStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            DepthStatusText.FontWeight = FontWeights.Normal;
        }

        DepthFocusSlider.Value = _depthFocus * 100;
        DepthFocusBox.Text = (_depthFocus * 100).ToString("F0", CultureInfo.InvariantCulture);
        DepthStrengthSlider.Value = _depthStrength;
        DepthStrengthBox.Text = _depthStrength.ToString("F0", CultureInfo.InvariantCulture);
        DepthRadiusSlider.Value = _depthMaxRadius;
        DepthRadiusBox.Text = _depthMaxRadius.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
    }

    private void DepthBlurEnableButton_Click(object sender, RoutedEventArgs e)
    {
        _depthBlurEnabled = !_depthBlurEnabled;
        RefreshDepthBlurUi();
        if (_depthBlurEnabled && _depthMap is null && _photoPixelBuffer is not null)
        {
            _ = ComputeDepthMapAsync();
            return;
        }
        DepthRerender();
    }

    private void DepthComputeButton_Click(object sender, RoutedEventArgs e) => _ = ComputeDepthMapAsync();

    private void DepthFocusPickButton_Click(object sender, RoutedEventArgs e) => BeginColorPick(ColorPickTarget.DepthFocus);

    private void DepthHighPrecisionButton_Click(object sender, RoutedEventArgs e)
    {
        _depthHighPrecision = !_depthHighPrecision;
        if (_depthMap is not null) _depthMapStale = true; // 精度変更は再計算が要る
        RefreshDepthBlurUi();
    }

    private void DepthShowMapButton_Click(object sender, RoutedEventArgs e)
    {
        _depthShowMap = !_depthShowMap;
        if (_depthShowMap) _depthShowFocusRange = false;
        RefreshDepthBlurUi();
        DepthRerender();
    }

    private void DepthShowFocusRangeButton_Click(object sender, RoutedEventArgs e)
    {
        _depthShowFocusRange = !_depthShowFocusRange;
        if (_depthShowFocusRange) _depthShowMap = false;
        RefreshDepthBlurUi();
        RefreshDepthOverlayOnly();
    }

    private void DepthFocusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DepthFocusSlider.Value);
        _suppressEventsDepth++;
        DepthFocusBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        SetDepthFocus(rounded / 100.0);
    }

    private void DepthFocusBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DepthFocusBox.Text, out var v)) return;
        SetDepthFocus(Math.Clamp(v, 0, 100) / 100.0);
    }

    private void SetDepthFocus(double focus01)
    {
        if (Math.Abs(focus01 - _depthFocus) < 1e-6) return;
        _depthFocus = Math.Clamp(focus01, 0, 1);
        DepthRerender();
    }

    private void DepthStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DepthStrengthSlider.Value);
        _suppressEventsDepth++;
        DepthStrengthBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _depthStrength) return;
        _depthStrength = rounded;
        DepthRerender();
    }

    private void DepthStrengthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DepthStrengthBox.Text, out var v) || v < 0) return;
        _depthStrength = v;
        _suppressEventsDepth++;
        DepthStrengthSlider.Value = Math.Clamp(v, 0, 100);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        DepthRerender();
    }

    private void DepthRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        double rounded = Math.Round(DepthRadiusSlider.Value);
        _suppressEventsDepth++;
        DepthRadiusBox.Text = rounded.ToString("F0", CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        if (rounded == _depthMaxRadius) return;
        _depthMaxRadius = rounded;
        DepthRerender();
    }

    private void DepthRadiusBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!TryParse(DepthRadiusBox.Text, out var v) || v < 1) return;
        _depthMaxRadius = v;
        _suppressEventsDepth++;
        DepthRadiusSlider.Value = Math.Clamp(v, 2, 30);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        DepthRerender();
    }

    /// <summary>被写界深度パラメータだけの変更で再レンダー。合成自体は変わらないので
    /// 深度マップを「古い」にはしない。</summary>
    private void DepthRerender()
    {
        _suppressDepthStale = true;
        try { ScheduleCompositeRender(); }
        finally { _suppressDepthStale = false; }
    }
}
