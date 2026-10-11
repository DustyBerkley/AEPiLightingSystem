using System;
using System.Windows.Media;

namespace AEPiLightingSystem.Models;

/// <summary>
/// One ceiling-mounted "12LED 3IN1 Wash Light Bar" (Laluce Natz), run in its
/// 75-channel DMX mode. Three independent kinds of emitter along its length,
/// matching the fixture's own 75CH operating schema:
/// <list type="bullet">
/// <item>12 large amber ("yellow") bulbs - the strobe-capable ones (Y1-Y12).</item>
/// <item>13 small white LEDs sitting between/around those bulbs (W1-W13).</item>
/// <item>16 RGB pixel segments along the bar (RGB1-RGB16).</item>
/// </list>
/// The bars run front-to-back on the ceiling. Index 0 of every array is the
/// fixture's own "1", assumed to be mounted at the front end.
/// </summary>
public class CeilingLightBar
{
    public const int AmberBulbCount = 12;
    public const int WhiteLedCount = 13;
    public const int RgbPixelCount = 16;

    public int Id { get; }

    public string Label { get; }

    /// <summary>Which side of the room the bar hangs on - effects that move across the room use this as its cross-room position.</summary>
    public bool IsLeftSide { get; }

    /// <summary>
    /// How far back the bar's front end starts, in bar-lengths from the
    /// front-most bar's front end - lets effects place every bar in one
    /// shared front-to-back coordinate. Matches the ceiling plan drawn in
    /// MainWindow.xaml.
    /// </summary>
    public double DepthOffset { get; }

    /// <summary>The 16 RGB segments.</summary>
    public Color[] Pixels { get; } = new Color[RgbPixelCount];

    /// <summary>
    /// Scales the RGB segments' actual output (DMX and on-screen) without
    /// touching <see cref="Pixels"/> itself - effects that read Pixels back
    /// as their own state (e.g. a flash decaying in place) carry on
    /// unaffected underneath. 1.0 = no dimming.
    /// </summary>
    public double RgbDimFactor { get; set; } = 1.0;

    /// <summary>The 12 amber bulbs, 0-255 each.</summary>
    public byte[] AmberLevels { get; } = new byte[AmberBulbCount];

    /// <summary>The 13 white LEDs, 0-255 each.</summary>
    public byte[] WhiteLevels { get; } = new byte[WhiteLedCount];

    /// <summary>
    /// Raised after any of the arrays above have been updated, so views and
    /// the DMX binder can pick up the new values.
    /// </summary>
    public event EventHandler? Changed;

    public CeilingLightBar(int id, string label, bool isLeftSide, double depthOffset)
    {
        Id = id;
        Label = label;
        IsLeftSide = isLeftSide;
        DepthOffset = depthOffset;
        Array.Fill(Pixels, Colors.Black);
    }

    public void FillPixels(Color color)
    {
        Array.Fill(Pixels, color);
        RaiseChanged();
    }

    /// <summary>Call after directly mutating any of the arrays in bulk.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
