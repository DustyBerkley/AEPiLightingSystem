using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Sole owner of every ceiling light bar's 12 amber bulbs, combining two
/// effects that can overlap (the lava lamp is often still fading out as a
/// drop lands):
/// <list type="bullet">
/// <item>A twinkle - random bulbs somewhere on the ceiling snap on and fade
/// out, staggered so two are always going at once, each with its two
/// neighbors glowing at half brightness alongside it - that fades in and out
/// with the lava lamp (see <see cref="SetTwinkleActive"/>).</item>
/// <item>A flash of every bulb on one bar (<see cref="FlashBar"/>) or on
/// all of them at once (<see cref="FlashAll"/>) that decays away. Each bar
/// tracks its own flash, so one bar's pulse can overlap another's.</item>
/// </list>
/// Each bulb shows the brighter of the two. Both are recomputed from this
/// animator's own state every frame and written outright - never blended
/// against whatever the bulb already showed - so either one fading out
/// actually dims the bulb.
/// </summary>
public sealed class LightBarAmberAnimator
{
    // Each twinkle snaps on and fades out to nothing over this long (at
    // SpeedFactor 1).
    private const double TwinkleFadeSeconds = 2.5;

    // How many twinkles are going at once - a new one starts every
    // TwinkleFadeSeconds / this, so there's always one fresh and one
    // partway through fading.
    private const int ConcurrentTwinkles = 2;
    private const double TwinkleIntervalSeconds = TwinkleFadeSeconds / ConcurrentTwinkles;

    // The bulbs directly either side of a twinkling one glow along with it
    // at this fraction of its brightness.
    private const double TwinkleNeighborLevel = 0.5;

    // A calm background glow, not a flash - kept well below full so the drop
    // flash and bass pulses still stand out against it.
    private const double TwinklePeakLevel = 0.6;

    // Same fade as the lava lamp's own, so the two arrive and leave together.
    private const double TwinkleEffectFadeSeconds = 1.5;

    private const double StopThreshold = 0.002;

    private readonly IReadOnlyList<CeilingLightBar> _bars;
    private readonly Random _random = new();

    // Per bar, so a single-bar pulse can decay at its own rate while
    // another bar's (or an all-bar flash) runs alongside it.
    private readonly double[] _flashIntensities;
    private readonly double[] _flashDecaySeconds;

    private readonly List<Twinkle> _twinkles = new();

    // The twinkle clock - advances at SpeedFactor, so twinkles quicken with
    // the music's energy like the lava lamp does.
    private double _twinkleTime;
    private double _nextTwinkleStart;
    private double _twinkleTargetIntensity;
    private double _twinkleCurrentIntensity;
    private DateTime _twinkleHiddenUntil = DateTime.MinValue;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    private readonly record struct Twinkle(int BarIndex, int BulbIndex, double StartTime);

    /// <summary>Scales the twinkle's brightness - set alongside the lava lamp's own.</summary>
    public double BrightnessFactor { get; set; } = 1.0;

    /// <summary>Scales how fast twinkles come and go - set alongside the lava lamp's own.</summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing the light bars off - every call is ignored
    /// outright while set.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public LightBarAmberAnimator(IReadOnlyList<CeilingLightBar> bars)
    {
        _bars = bars;
        _flashIntensities = new double[bars.Count];
        _flashDecaySeconds = new double[bars.Count];
        Array.Fill(_flashDecaySeconds, 1.0);
    }

    /// <summary>Fades the twinkle in (true) or out (false). Safe to call repeatedly with the same value.</summary>
    public void SetTwinkleActive(bool active)
    {
        if (IsSuppressed)
            return;

        // Starting up: the first bulb should begin right away rather than
        // waiting out whatever was left of a stale interval.
        if (active && _twinkleTargetIntensity == 0.0)
            _nextTwinkleStart = _twinkleTime;

        _twinkleTargetIntensity = active ? 1.0 : 0.0;
        EnsureRunning();
    }

    /// <summary>
    /// Keeps the twinkle fully off for the next <paramref name="seconds"/>
    /// (e.g. while the white LEDs are flashing with a percussion buildup).
    /// It fades back in from nothing afterwards rather than snapping back
    /// on. Doesn't affect flashes.
    /// </summary>
    public void HideTwinkleFor(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _twinkleHiddenUntil)
            _twinkleHiddenUntil = until;

