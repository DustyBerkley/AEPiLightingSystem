using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Snaps every RGB segment of the given ceiling light bars to a flash color,
/// then exponentially decays them all back to off (black), driven by the
/// WPF render loop. Mirrors <see cref="ParLightFlashAnimator"/> but for the
/// bars.
/// </summary>
public sealed class LightBarFlashAnimator
{
    private const double SnapThreshold = 1.0;

    private readonly IReadOnlyList<CeilingLightBar> _bars;
    private readonly double _decaySeconds;

    private DateTime _lastFrameTime;
    private bool _isRunning;

    public Color FlashColor { get; set; } = Colors.White;

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing this group off - every Flash call is ignored
    /// outright while set, regardless of who's calling it.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public LightBarFlashAnimator(IReadOnlyList<CeilingLightBar> bars, double decaySeconds)
    {
        _bars = bars;
        _decaySeconds = decaySeconds;
    }

    /// <summary>Force this group off (true) or let it respond to Flash calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        foreach (var bar in _bars)
            bar.FillPixels(Colors.Black);

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    public void FlashAll() => Flash(_bars);

    /// <summary>
    /// Flashes only the given subset of bars (the rest are left alone -
    /// still decaying toward black if they were already mid-flash).
    /// </summary>
    public void Flash(IEnumerable<CeilingLightBar> bars)
    {
        if (IsSuppressed)
            return;

        foreach (var bar in bars)
            bar.FillPixels(FlashColor);

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

        foreach (var bar in _bars)
        {
            var pixels = bar.Pixels;
            var barChanged = false;

            for (var i = 0; i < pixels.Length; i++)
            {
                var current = pixels[i];

                if (IsCloseEnough(current, Colors.Black))
                {
                    if (current != Colors.Black)
                    {
                        pixels[i] = Colors.Black;
                        barChanged = true;
                    }

                    continue;
                }

                pixels[i] = Lerp(current, Colors.Black, t);
                barChanged = true;
                stillAnimating = true;
            }

            if (barChanged)
                bar.RaiseChanged();
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
