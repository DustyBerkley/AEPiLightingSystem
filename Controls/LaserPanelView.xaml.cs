using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Controls;

/// <summary>
/// Draws the laser as <see cref="LaserFixture.GroupCount"/> horizontal
/// lines - one per lens - kept honest to what the hardware can actually do:
/// each lens draws exactly one line-shaped pattern at a time (confirmed via
/// direct hardware testing), with its own position, width, and color, not a
/// smooth 2D gradient or independently addressable sub-beams. An earlier
/// version drew <see cref="LaserFixture.BeamCount"/> individual beams
/// radiating from a vanishing point, styled as a perspective room - that
/// never had a real DMX equivalent (a lens can't light 35 independent
/// beams), so this instead mirrors <see cref="LaserFixture.Groups"/>
/// directly: each group's <see cref="LaserGroup.CenterFraction"/> maps to a
/// horizontal screen position, <see cref="LaserGroup.WidthFraction"/> to
/// the line's length, and <see cref="LaserGroup.TiltRadians"/> to a small
/// vertical offset.
///
/// Redrawn every frame while groups are changing, without rebuilding the
/// line shapes themselves - only each line's endpoints and brush color are
/// touched.
/// </summary>
public partial class LaserPanelView : UserControl
{
    // How far LaserGroup.TiltRadians (a small wobble, not a full rotation
    // here) shifts a line vertically, in pixels per radian - purely a
    // display-scale choice, not tied to any real angle.
    private const double TiltPixelsPerRadian = 400.0;

    private readonly List<(Line Shape, SolidColorBrush Brush)> _lines = new();

    public static readonly DependencyProperty LaserProperty = DependencyProperty.Register(
        nameof(Laser),
        typeof(LaserFixture),
        typeof(LaserPanelView),
        new PropertyMetadata(null, OnLaserChanged));

    public LaserFixture? Laser
    {
        get => (LaserFixture?)GetValue(LaserProperty);
        set => SetValue(LaserProperty, value);
    }

    public LaserPanelView()
    {
        InitializeComponent();
    }

    private static void OnLaserChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LaserPanelView)d;

        if (e.OldValue is LaserFixture oldLaser)
            oldLaser.GroupsChanged -= view.OnGroupsChanged;

        if (e.NewValue is LaserFixture newLaser)
        {
            newLaser.GroupsChanged += view.OnGroupsChanged;
            view.BuildGeometry();
        }
    }

    private void OnGroupsChanged(object? sender, System.EventArgs e) => UpdateLines();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => BuildGeometry();

    private void BuildGeometry()
    {
        LineCanvas.Children.Clear();
        _lines.Clear();

        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        for (var i = 0; i < LaserFixture.GroupCount; i++)
        {
            var brush = new SolidColorBrush(Colors.Black);
            var line = new Line
            {
                Stroke = brush,
                StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            LineCanvas.Children.Add(line);
            _lines.Add((line, brush));
        }

        UpdateLines();
    }

    private void UpdateLines()
    {
        var groups = Laser?.Groups;
        var width = ActualWidth;
        var height = ActualHeight;
        var centerY = height / 2;

        for (var i = 0; i < _lines.Count; i++)
        {
            var (line, brush) = _lines[i];

            if (groups is null || i >= groups.Length)
            {
                brush.Color = Colors.Black;
                continue;
            }

            var group = groups[i];
            brush.Color = group.Color;

            var centerX = width / 2 + group.CenterFraction * (width / 2);
            var halfLength = group.WidthFraction * width / 2;
            var y = centerY + group.TiltRadians * TiltPixelsPerRadian;

            line.X1 = centerX - halfLength;
            line.X2 = centerX + halfLength;
            line.Y1 = y;
            line.Y2 = y;
        }
    }
}
