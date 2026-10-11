using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// A "chill" sweeping chase through 7 fixed steps - the 4 wall par lights,
/// 2 floor par lights (one step lights both at once), and the wall
/// striplight - a soft traveling highlight smoothly crossfading from one
/// step to the next rather than hard-cutting, while the color itself
/// separately fades through its own 4-color palette. Both the sweep and the
/// color loop complete one full lap over the same <see cref="CycleSeconds"/>.
/// Mirrors <see cref="WallParColorCycleAnimator"/>'s "no flash, just smooth
/// continuous motion" spirit, just spatial as well as chromatic.
///
/// Since this spans both the wall/striplight group and the floor par group
/// (see MainWindow's Launchpad row-2 light-group override pads), the two
/// groups are suppressed independently - turning one off only blacks out
/// the steps that belong to it, the other keeps sweeping.
///
/// Composites with whatever else is writing to a given step's lights (e.g.
/// an automated flash) by taking the per-channel brighter of its own
/// computed color and whatever's already showing, rather than overwriting
/// it outright - see ParLightFlashAnimator's own class summary for why
/// this is safe without either side corrupting the other's internal state.
/// </summary>
public sealed class ComboSweepAnimator
{
    public const double MinCycleSeconds = 1.0;
    public const double MaxCycleSeconds = 60.0;
    private const double DefaultCycleSeconds = 15.0;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xe3, 0xb0, 0x42),
        Color.FromRgb(0x22, 0x84, 0xd4),
        Color.FromRgb(0x6c, 0x22, 0xd4),
        Color.FromRgb(0xd4, 0x1c, 0x37),
    ];

    // How wide (in step-units) the traveling highlight's falloff is - at
    // 1.8, a step is still noticeably lit while its neighbor is already
    // brightening, so up to 3 adjacent steps overlap at once instead of a
    // clean hand-off between just 2.
    private const double FalloffWidthSteps = 1.8;

    private readonly IReadOnlyList<ParLight>[] _steps;
    private readonly bool[] _stepIsFloorGroup;

    private double _cycleSeconds = DefaultCycleSeconds;
    private double _time;
    private bool _isRunning;
    private bool _wantsActive;
    private DateTime _lastFrameTime;

    /// <summary>Scales the whole effect's brightness - intended to follow the driving fader's own Value.</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>How long one full lap (sweep AND color) takes, clamped to [<see cref="MinCycleSeconds"/>, <see cref="MaxCycleSeconds"/>].</summary>
    public double CycleSeconds
    {
        get => _cycleSeconds;
        set => _cycleSeconds = Math.Clamp(value, MinCycleSeconds, MaxCycleSeconds);
    }

    public bool IsWallGroupSuppressed { get; private set; }
    public bool IsFloorGroupSuppressed { get; private set; }

    /// <param name="steps">The 7 steps in sweep order, each 1 or more lights that light up together.</param>
    /// <param name="stepIsFloorGroup">Parallel to <paramref name="steps"/> - true for the floor-par steps, false for the wall/striplight ones.</param>
    public ComboSweepAnimator(IReadOnlyList<ParLight>[] steps, bool[] stepIsFloorGroup)
    {
        _steps = steps;
        _stepIsFloorGroup = stepIsFloorGroup;
    }

    /// <summary>Call whenever the fader driving this effect crosses to/from 0%.</summary>
    public void SetActive(bool active)
    {
        _wantsActive = active;

        if (active)
        {
            EnsureRunning();
            return;
        }

        // The caller (MainWindow's per-tick UpdateComboSweepFaderEffect)
        // calls this with active=false on every single tick whenever the
        // driving fader just sits at 0%, not only on the actual
        // active-to-inactive transition - already being stopped means
        // there's nothing to turn off, so bail out before force-blacking
        // these lights again, which would otherwise stomp on whatever
        // unrelated effect (e.g. an automated flash) is using them right now.
        if (!_isRunning)
            return;

        ForceAllBlack();
        Stop();
    }

    /// <summary>Force just the wall/striplight steps off (true) or let them sweep again (false).</summary>
    public void SetWallGroupSuppressed(bool suppressed)
    {
        IsWallGroupSuppressed = suppressed;
        if (suppressed)
            ForceGroupBlack(isFloorGroup: false);
    }

    /// <summary>Force just the floor-par steps off (true) or let them sweep again (false).</summary>
    public void SetFloorGroupSuppressed(bool suppressed)
    {
        IsFloorGroupSuppressed = suppressed;
        if (suppressed)
            ForceGroupBlack(isFloorGroup: true);
    }

    private void ForceGroupBlack(bool isFloorGroup)
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            if (_stepIsFloorGroup[i] != isFloorGroup)
                continue;

            foreach (var light in _steps[i])
                light.Color = Colors.Black;
        }
    }

    private void ForceAllBlack()
    {
        foreach (var step in _steps)
        {
            foreach (var light in step)
                light.Color = Colors.Black;
        }
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_isRunning)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _isRunning = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        if (!_wantsActive)
            return;

        _time += dt;

        var stepCount = _steps.Length;
        var secondsPerStep = _cycleSeconds / stepCount;
        var position = _time / secondsPerStep % stepCount;

        var secondsPerColor = _cycleSeconds / Palette.Length;
        var colorPosition = _time / secondsPerColor % Palette.Length;
        var colorIndex = (int)colorPosition;
        var nextColorIndex = (colorIndex + 1) % Palette.Length;
        var colorT = colorPosition - colorIndex;
        var currentColor = Lerp(Palette[colorIndex], Palette[nextColorIndex], colorT);
        var scaledColor = Scale(currentColor, Brightness);

        for (var i = 0; i < stepCount; i++)
        {
            var isFloorStep = _stepIsFloorGroup[i];

            var suppressed = isFloorStep ? IsFloorGroupSuppressed : IsWallGroupSuppressed;
            if (suppressed)
            {
                foreach (var light in _steps[i])
                    light.Color = Colors.Black;
                continue;
            }

            var distance = CircularDistance(position, i, stepCount);
            var weight = distance >= FalloffWidthSteps
                ? 0.0
                : 0.5 * (1.0 + Math.Cos(Math.PI * distance / FalloffWidthSteps));
            var stepColor = Scale(scaledColor, weight);

            foreach (var light in _steps[i])
                light.Color = Max(light.Color, stepColor);
        }
    }

    private static double CircularDistance(double a, int b, int count)
    {
        var diff = Math.Abs(a - b) % count;
        return Math.Min(diff, count - diff);
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
