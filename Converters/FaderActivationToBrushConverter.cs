using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AEPiLightingSystem.Converters;

/// <summary>
/// A fader box's "is this active" color: the same translucent gray as its
/// own border outline while Value is 0, brightening to a near-white once
/// the fader is activated at all (any Value above 0) - a binary switch, not
/// a gradual brightness ramp.
/// </summary>
public class FaderActivationToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InactiveBrush = new(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d > 0 ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
