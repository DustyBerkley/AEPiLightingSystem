using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Pulls every ceiling light bar's RGB segments down to
/// <see cref="DimmedLevel"/> while held (see <see cref="DimFor"/>), then
/// eases them back to full once the hold lapses. Works through
/// <see cref="CeilingLightBar.RgbDimFactor"/>, so whatever effect is
/// actually driving the colors keeps running untouched underneath.
/// </summary>
public sealed class LightBarRgbDimmer
{
    private const double DimmedLevel = 0.3;

    // Ducks down quickly so the dim lands with the first flash, but comes
    // back up gently rather than popping.
    private const double DimDownSeconds = 0.15;
    private const double RecoverSeconds = 1.0;

    private readonly IReadOnlyList<CeilingLightBar> _bars;

    private double _factor = 1.0;
    private DateTime _heldUntil = DateTime.MinValue;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    public LightBarRgbDimmer(IReadOnlyList<CeilingLightBar> bars)
    {
        _bars = bars;
    }

    /// <summary>Holds the dim for (at least) the next <paramref name="seconds"/>.</summary>
    public void DimFor(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _heldUntil)
            _heldUntil = until;

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

        var isHeld = now < _heldUntil;
        _factor = isHeld
            ? Math.Max(DimmedLevel, _factor - dt * (1.0 - DimmedLevel) / DimDownSeconds)
            : Math.Min(1.0, _factor + dt * (1.0 - DimmedLevel) / RecoverSeconds);

        foreach (var bar in _bars)
        {
            bar.RgbDimFactor = _factor;
            bar.RaiseChanged();
        }

        if (!isHeld && _factor >= 1.0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }
}
