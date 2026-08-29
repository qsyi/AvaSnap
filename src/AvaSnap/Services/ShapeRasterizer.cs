using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Services;

/// <summary>図形デカールの種類。現状は写真の縁取り用の枠線のみ
/// (<c>DecalLayer.ShapeKind</c> が null なら画像デカール)。</summary>
public enum ShapeKind
{
    RectangleFrame,
}

/// <summary>コード生成の図形を <see cref="ImageAdjustment.PixelBuffer"/> に
/// ラスタライズする。画像ファイルの代わりにこのバッファをデカール
/// (<c>DecalLayer</c>)へ流し込むことで、画像を用意しなくても枠線を
/// アバターの前後に合成できる。<see cref="ImageAdjustment.CreateSolidColor"/> /
/// <see cref="ImageAdjustment.CreateLinearGradient"/>(背景なしキャンバス用)と
/// 同じ「コード側で PixelBuffer を作る」系のヘルパー。
///
/// デカールのサイズ変更や色/太さ変更のたびに呼び直して <c>DecalLayer.Pixels</c>
/// を差し替える運用(BlendDecalOnto 側は最近傍拡大なので、細い枠ほど元バッファ
/// 解像度の粗さが出る -- 変更時に出力に近い解像度で焼き直すのが前提)。
/// <see cref="RenderTargetBitmap"/> を使うので必ず UI スレッドから呼ぶこと。</summary>
public static class ShapeRasterizer
{
    /// <summary>焼き上げるバッファの1辺の上限。デカールを 4K キャンバス全面へ
    /// 伸ばしても、ここで頭打ちにして拡大は BlendDecalOnto 任せにする
    /// (塗りつぶし図形は拡大しても劣化しない。枠/線はわずかに甘くなるが
    /// 実用範囲)。</summary>
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

    /// <summary><paramref name="kind"/> の図形を (<paramref name="width"/> x
    /// <paramref name="height"/>) の BGRA32 バッファへ描く。枠線の線幅は
    /// 短辺に対する <paramref name="strokePercent"/> パーセント(選択A:
    /// 拡縮しても見た目の細さが一定になるよう、レンダー解像度が確定した
    /// この時点で実ピクセルへ変換する)。</summary>
    public static ImageAdjustment.PixelBuffer Rasterize(ShapeKind kind, int width, int height, Color color, double strokePercent)
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
            switch (kind)
            {
                case ShapeKind.RectangleFrame:
                {
                    var pen = new Pen(fill, stroke);
                    pen.Freeze();
                    double inset = stroke / 2.0; // 線の外縁をバッファの端(0 と width/height)にそろえる
                    dc.DrawRectangle(null, pen, new Rect(inset, inset, width - stroke, height - stroke));
                    break;
                }
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        // PrepareBuffer が Pbgra32 -> Bgra32 変換でアルファの乗算を解除するので、
        // BlendDecalOnto が期待するストレートアルファのバッファになる。
        return ImageAdjustment.PrepareBuffer(rtb);
    }
}
