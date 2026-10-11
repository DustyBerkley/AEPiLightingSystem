using System.Windows;
using System.Windows.Controls;

namespace AEPiLightingSystem.Controls;

/// <summary>
/// One fader's box - icon, beat-interval label, invalidation indicator, and
/// activation bar. Used both for the main per-page fader row (at its
/// defaults) and for the smaller copies in the active-effects strip, which
/// shrink/shift the icon down (so it clears that copy's own page-name
/// label overlaid on top of it, see MainWindow.xaml) and dim the whole box
/// when its page isn't the one currently selected.
/// </summary>
public partial class FaderBoxView : UserControl
{
    public static readonly DependencyProperty IconScaleProperty = DependencyProperty.Register(
        nameof(IconScale), typeof(double), typeof(FaderBoxView), new PropertyMetadata(1.0));

    public static readonly DependencyProperty IconVerticalOffsetProperty = DependencyProperty.Register(
        nameof(IconVerticalOffset), typeof(double), typeof(FaderBoxView), new PropertyMetadata(0.0));

    public static readonly DependencyProperty DimOpacityProperty = DependencyProperty.Register(
        nameof(DimOpacity), typeof(double), typeof(FaderBoxView), new PropertyMetadata(1.0));

    public double IconScale
    {
        get => (double)GetValue(IconScaleProperty);
        set => SetValue(IconScaleProperty, value);
    }

    public double IconVerticalOffset
    {
        get => (double)GetValue(IconVerticalOffsetProperty);
        set => SetValue(IconVerticalOffsetProperty, value);
    }

    public double DimOpacity
    {
        get => (double)GetValue(DimOpacityProperty);
        set => SetValue(DimOpacityProperty, value);
    }

    public FaderBoxView()
    {
        InitializeComponent();
    }
}
