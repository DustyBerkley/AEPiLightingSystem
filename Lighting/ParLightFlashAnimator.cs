using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Snaps par lights to a bright flash color, then exponentially decays them
/// back to off (black) over time, driven by the WPF render loop.
///
/// Composites with whatever else is writing to the same lights (e.g. a
/// combo fader's continuous color cycle, see WallParColorCycleAnimator)
/// rather than fighting over or deferring to it: this animator tracks its
/// own flash/decay state entirely internally (<see cref="_ownColors"/>,
/// parallel to <see cref="_lights"/>) instead of reading it back from
/// <see cref="ParLight.Color"/> - that's what lets it take the per-channel
/// brighter of "my own decaying flash" and "whatever's already showing" as
/// the light's actual displayed color every frame, so a bright flash reads
/// as a spike on top of a calmer effect underneath (the same "brighter
/// wins" idea ParLightAmbientAnimator already uses for its own flash-over-
/// ambient blend) without corrupting its own decay curve by accidentally
/// reading back a brighter color some other effect just wrote.
/// </summary>
public sealed class ParLightFlashAnimator
{
    private const double SnapThreshold = 1.0;

    private readonly IReadOnlyList<ParLight> _lights;
    private readonly Color[] _ownColors;
    private readonly double _defaultDecaySeconds;
    private double _decaySeconds;

    private DateTime _lastFrameTime;
    private bool _isRunning;

    public Color FlashColor { get; set; } = Colors.White;

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing this group off - every Flash call is ignored
    /// outright while set, regardless of who's calling it (manual fader
    /// effect or automation).
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public ParLightFlashAnimator(IReadOnlyList<ParLight> lights, double decaySeconds)
    {
        _lights = lights;
        _ownColors = new Color[lights.Count];
        _defaultDecaySeconds = decaySeconds;
        _decaySeconds = decaySeconds;
    }

    /// <summary>Force this group off (true) or let it respond to Flash calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        for (var i = 0; i < _lights.Count; i++)
        {
            _ownColors[i] = Colors.Black;
            _lights[i].Color = Colors.Black;
        }

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
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
        if (IsSuppressed)
            return;

        _decaySeconds = decaySeconds ?? _defaultDecaySeconds;

        foreach (var light in lights)
        {
            var index = IndexOf(light);
            if (index >= 0)
                _ownColors[index] = FlashColor;
        }

        EnsureRunning();
    }

    private int IndexOf(ParLight light)
    {
        for (var i = 0; i < _lights.Count; i++)
        {
            if (ReferenceEquals(_lights[i], light))
                return i;
        }

        return -1;
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

        for (var i = 0; i < _lights.Count; i++)
        {
            var own = _ownColors[i];

            if (IsCloseEnough(own, Colors.Black))
            {
                own = Colors.Black;
            }
            else
            {
                own = Lerp(own, Colors.Black, t);
                stillAnimating = true;
            }

            _ownColors[i] = own;
            _lights[i].Color = Max(_lights[i].Color, own);
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

    private static Color Max(Color a, Color b) =>
        Color.FromRgb(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));
}
