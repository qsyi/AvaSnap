using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AvaSnap.Converters;

/// <summary>スライダーの色付きフィルバーの Margin を計算する。Minimum からではなく
/// 「0 の位置」から thumb へ向かって伸びるようにする。Minimum が既に 0 のスライダー
/// (境界ぼかし・不透明度)では 0 の位置が左端なので、普通の「左から伸びる」バーに
/// 自然に退化する。入力: Value / Minimum / Maximum / トラックの描画幅。</summary>
public sealed class SliderZeroFillConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4) return new Thickness(0);
        if (values[0] is not double value || values[1] is not double min ||
            values[2] is not double max || values[3] is not double width)
        {
            return new Thickness(0);
        }
        if (width <= 0 || max <= min) return new Thickness(0);

        double zeroFrac = Math.Clamp((0 - min) / (max - min), 0, 1);
        double valueFrac = Math.Clamp((value - min) / (max - min), 0, 1);
        double left = Math.Min(zeroFrac, valueFrac) * width;
        double right = width - Math.Max(zeroFrac, valueFrac) * width;
        return new Thickness(left, 0, right, 0);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
