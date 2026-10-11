using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Drives the floor par lights' baseline state, independent of every effect
/// on the wall par lights: a constant dim white while music is playing at a
/// reasonable volume, fading to black when it isn't. <see cref="Flash"/>
/// spikes them to <see cref="FlashColor"/> (white by default) on top of that
/// and decays back down to whatever the ambient baseline currently is (not
/// to black), so a flash reads as a brief spike over the resting state
/// rather than fighting it.
/// </summary>
public sealed class ParLightAmbientAnimator
{
    private const double TargetBrightness = 0.03;
    private const double AmbientFadeSeconds = 1.0;

    // Matches the wall par lights' own flash decay so both groups read the
    // same speed of "flash" for a one-off rig flash. Faster percussion
    // (see Flash(double)) uses a shorter decay of its own.
    private const double DefaultFlashDecaySeconds = 0.22;

    // A decay this short or shorter is imperceptible as a distinct pulse
    // rather than a constant glow, regardless of how fast hits are arriving.
    private const double MinimumFlashDecaySeconds = 0.03;

    private readonly IReadOnlyList<ParLight> _lights;

    private double _ambientTargetIntensity;
    private double _ambientCurrentIntensity;
    private double _flashIntensity;
    private double _flashDecaySeconds = DefaultFlashDecaySeconds;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    /// <summary>
    /// The color <see cref="Flash"/> spikes to - set this before calling
    /// Flash if a call site wants something other than white (the default,
    /// and what every call site got before this existed).
    /// </summary>
    public Color FlashColor { get; set; } = Colors.White;

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing this group off - SetActive/Flash calls are
    /// ignored outright while set, regardless of who's calling them (manual
    /// fader effect or automation).
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public ParLightAmbientAnimator(IReadOnlyList<ParLight> lights)
    {
        _lights = lights;
    }

    /// <summary>Force this group off (true) or let it respond to SetActive/Flash calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        _ambientTargetIntensity = 0.0;
        _ambientCurrentIntensity = 0.0;
        _flashIntensity = 0.0;

        foreach (var light in _lights)
            light.Color = Colors.Black;

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    /// <summary>Call whenever the "is music reasonably loud" state changes.</summary>
    public void SetActive(bool loud)
    {
        if (IsSuppressed)
            return;

        _ambientTargetIntensity = loud ? 1.0 : 0.0;
        EnsureRunning();
    }

    /// <summary>
    /// Spikes to <paramref name="peakIntensity"/> (full <see cref="FlashColor"/>
    /// by default), then decays back down to the ambient baseline over
    /// <paramref name="decaySeconds"/> (default: matches the wall par
    /// lights' flash). A shorter decay reads as a sharper, more distinct
    /// pulse - useful for timing a flash to a fast, specific event rather
    /// than a generic "something big just happened" flash.
    /// </summary>
    public void Flash(double? decaySeconds = null, double peakIntensity = 1.0)
    {
        if (IsSuppressed)
            return;

        _flashIntensity = Math.Clamp(peakIntensity, 0.0, 1.0);
        _flashDecaySeconds = Math.Max(MinimumFlashDecaySeconds, decaySeconds ?? DefaultFlashDecaySeconds);
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

        var ambientStep = dt / AmbientFadeSeconds;
        if (_ambientCurrentIntensity < _ambientTargetIntensity)
            _ambientCurrentIntensity = Math.Min(_ambientTargetIntensity, _ambientCurrentIntensity + ambientStep);
        else if (_ambientCurrentIntensity > _ambientTargetIntensity)
            _ambientCurrentIntensity = Math.Max(_ambientTargetIntensity, _ambientCurrentIntensity - ambientStep);

        if (_flashIntensity > 0)
        {
            var decay = 1.0 - Math.Exp(-dt / _flashDecaySeconds);
            _flashIntensity = Math.Max(0, _flashIntensity - decay * _flashIntensity);
        }

        // Flash spikes to FlashColor rather than replacing the ambient
        // baseline outright - scaling that color by intensity and taking
        // the brighter of the two per channel is what lets the flash read
        // as a spike over the (always grayscale) baseline instead of one
        // fighting the other, the same "brighter wins" idea used elsewhere
        // for overlapping effects. The light's current color is folded into
        // that same max too, so this composites correctly with whatever
        // else might be writing to the same light (e.g. a combo fader's
        // continuous color cycle) instead of overwriting it outright.
        var ambientColor = ToGray(_ambientCurrentIntensity * TargetBrightness);
        var flashColor = Scale(FlashColor, _flashIntensity);
        var ownColor = Max(ambientColor, flashColor);

        foreach (var light in _lights)
            light.Color = Max(light.Color, ownColor);

        // Deliberately NOT "has it finished transitioning" - MainWindow
        // clears every composited ParLight to black at the start of each
        // frame (see ClearCompositedParLights) before any Max-blend
        // animator re-asserts its own contribution, so a steady, non-zero
        // ambient baseline (e.g. music holding loud) has to keep
        // re-writing every single frame or it'd read as going black the
        // instant this stops "still transitioning" and unsubscribes. Only
        // genuinely having nothing left to contribute is safe to stop on.
        var stillAnimating = _flashIntensity > 0.0005 || _ambientCurrentIntensity > 0.0005;
        if (!stillAnimating)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private static Color ToGray(double brightness)
    {
        var level = (byte)Math.Round(255 * brightness);
        return Color.FromRgb(level, level, level);
    }

    private static Color Scale(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private static Color Max(Color a, Color b) =>
        Color.FromRgb(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));
}
