using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AvaSnap.Converters;

/// <summary>Computes the Margin for a slider's colored fill bar so it grows
/// from the slider's zero point toward the thumb, rather than from Minimum
/// toward the thumb. For a slider whose Minimum is already 0 (edge blur,
/// opacity) the zero point IS the left edge, so this naturally degrades to a
/// normal "fill from the left" bar -- one converter covers both cases.
/// Inputs: Value, Minimum, Maximum, and the track's rendered width.</summary>
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
