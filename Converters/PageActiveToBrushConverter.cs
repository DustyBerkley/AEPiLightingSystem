using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Converters;

/// <summary>
/// Colors a fader's page-name label in the active-effects strip: bright
/// white if that fader's own page is the one currently open, a darker gray
/// otherwise. Bindings are [0] = the fader's own Page, [1] =
/// LightingRig.SelectedFaderPage (bound via an AncestorType=Window
/// RelativeSource back up to the window's own DataContext, since the
/// item's DataContext inside the ItemsControl is the MidiFader itself).
/// </summary>
public class PageActiveToBrushConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush ActivePageBrush = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush InactivePageBrush = new(Color.FromArgb(0xAA, 0x88, 0x88, 0x88));

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [FaderPage faderPage, FaderPage selectedPage])
            return faderPage == selectedPage ? ActivePageBrush : InactivePageBrush;

        return InactivePageBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
