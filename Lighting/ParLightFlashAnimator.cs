using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Snaps par lights to a bright flash color, then exponentially decays them
/// back to off (black) over time, driven by the WPF render loop. Lights have
/// no idle/resting color - they're dark except while an effect is animating.
/// </summary>
public sealed class ParLightFlashAnimator
{
    private const double SnapThreshold = 1.0;

    private readonly IReadOnlyList<ParLight> _lights;
    private readonly double _defaultDecaySeconds;
    private double _decaySeconds;

    private DateTime _lastFrameTime;
    private bool _isRunning;

    public Color FlashColor { get; set; } = Colors.White;

    public ParLightFlashAnimator(IReadOnlyList<ParLight> lights, double decaySeconds)
    {
        _lights = lights;
        _defaultDecaySeconds = decaySeconds;
        _decaySeconds = decaySeconds;
    }

    public void FlashAll(double? decaySeconds = null) => Flash(_lights, decaySeconds);

    /// <summary>
    /// Flashes only the given subset of lights (the rest are left alone -
    /// still decaying toward black if they were already mid-flash).
    /// <paramref name="decaySeconds"/> overrides the constructor's default
    /// for this flash only - e.g. a fader-driven effect scaling its own
    /// decay to the current beat interval instead of a fixed rate.
    /// </summary>
    public void Flash(IEnumerable<ParLight> lights, double? decaySeconds = null)
    {
        _decaySeconds = decaySeconds ?? _defaultDecaySeconds;

        foreach (var light in lights)
            light.Color = FlashColor;

        EnsureRunning();
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

        var t = 1 - Math.Exp(-dt / _decaySeconds);
        var stillAnimating = false;

        foreach (var light in _lights)
        {
            var current = light.Color;

            if (IsCloseEnough(current, Colors.Black))
            {
                if (current != Colors.Black)
                    light.Color = Colors.Black;

                continue;
            }

            light.Color = Lerp(current, Colors.Black, t);
            stillAnimating = true;
        }

        if (!stillAnimating)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private static bool IsCloseEnough(Color a, Color b) =>
        Math.Abs(a.R - b.R) < SnapThreshold &&
        Math.Abs(a.G - b.G) < SnapThreshold &&
        Math.Abs(a.B - b.B) < SnapThreshold;

    private static Color Lerp(Color from, Color to, double t)
    {
        byte LerpChannel(byte a, byte b) => (byte)(a + (b - a) * t);

        return Color.FromRgb(
            LerpChannel(from.R, to.R),
            LerpChannel(from.G, to.G),
            LerpChannel(from.B, to.B));
    }
}
