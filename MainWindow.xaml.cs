using System.Windows.Media;
using AEPiLightingSystem.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AEPiLightingSystem;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private static readonly Color BackgroundColor = Color.FromRgb(0x08, 0x08, 0x08);

    public LightingRig Rig { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(this);
        SystemThemeWatcher.Watch(this);

        // The theme manager/watcher reassert their own background brush around
        // load time, so pin ours after that settles instead of only in the ctor.
        Background = new SolidColorBrush(BackgroundColor);
        Loaded += (_, _) => Background = new SolidColorBrush(BackgroundColor);

        DataContext = Rig;

        SeedDemoState();
    }

    /// <summary>
    /// Populates the rig with placeholder colors so the visualizer isn't blank
    /// before real audio-reactive control is wired up.
    /// </summary>
    private void SeedDemoState()
    {
        Rig.TopLeftPar.Color = Colors.OrangeRed;
        Rig.TopRightPar.Color = Colors.DeepSkyBlue;
        Rig.BottomLeftPar.Color = Colors.MediumPurple;
        Rig.BottomRightPar.Color = Colors.Gold;

        foreach (var strip in Rig.LedStrips)
        {
            var hue = (strip.Id - 1) * 60.0;
            for (var i = 0; i < strip.PixelCount; i++)
            {
                var lightness = 0.15 + 0.55 * i / strip.PixelCount;
                strip.Pixels[i] = HsvToColor(hue, 0.85, lightness);
            }

            strip.RaisePixelsChanged();
        }
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - System.Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}
