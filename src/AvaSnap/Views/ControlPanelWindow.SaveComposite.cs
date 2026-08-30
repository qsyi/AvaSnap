using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AvaSnap.Views;

// ---- 合成結果の保存。通常保存 + 「分割 なし/2/3/4」セグメントによる横N等分保存
//      (継ぎ目に「分割時の余白」px を空ける)、プレビューの区切り線シミュレート、
//      保存後の「フォルダを開く」ボタン、保存ステータス通知(自動消去タイマー)。 ----
public partial class ControlPanelWindow
{
    /// <summary>フル解像度(VRChat スクショサイズ)の合成を PNG エンコードして書き出す ──
    /// 再合成と同程度に遅く、UI スレッドで同期実行すると保存中ずっと窓が固まる。
    /// _lastComposite は常に CompositeOverlayOntoPhoto/CropToAspect の凍結済み出力なので
    /// UI スレッド外でエンコードして安全。</summary>
    private async void SaveCompositeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastComposite is null) return;
        int splits = Math.Clamp(_splitCount, 1, MaxSplitCount);

        // 名前かぶり防止に日時を付ける(保存ダイアログの初期名。ユーザーは変更可)。
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var defaultName = _photoPath is not null
            ? Path.GetFileNameWithoutExtension(_photoPath) + $"_avasnap_{ts}.png"
            : $"avasnap_{ts}.png";
        var dialog = new SaveFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            FileName = defaultName,
            InitialDirectory = _photoPath is not null ? Path.GetDirectoryName(_photoPath) ?? "" : "",
        };
        if (dialog.ShowDialog() != true) return;

        var composite = _lastComposite;
        string path = dialog.FileName;
        SaveCompositeButton.IsEnabled = false;
        try
        {
            if (splits <= 1)
            {
                bool saved = await Task.Run(() => TrySavePng(composite, path));
                if (saved) _lastSavedCompositePath = path;
                ShowCompositeSaveStatus(
                    saved ? "保存: " + Ellipsize(Path.GetFileName(path), 16) : "保存に失敗しました。",
                    success: saved);
                return;
            }

            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            var (ok, count) = await SaveCompositeSplitAsync(composite, dir, stem, splits, _splitGapPx);
            if (ok) _lastSavedCompositePath = Path.Combine(dir, $"{stem}_1.png");
            ShowCompositeSaveStatus(ok ? $"{count}枚保存しました" : "保存に失敗しました。", success: ok);
        }
        finally
        {
            SaveCompositeButton.IsEnabled = true;
        }
    }

    /// <summary>合成結果を横に <paramref name="splits"/> 等分し、各継ぎ目で合計
    /// <paramref name="gapPx"/> px 落として <c>dir\stem_1.png .. _N.png</c> へ
    /// 書き出す。ピクセルは UI スレッドで一度だけ取り出し、切り出し+エンコードは
    /// バックグラウンド。両端の外側は落とさない。</summary>
    private async Task<(bool ok, int count)> SaveCompositeSplitAsync(
        BitmapSource composite, string dir, string stem, int splits, int gapPx)
    {
        int w = composite.PixelWidth, h = composite.PixelHeight, stride = w * 4;
        var buf = new byte[stride * h];
        composite.CopyPixels(buf, stride, 0);
        var fmt = composite.Format;
        double dpiX = composite.DpiX, dpiY = composite.DpiY;
        int gap = Math.Clamp(gapPx, 0, Math.Max(0, w / splits - 2));

        return await Task.Run(() =>
        {
            int done = 0;
            for (int k = 0; k < splits; k++)
            {
                int nat0 = (int)((long)w * k / splits);
                int nat1 = (int)((long)w * (k + 1) / splits);
                int x0 = nat0 + (k > 0 ? gap - gap / 2 : 0);   // 左の継ぎ目ぶん内側へ
                int x1 = nat1 - (k < splits - 1 ? gap / 2 : 0); // 右の継ぎ目ぶん内側へ
                int cw = x1 - x0;
                if (cw <= 0) continue;
                var sub = new byte[cw * 4 * h];
                for (int y = 0; y < h; y++)
                    Array.Copy(buf, y * stride + x0 * 4, sub, y * cw * 4, cw * 4);
                var frame = BitmapSource.Create(cw, h, dpiX, dpiY, fmt, null, sub, cw * 4);
                if (!TrySavePng(frame, Path.Combine(dir, $"{stem}_{k + 1}.png"))) return (false, done);
                done++;
            }
            return (true, done);
        });
    }

    private static bool TrySavePng(BitmapSource src, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>長い文字列を中央省略("aaaa…zzz.png")して通知を短く保つ。</summary>
    private static string Ellipsize(string s, int max)
    {
        if (s.Length <= max) return s;
        int keep = Math.Max(1, (max - 1) / 2);
        return string.Concat(s.AsSpan(0, keep), "…", s.AsSpan(s.Length - keep));
    }

    // ---- 分割セグメント + 分割時の余白 ----

    private const int MaxSplitCount = 4;
    private const int MaxSplitGapPx = 200;
    private int _splitCount = 1;
    private int _splitGapPx = 12; // 各継ぎ目で落とす合計px(左右 gap/2 ずつ)

    private void SplitCountRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var n))
            _splitCount = Math.Clamp(n, 1, MaxSplitCount);
        RefreshSplitGapRowEnabled();
        UpdateSplitGuides();
    }

    private void SplitGapBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _splitGapPx = int.TryParse(SplitGapBox.Text, out var g) ? Math.Clamp(g, 0, MaxSplitGapPx) : 0;
        UpdateSplitGuides();
    }

    private void RefreshSplitGapRowEnabled()
    {
        bool on = _splitCount >= 2;
        SplitGapLabel.IsEnabled = SplitGapBox.IsEnabled = SplitGapUnit.IsEnabled = on;
    }

    /// <summary>プレビューに分割の区切りを描く。通常はプレビュー全体(=保存画像)を
    /// N 等分。切り抜きモード中はプレビューが未切り抜きなので、実際に保存される
    /// 切り抜き範囲の中でシミュレートする。「分割時の余白」ぶんの帯も重ねる。</summary>
    private void UpdateSplitGuides()
    {
        SplitGuideLayer.Children.Clear();
        if (_splitCount < 2 || _lastComposite is null
            || double.IsNaN(PreviewBorder.Width) || PreviewBorder.Width <= 0)
            return;

        double x0 = 0, y0 = 0, regionW = PreviewBorder.Width, regionH = PreviewBorder.Height;
        double outputW = _lastComposite.PixelWidth; // regionW が表す出力ピクセル幅
        if (_isCropModeActive && _photoPixelBuffer is { } photo)
        {
            var crop = GetCanvasCropRect(photo.Width, photo.Height);
            double scale = PreviewBorder.Width / photo.Width;
            x0 = crop.Left * scale;
            y0 = crop.Top * scale;
            regionW = crop.Width * scale;
            regionH = crop.Height * scale;
            outputW = crop.Width;
        }

        // 保存時に落とす余白(px)をプレビュー座標へ。継ぎ目1本あたり合計 gap。
        double previewGap = outputW > 0
            ? Math.Clamp(_splitGapPx, 0, Math.Max(0, (int)outputW / _splitCount - 2)) * regionW / outputW
            : 0;

        // どんな画像色でも見えるよう「白い薄いスクリム + 白黒の破線(marching ants)」。
        for (int i = 1; i < _splitCount; i++)
        {
            double x = x0 + Math.Round(regionW * i / _splitCount);
            if (previewGap >= 1)
            {
                var band = new System.Windows.Shapes.Rectangle
                {
                    Width = previewGap, Height = regionH, Fill = SplitGuideScrimBrush, IsHitTestVisible = false,
                };
                Canvas.SetLeft(band, x - previewGap / 2);
                Canvas.SetTop(band, y0);
                SplitGuideLayer.Children.Add(band);
            }
            SplitGuideLayer.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = y0, Y2 = y0 + regionH,
                Stroke = SplitGuideDashBlackBrush, StrokeThickness = 1.5,
                StrokeDashArray = SplitGuideDashArray, IsHitTestVisible = false,
            });
            SplitGuideLayer.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = y0, Y2 = y0 + regionH,
                Stroke = SplitGuideDashWhiteBrush, StrokeThickness = 1.5,
                StrokeDashArray = SplitGuideDashArray, StrokeDashOffset = 3, IsHitTestVisible = false,
            });
        }
    }

    private static readonly SolidColorBrush SplitGuideScrimBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SplitGuideDashBlackBrush = Frozen(Color.FromArgb(0xC0, 0, 0, 0));
    private static readonly SolidColorBrush SplitGuideDashWhiteBrush = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    private static readonly DoubleCollection SplitGuideDashArray = FrozenDashes(3, 3);

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static DoubleCollection FrozenDashes(params double[] d) { var c = new DoubleCollection(d); c.Freeze(); return c; }

    // ---- 保存ステータス通知 + 「フォルダを開く」 ----

    /// <summary>保存結果と「背景写真の読み込み失敗」メッセージが同じ TextBlock を
    /// 共有(色分けを揃えるため)。成功時はチェックのフェードイン + 8秒で自動消去
    /// (失敗時は消さない)。「フォルダを開く」は通知が出ている間だけ表示。</summary>
    private DispatcherTimer? _compositeSaveStatusClearTimer;

    private void ShowCompositeSaveStatus(string text, bool success)
    {
        _compositeSaveStatusClearTimer?.Stop();
        CompositeSaveStatusText.Text = text;
        CompositeSaveStatusText.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "AccentDarkBrush");
        OpenSavedFolderButton.Visibility = success && _lastSavedCompositePath is not null
            ? Visibility.Visible : Visibility.Collapsed;

        if (!success)
        {
            CompositeSaveCheckmark.Visibility = Visibility.Collapsed;
            return;
        }

        CompositeSaveCheckmark.Visibility = Visibility.Visible;
        CompositeSaveCheckmark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

        _compositeSaveStatusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _compositeSaveStatusClearTimer.Tick -= CompositeSaveStatusClearTimer_Tick;
        _compositeSaveStatusClearTimer.Tick += CompositeSaveStatusClearTimer_Tick;
        _compositeSaveStatusClearTimer.Start();
    }

    private void CompositeSaveStatusClearTimer_Tick(object? sender, EventArgs e)
    {
        // 通知が消えたら「フォルダを開く」も一緒に消す(通知が出ている間だけ表示)。
        _compositeSaveStatusClearTimer!.Stop();
        ClearCompositeSaveStatus();
    }

    private void ClearCompositeSaveStatus()
    {
        _compositeSaveStatusClearTimer?.Stop();
        CompositeSaveStatusText.Text = "";
        CompositeSaveCheckmark.Visibility = Visibility.Collapsed;
        OpenSavedFolderButton.Visibility = Visibility.Collapsed;
    }

    private string? _lastSavedCompositePath;

    private void OpenSavedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedCompositePath is not { } p) return;
        try
        {
            if (File.Exists(p))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
            else if (Path.GetDirectoryName(p) is { } dir && Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // エクスプローラーが開けなくても致命的ではない。
        }
    }
}
