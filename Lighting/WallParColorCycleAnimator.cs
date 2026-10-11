using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// A "chill" ambient wash for the 4 wall par lights + wall striplight: no
/// flashes, just a continuous, smooth crossfade looping through a fixed
/// palette of colors, all lights showing the same color at any given
/// moment. Unlike <see cref="ParLightFlashAnimator"/>'s snap-then-decay
/// model, there's no trigger/decay concept here at all - once active, the
/// cycle just keeps running.
///
/// Composites with whatever else is writing to the same lights (e.g. an
/// automated flash) by taking the per-channel brighter of its own cycling
/// color and whatever's already showing, rather than overwriting it
/// outright - see ParLightFlashAnimator's own class summary for why this is
/// safe to do without either side corrupting the other's internal state.
/// </summary>
public sealed class WallParColorCycleAnimator
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xbd, 0x95, 0x28),
        Color.FromRgb(0x2f, 0x15, 0x63),
        Color.FromRgb(0x9e, 0x16, 0x21),
        Color.FromRgb(0x16, 0x5f, 0x9e),
    ];

    /// <summary>How long a full loop through every color in the palette is allowed to take - see <see cref="CycleSeconds"/>.</summary>
    public const double MinCycleSeconds = 4.0;

    /// <summary>How long a full loop through every color in the palette is allowed to take - see <see cref="CycleSeconds"/>.</summary>
    public const double MaxCycleSeconds = 60.0;

    private const double DefaultCycleSeconds = 15.0;

    private readonly IReadOnlyList<ParLight> _lights;

    private double _cycleSeconds = DefaultCycleSeconds;
    private double _time;
    private bool _isRunning;
    private DateTime _lastFrameTime;

    /// <summary>Scales the cycling color's brightness - intended to follow the driving fader's own Value.</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>
    /// How long one full loop through every color in the palette takes -
    /// intended to follow the driving fader's own knob, clamped to
    /// [<see cref="MinCycleSeconds"/>, <see cref="MaxCycleSeconds"/>].
    /// </summary>
    public double CycleSeconds
    {
        get => _cycleSeconds;
        set => _cycleSeconds = Math.Clamp(value, MinCycleSeconds, MaxCycleSeconds);
    }

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing the wall group off - SetActive is ignored
    /// outright while set, regardless of who's calling it.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public WallParColorCycleAnimator(IReadOnlyList<ParLight> lights)
    {
        _lights = lights;
    }

    /// <summary>Call whenever the fader driving this effect crosses to/from 0%.</summary>
    public void SetActive(bool active)
    {
        if (IsSuppressed)
            return;

        SetActiveCore(active);
    }

    /// <summary>Force this effect off (true) or let it respond to SetActive calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (suppressed)
            SetActiveCore(false);
    }

    private void SetActiveCore(bool active)
    {
        if (active)
        {
            EnsureRunning();
            return;
        }

        // The caller (MainWindow's per-tick UpdateComboWallColorCycleFaderEffect)
        // calls this with active=false on every single tick whenever the
        // driving fader just sits at 0%, not only on the actual
        // active-to-inactive transition - already being stopped means
        // there's nothing to turn off, so bail out before force-blacking
        // these lights again, which would otherwise stomp on whatever
        // unrelated effect (e.g. an automated flash) is using them right now.
        if (!_isRunning)
            return;

        foreach (var light in _lights)
            light.Color = Colors.Black;

        CompositionTarget.Rendering -= OnRendering;
        _isRunning = false;
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        _time += dt;

        var secondsPerColor = _cycleSeconds / Palette.Length;
        var cyclePosition = _time / secondsPerColor % Palette.Length;
        var index = (int)cyclePosition;
        var nextIndex = (index + 1) % Palette.Length;
        var t = cyclePosition - index;

        var color = Lerp(Palette[index], Palette[nextIndex], t);
        var scaled = Scale(color, Brightness);

        foreach (var light in _lights)
            light.Color = Max(light.Color, scaled);
    }

    private static Color Scale(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        byte LerpChannel(byte a, byte b) => (byte)(a + (b - a) * t);

        return Color.FromRgb(
            LerpChannel(from.R, to.R),
            LerpChannel(from.G, to.G),
            LerpChannel(from.B, to.B));
    }

    private static Color Max(Color a, Color b) =>
        Color.FromRgb(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));
}
