using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Services;

/// <summary>コード生成の「枠線」(写真の縁取り)を <see cref="ImageAdjustment.PixelBuffer"/>
/// にラスタライズする。画像ファイルの代わりにこのバッファをデカール
/// (<c>DecalLayer</c>、<c>IsFrame == true</c>)へ流し込むことで、画像を用意しなくても
/// 枠線をアバターの前後に合成できる。<see cref="ImageAdjustment.CreateSolidColor"/> /
/// <see cref="ImageAdjustment.CreateLinearGradient"/>(背景なしキャンバス用)と
/// 同じ「コード側で PixelBuffer を作る」系のヘルパー。
///
/// デカールのサイズ/色/太さ変更のたびに呼び直して <c>DecalLayer.Pixels</c> を
/// 差し替える運用(BlendDecalOnto 側は最近傍拡大なので、細い枠ほど元バッファ
/// 解像度の粗さが出る -- 変更時に出力に近い解像度で焼き直すのが前提)。
/// <see cref="RenderTargetBitmap"/> を使うので必ず UI スレッドから呼ぶこと。</summary>
public static class ShapeRasterizer
{
    /// <summary>焼き上げるバッファの1辺の上限。デカールを 4K キャンバス全面へ
    /// 伸ばしても、ここで頭打ちにして拡大は BlendDecalOnto 任せにする
    /// (枠はわずかに甘くなるが実用範囲。出力解像度での焼き直しは
    /// ControlPanelWindow.EnsureFrameRenderBuffer 側)。</summary>
    private const int MaxRasterDimension = 2400;

    /// <summary>デカールの表示サイズ(写真ピクセル空間の幅/高さ)から、
    /// 焼き上げるバッファの解像度を決める。長辺を <see cref="MaxRasterDimension"/>
    /// で頭打ちにしつつ、それ未満なら等倍で焼く。</summary>
    public static (int Width, int Height) RasterSizeFor(double displayWidth, double displayHeight)
    {
        double longSide = Math.Max(displayWidth, displayHeight);
        double scale = longSide > MaxRasterDimension ? MaxRasterDimension / longSide : 1.0;
        int w = Math.Max(2, (int)Math.Round(displayWidth * scale));
        int h = Math.Max(2, (int)Math.Round(displayHeight * scale));
        return (w, h);
    }

    /// <summary>(<paramref name="width"/> x <paramref name="height"/>) の BGRA32
    /// バッファへ枠線を描く。線幅は短辺に対する <paramref name="strokePercent"/>
    /// パーセント(拡縮しても見た目の細さが一定になるよう、レンダー解像度が
    /// 確定したこの時点で実ピクセルへ変換する)。</summary>
    public static ImageAdjustment.PixelBuffer RasterizeFrame(int width, int height, Color color, double strokePercent)
    {
        width = Math.Clamp(width, 2, 4096);
        height = Math.Clamp(height, 2, 4096);

        var fill = new SolidColorBrush(color);
        fill.Freeze();
        double shorter = Math.Min(width, height);
        double stroke = Math.Max(1.0, shorter * strokePercent / 100.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var pen = new Pen(fill, stroke);
            pen.Freeze();
            double inset = stroke / 2.0; // 線の外縁をバッファの端(0 と width/height)にそろえる
            dc.DrawRectangle(null, pen, new Rect(inset, inset, width - stroke, height - stroke));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        // PrepareBuffer が Pbgra32 -> Bgra32 変換でアルファの乗算を解除するので、
        // BlendDecalOnto が期待するストレートアルファのバッファになる。
        return ImageAdjustment.PrepareBuffer(rtb);
    }
}
