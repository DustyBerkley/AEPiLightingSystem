using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Sole owner of every ceiling light bar's 13 white LEDs. Each
/// <see cref="FlashRandom"/> snaps a fresh random handful of them on every
/// bar to a peak, then everything decays back to off - LEDs picked by an
/// earlier flash keep decaying underneath rather than being cut off.
/// Intended to ride along with the floor pars' percussion-buildup flashes.
/// </summary>
public sealed class LightBarWhiteFlashAnimator
{
    // Roughly a third of each bar's 13 white LEDs per flash.
    private const int LedsPerFlash = 4;

    private const double StopThreshold = 0.002;

    private readonly IReadOnlyList<CeilingLightBar> _bars;
    private readonly double[][] _intensities;
    private readonly Random _random = new();

    private double _decaySeconds = 0.2;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing the light bars off - every call is ignored
    /// outright while set.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    public LightBarWhiteFlashAnimator(IReadOnlyList<CeilingLightBar> bars)
    {
        _bars = bars;
        _intensities = new double[bars.Count][];
        for (var b = 0; b < bars.Count; b++)
            _intensities[b] = new double[CeilingLightBar.WhiteLedCount];
    }

    /// <summary>
    /// Snaps a random selection of white LEDs on every bar to
    /// <paramref name="peakIntensity"/>, decaying over
    /// <paramref name="decaySeconds"/>.
    /// </summary>
    public void FlashRandom(double decaySeconds, double peakIntensity)
    {
        if (IsSuppressed)
            return;

        _decaySeconds = decaySeconds;

        foreach (var intensities in _intensities)
        {
            // Partial Fisher-Yates over the LED indices - a distinct random
            // pick of LedsPerFlash, chosen independently per bar.
            var indices = new int[intensities.Length];
            for (var i = 0; i < indices.Length; i++)
                indices[i] = i;

            for (var i = 0; i < LedsPerFlash; i++)
            {
                var j = _random.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
                intensities[indices[i]] = Math.Max(intensities[indices[i]], peakIntensity);
            }
        }

        EnsureRunning();
    }

    /// <summary>Force every white LED off (true) or let flashes through again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        foreach (var intensities in _intensities)
            Array.Clear(intensities);
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

        var decay = Math.Exp(-dt / _decaySeconds);
        var stillAnimating = false;

        foreach (var intensities in _intensities)
        {
            for (var i = 0; i < intensities.Length; i++)
            {
                intensities[i] *= decay;
                if (intensities[i] < StopThreshold)
                    intensities[i] = 0.0;
                else
                    stillAnimating = true;
            }
        }

        WriteLevels();

        if (!stillAnimating)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private void WriteLevels()
    {
        for (var b = 0; b < _bars.Count; b++)
        {
            var levels = _bars[b].WhiteLevels;
            var intensities = _intensities[b];
            for (var i = 0; i < levels.Length; i++)
                levels[i] = (byte)Math.Round(Math.Clamp(intensities[i], 0.0, 1.0) * 255.0);

            _bars[b].RaiseChanged();
        }
    }
}
