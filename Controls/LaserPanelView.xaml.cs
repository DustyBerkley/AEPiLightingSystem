using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Controls;

/// <summary>
/// A perspective "laser fan" mockup, kept honest to what the hardware can
/// actually do: every beam is a straight line originating from the
/// projector point, uniformly colored along its own length - a real laser
/// can turn individual beams on/off/dim, re-color them, and pan them up and
/// down, but it can't paint a smooth 2D gradient, vary brightness partway
/// down a beam, or make light fall short of its actual reach, so none of
/// that happens here either. Each beam's color comes from the bound
/// <see cref="Laser"/>'s <see cref="LaserFixture.BeamColors"/>; its vertical
/// pan comes from <see cref="LaserFixture.BeamTiltRadians"/>, applied as a
/// rotation of the beam's full-length vector around the projector point (see
/// <see cref="Rotate"/>) so the beam's length - the distance from the
/// projector to wherever it lands - never changes, only the angle it's
/// pointed at. With enough beams lit at once they read as a cohesive fan,
/// and the lava-lamp-style breaks are real gaps between beams that are off
/// rather than a shape with dark patches painted into it.
///
/// A dim static reference grid (room edges plus labeled distance bands) is
/// drawn behind the beams so the fan reads as an actual room in perspective
/// rather than a flat 2D shape - equal real-world distances are spaced wide
/// near the front (bottom, closest to the crowd) and compress toward the
/// vanishing point (top, where the laser is mounted), the same way a floor
/// grid recedes in a photo (see <see cref="DepthT"/>). The distances
/// themselves are an illustrative assumption (the app has no real venue
/// measurements to draw from), not a calibrated reading, and are purely for
/// spatial reference - beams don't use this mapping themselves since their
/// reach never varies.
///
/// Redrawn every frame while colors/angles are changing, without rebuilding
/// the line geometry - only each beam's endpoint and brush color are
/// touched; the reference grid is static and built once per resize.
/// </summary>
public partial class LaserPanelView : UserControl
{
    // Illustrative assumption for how far the laser's beams reach - there's
    // no real venue measurement to draw from, so this is just a reasonable
    // round number to give the reference grid's distance labels meaning.
    private const double AssumedMaxReachFeet = 40.0;
    private static readonly double[] DistanceBandFeet = { 10, 20, 30, 40 };

    // Controls how aggressively depth compresses toward the vanishing
    // point - smaller values bunch farther distances closer to the
    // vanishing point (more dramatic perspective), larger values spread
    // them more evenly. Only used for the static reference grid.
    private const double PerspectiveK = 0.18;

    private readonly List<(Line Shape, SolidColorBrush Brush, Point VanishingPoint, Point FullBottom)> _beams = new();

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
            oldLaser.BeamsChanged -= view.OnBeamsChanged;

