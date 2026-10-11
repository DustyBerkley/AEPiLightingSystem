using System;
using System.Windows;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Controls;

/// <summary>
/// Draws a <see cref="CeilingLightBar"/> laid out like the real fixture,
/// long axis vertical (the bars run front-to-back, and the ceiling plan has
/// the front of the room at the top), index 0 at the top: its 16 RGB
/// segments as a strip along both long edges, and its 12 amber bulbs
/// alternating with its 13 white LEDs down the middle (white, amber, white,
/// ..., amber, white - per the manual's 75CH schema).
/// </summary>
public sealed class LightBarView : FrameworkElement
{
    private static readonly Color AmberFullColor = Color.FromRgb(0xFF, 0xB0, 0x20);
    private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)));
    private static readonly Pen UnlitOutlinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));

    public static readonly DependencyProperty BarProperty = DependencyProperty.Register(
        nameof(Bar),
        typeof(CeilingLightBar),
        typeof(LightBarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBarChanged));

    public CeilingLightBar? Bar
    {
        get => (CeilingLightBar?)GetValue(BarProperty);
        set => SetValue(BarProperty, value);
    }

    private static void OnBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LightBarView)d;

        if (e.OldValue is CeilingLightBar oldBar)
            oldBar.Changed -= view.OnBarStateChanged;

        if (e.NewValue is CeilingLightBar newBar)
            newBar.Changed += view.OnBarStateChanged;
    }

    private void OnBarStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bar = Bar;
        var width = ActualWidth;
        var height = ActualHeight;
        if (bar is null || width <= 0 || height <= 0)
            return;

        dc.DrawRoundedRectangle(BodyBrush, null, new Rect(0, 0, width, height), 3, 3);

        // RGB segments along both long edges.
        var edgeWidth = width * 0.2;
        var segmentHeight = height / CeilingLightBar.RgbPixelCount;
        for (var i = 0; i < CeilingLightBar.RgbPixelCount; i++)
        {
            var brush = new SolidColorBrush(Scale(bar.Pixels[i], Math.Clamp(bar.RgbDimFactor, 0.0, 1.0)));
            var y = i * segmentHeight + 0.5;
            var h = segmentHeight - 1;
            dc.DrawRectangle(brush, null, new Rect(0, y, edgeWidth, h));
            dc.DrawRectangle(brush, null, new Rect(width - edgeWidth, y, edgeWidth, h));
        }

        // White, amber, white, amber, ... white - 25 slots.
        const int slotCount = CeilingLightBar.WhiteLedCount + CeilingLightBar.AmberBulbCount;
        var slotHeight = height / slotCount;
        var centerX = width / 2;
        var middleWidth = width - 2 * edgeWidth;

        for (var slot = 0; slot < slotCount; slot++)
        {
            var centerY = (slot + 0.5) * slotHeight;

            if (slot % 2 == 0)
            {
                var level = bar.WhiteLevels[slot / 2];
                var size = Math.Min(slotHeight, middleWidth) * 0.35;
                var rect = new Rect(centerX - size / 2, centerY - size / 2, size, size);
                DrawEmitter(dc, level, Colors.White, rect, isRound: false);
            }
            else
            {
                var level = bar.AmberLevels[slot / 2];
                var radius = Math.Min(slotHeight, middleWidth) * 0.42;
                var rect = new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2);
                DrawEmitter(dc, level, AmberFullColor, rect, isRound: true);
            }
        }
    }

    private static void DrawEmitter(DrawingContext dc, byte level, Color fullColor, Rect rect, bool isRound)
    {
        var lit = level > 0;
        var brush = lit ? new SolidColorBrush(Scale(fullColor, level / 255.0)) : null;
        var pen = lit ? null : UnlitOutlinePen;

        if (isRound)
            dc.DrawEllipse(brush, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
        else
            dc.DrawRectangle(brush, pen, rect);
    }

    private static Color Scale(Color color, double factor) => Color.FromRgb(
        (byte)Math.Round(color.R * factor),
        (byte)Math.Round(color.G * factor),
        (byte)Math.Round(color.B * factor));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