        _twinkleCurrentIntensity = 0.0;
        EnsureRunning();
    }

    /// <summary>Snaps every bulb on every bar to full, then decays over <paramref name="decaySeconds"/>.</summary>
    public void FlashAll(double decaySeconds)
    {
        for (var b = 0; b < _bars.Count; b++)
            FlashBar(b, decaySeconds);
    }

    /// <summary>Snaps every bulb on one bar to full, then decays over <paramref name="decaySeconds"/>.</summary>
    public void FlashBar(int barIndex, double decaySeconds)
    {
        if (IsSuppressed)
            return;

        _flashIntensities[barIndex] = 1.0;
        _flashDecaySeconds[barIndex] = decaySeconds;
        EnsureRunning();
    }

    /// <summary>Force every bulb off (true) or let the effects respond again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        _twinkleTargetIntensity = 0.0;
        _twinkleCurrentIntensity = 0.0;
        _twinkles.Clear();
        Array.Clear(_flashIntensities);
        WriteLevels();

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
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

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        _twinkleTime += dt * SpeedFactor;

        var isHidden = now < _twinkleHiddenUntil;
        var fadeStep = dt / TwinkleEffectFadeSeconds;
        if (isHidden)
            _twinkleCurrentIntensity = 0.0;
        else if (_twinkleCurrentIntensity < _twinkleTargetIntensity)
            _twinkleCurrentIntensity = Math.Min(_twinkleTargetIntensity, _twinkleCurrentIntensity + fadeStep);
        else if (_twinkleCurrentIntensity > _twinkleTargetIntensity)
            _twinkleCurrentIntensity = Math.Max(_twinkleTargetIntensity, _twinkleCurrentIntensity - fadeStep);

        _twinkles.RemoveAll(t => _twinkleTime - t.StartTime >= TwinkleFadeSeconds);

        // Only start new bulbs while the effect is on and visible - ones
        // already going just play out.
        if (_twinkleTargetIntensity > 0.0 && !isHidden && _twinkleTime >= _nextTwinkleStart)
        {
            StartRandomTwinkle();
            _nextTwinkleStart = _twinkleTime + TwinkleIntervalSeconds;
        }

        var anyFlashing = false;
        for (var b = 0; b < _flashIntensities.Length; b++)
        {
            _flashIntensities[b] *= Math.Exp(-dt / _flashDecaySeconds[b]);
            if (_flashIntensities[b] < StopThreshold)
                _flashIntensities[b] = 0.0;
            else
                anyFlashing = true;
        }

        WriteLevels();

        if (!anyFlashing && _twinkleCurrentIntensity == 0.0 && _twinkleTargetIntensity == 0.0)
        {
            _twinkles.Clear();
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    // Any bulb on any bar, kept clear of the other twinkle's glow (the bulb
    // itself and its lit neighbors) so the two always read as separate spots.
    private void StartRandomTwinkle()
    {
        const int bulbsPerBar = CeilingLightBar.AmberBulbCount;
        var totalBulbs = _bars.Count * bulbsPerBar;

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var pick = _random.Next(totalBulbs);
            var barIndex = pick / bulbsPerBar;
            var bulbIndex = pick % bulbsPerBar;

            if (_twinkles.Exists(t => t.BarIndex == barIndex && Math.Abs(t.BulbIndex - bulbIndex) <= 2))
                continue;

            _twinkles.Add(new Twinkle(barIndex, bulbIndex, _twinkleTime));
            return;
        }
    }

    // Full the instant it starts, reaching exactly 0 at TwinkleFadeSeconds.
    // Squared so it drops off quickly at first like a flash, then eases out
    // gently instead of cutting off.
    private static double TwinkleEnvelope(double age)
    {
        if (age < 0.0 || age >= TwinkleFadeSeconds)
            return 0.0;

        var remaining = 1.0 - age / TwinkleFadeSeconds;
        return remaining * remaining;
    }

    private void WriteLevels()
    {
        var twinkleScale = TwinklePeakLevel * _twinkleCurrentIntensity * BrightnessFactor;

        for (var b = 0; b < _bars.Count; b++)
        {
            var levels = _bars[b].AmberLevels;
            for (var i = 0; i < levels.Length; i++)
            {
                var twinkle = 0.0;
                foreach (var t in _twinkles)
                {
                    if (t.BarIndex != b)
                        continue;

                    var offset = Math.Abs(t.BulbIndex - i);
                    if (offset > 1)
                        continue;

                    var envelope = TwinkleEnvelope(_twinkleTime - t.StartTime);
                    twinkle = Math.Max(twinkle, offset == 0 ? envelope : envelope * TwinkleNeighborLevel);
                }

                var level = Math.Max(twinkle * twinkleScale, _flashIntensities[b]);
                levels[i] = (byte)Math.Round(Math.Clamp(level, 0.0, 1.0) * 255.0);
            }

            _bars[b].RaiseChanged();
        }
    }
}
