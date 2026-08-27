using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AEPiLightingSystem.Converters;

/// <summary>
/// Converts a double into a star-sized GridLength, so 2 columns bound to a
/// value and its complement (e.g. MidiFader.Value/RemainingValue) split a
/// row proportionally and keep filling the full available width regardless
/// of the container's actual pixel size - no fixed max-width needed.
/// </summary>
public class DoubleToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Max(0.0, d) : 0.0;
        return new GridLength(fraction, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
