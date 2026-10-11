using System;
using System.Globalization;
using System.Windows.Data;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Converters;

/// <summary>
/// Dims an entire active-effects box (not just its page-name label, see
/// PageActiveToBrushConverter) when its page isn't the one currently open.
/// Bindings are [0] = the fader's own Page, [1] =
/// LightingRig.SelectedFaderPage.
/// </summary>
public class PageActiveToOpacityConverter : IMultiValueConverter
{
    private const double ActivePageOpacity = 1.0;
    private const double InactivePageOpacity = 0.5;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [FaderPage faderPage, FaderPage selectedPage])
            return faderPage == selectedPage ? ActivePageOpacity : InactivePageOpacity;

        return InactivePageOpacity;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