        if (e.NewValue is LaserFixture newLaser)
        {
            newLaser.BeamsChanged += view.OnBeamsChanged;
            view.UpdateBeams();
        }
    }

    private void OnBeamsChanged(object? sender, EventArgs e) => UpdateBeams();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => BuildGeometry();

    // Maps a [0, 1] fraction of the room (0 = at the laser/vanishing point,
    // 1 = the front edge closest to the crowd) to a [0, 1] screen-space
    // depth: equal steps near 1 (the front) cover more screen space than
    // equal steps near 0 (near the vanishing point), which is what makes a
    // flat fan read as a receding floor instead of a flat wedge. Only used
    // for the static reference grid - beam endpoints use Rotate instead,
    // since their reach never varies.
    private static double DepthT(double roomFraction)
    {
        var distanceFromFront = 1.0 - roomFraction;
        return PerspectiveK / (PerspectiveK + distanceFromFront);
    }

    // Rotates point around origin by angleRadians, preserving the distance
    // between them exactly - this is what lets a beam's on-screen landing
    // spot move up/down while its actual length stays fixed.
    private static Point Rotate(Point origin, Point point, double angleRadians)
    {
        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;
        var cos = Math.Cos(angleRadians);
        var sin = Math.Sin(angleRadians);

        return new Point(
            origin.X + dx * cos - dy * sin,
            origin.Y + dx * sin + dy * cos);
    }

    private void BuildGeometry()
    {
        LineCanvas.Children.Clear();
        _beams.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var vanishingPoint = new Point(width / 2, 0);
        var leftEdge = new Point(0, height);
        var rightEdge = new Point(width, height);

        BuildReferenceGrid(vanishingPoint, leftEdge, rightEdge, width);

        var beamCount = LaserFixture.BeamCount;
        for (var i = 0; i < beamCount; i++)
        {
            var x = beamCount == 1 ? width / 2 : width * i / (beamCount - 1);
            var bottom = new Point(x, height);

            var brush = new SolidColorBrush(Colors.Black);
            var line = new Line
            {
                X1 = vanishingPoint.X,
                Y1 = vanishingPoint.Y,
                X2 = bottom.X,
                Y2 = bottom.Y,
                Stroke = brush,
                StrokeThickness = 1.5,
            };
            LineCanvas.Children.Add(line);
            _beams.Add((line, brush, vanishingPoint, bottom));
        }

        UpdateBeams();
    }

    // Static, dim reference geometry - the room's outer edges plus a few
    // labeled distance bands - drawn once behind the beams so the panel
    // still reads as a room in perspective even when the laser is off.
    private void BuildReferenceGrid(Point vanishingPoint, Point leftEdge, Point rightEdge, double width)
    {
        var edgeBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        LineCanvas.Children.Add(new Line
        {
            X1 = vanishingPoint.X,
            Y1 = vanishingPoint.Y,
            X2 = leftEdge.X,
            Y2 = leftEdge.Y,
            Stroke = edgeBrush,
            StrokeThickness = 1.0,
        });
        LineCanvas.Children.Add(new Line
        {
            X1 = vanishingPoint.X,
            Y1 = vanishingPoint.Y,
            X2 = rightEdge.X,
            Y2 = rightEdge.Y,
            Stroke = edgeBrush,
            StrokeThickness = 1.0,
        });

        var bandBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF));

        foreach (var feet in DistanceBandFeet)
        {
            var t = DepthT(feet / AssumedMaxReachFeet);
            var y = vanishingPoint.Y + (leftEdge.Y - vanishingPoint.Y) * t;
            var xLeft = vanishingPoint.X + (leftEdge.X - vanishingPoint.X) * t;
            var xRight = vanishingPoint.X + (rightEdge.X - vanishingPoint.X) * t;

            LineCanvas.Children.Add(new Line
            {
                X1 = xLeft,
                Y1 = y,
                X2 = xRight,
                Y2 = y,
                Stroke = bandBrush,
                StrokeThickness = 1.0,
            });

            var label = new TextBlock
            {
                Text = $"{feet:0} ft",
                FontSize = 9,
                Foreground = labelBrush,
            };
            Canvas.SetLeft(label, Math.Max(2, xLeft + 3));
            Canvas.SetTop(label, y - 12);
            LineCanvas.Children.Add(label);
        }
    }

    private void UpdateBeams()
    {
        var beamColors = Laser?.BeamColors;
        var beamTilts = Laser?.BeamTiltRadians;

        for (var i = 0; i < _beams.Count; i++)
        {
            var (line, brush, vanishingPoint, fullBottom) = _beams[i];

            brush.Color = beamColors != null && i < beamColors.Length ? beamColors[i] : Colors.Black;

            var tilt = beamTilts != null && i < beamTilts.Length ? beamTilts[i] : 0.0;
            var endpoint = Rotate(vanishingPoint, fullBottom, tilt);
            line.X2 = endpoint.X;
            line.Y2 = endpoint.Y;
        }
    }
}
