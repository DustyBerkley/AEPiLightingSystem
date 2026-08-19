using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AEPiLightingSystem.Converters;

public class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
            return Brushes.Black;

        if (parameter is string opacityText && double.TryParse(opacityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            color.A = (byte)(Math.Clamp(opacity, 0, 1) * 255);

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color : Colors.Black;
}
