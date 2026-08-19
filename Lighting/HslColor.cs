using System;
using System.Windows.Media;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// HSL to RGB conversion for picking lighting colors by hue while holding
/// saturation/lightness constant.
/// </summary>
public static class HslColor
{
    /// <param name="hueDegrees">Hue in degrees, [0, 360).</param>
    /// <param name="saturation">Saturation, [0, 1].</param>
    /// <param name="lightness">Lightness, [0, 1].</param>
    public static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hueDegrees / 60.0 % 2 - 1));
        var m = lightness - c / 2;

        var (r, g, b) = hueDegrees switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
