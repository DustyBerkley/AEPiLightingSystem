using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Snaps every pixel of every LED strip to a flash color, then exponentially
/// decays them all back to off (black), driven by the WPF render loop. Mirrors
/// <see cref="ParLightFlashAnimator"/> but for the strips.
/// </summary>
public sealed class LedStripFlashAnimator
{
    private const double SnapThreshold = 1.0;

    private readonly IReadOnlyList<LedStrip> _strips;
    private readonly double _decaySeconds;

    private DateTime _lastFrameTime;
    private bool _isRunning;

    public Color FlashColor { get; set; } = Colors.White;

    public LedStripFlashAnimator(IReadOnlyList<LedStrip> strips, double decaySeconds)
    {
        _strips = strips;
        _decaySeconds = decaySeconds;
    }

    public void FlashAll() => Flash(_strips);

    /// <summary>
    /// Flashes only the given subset of strips (the rest are left alone -
    /// still decaying toward black if they were already mid-flash).
    /// </summary>
    public void Flash(IEnumerable<LedStrip> strips)
    {
        foreach (var strip in strips)
        {
            Array.Fill(strip.Pixels, FlashColor);
            strip.RaisePixelsChanged();
        }

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

        foreach (var strip in _strips)
        {
            var pixels = strip.Pixels;
            var stripChanged = false;

            for (var i = 0; i < pixels.Length; i++)
            {
                var current = pixels[i];

                if (IsCloseEnough(current, Colors.Black))
                {
                    if (current != Colors.Black)
                    {
                        pixels[i] = Colors.Black;
                        stripChanged = true;
                    }

                    continue;
                }

                pixels[i] = Lerp(current, Colors.Black, t);
                stripChanged = true;
                stillAnimating = true;
            }

            if (stripChanged)
                strip.RaisePixelsChanged();
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
