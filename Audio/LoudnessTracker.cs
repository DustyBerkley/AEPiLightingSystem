using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Tracks whether the room is currently "reasonably loud" - i.e. music is
/// actually playing at a normal listening level, as opposed to near-silence
/// between tracks or very quiet background sound. Unlike the onset
/// detectors, this isn't about transients at all: it's a slow-smoothed
/// overall level with a simple fixed threshold, meant to drive a steady
/// ambient state rather than fire discrete events.
/// </summary>
public sealed class LoudnessTracker
{
    // Smoothed over half a second so brief dips between beats don't flicker
    // the result - this should track "is music playing", not the beat itself.
    private const double SmoothingSeconds = 0.5;

    // -28 dBFS RMS: comfortably above room noise/silence, but below what a
    // real bass hit or impact would read - meant to capture "music is on at
    // a normal volume", not any particular loud moment.
    private const double LoudRms = 0.0398; // 10^(-28/20)
    private const double LoudEnergyThreshold = LoudRms * LoudRms;

    private double _smoothedEnergy;

    /// <summary>True once the smoothed level has been above the threshold.</summary>
    public bool IsLoud { get; private set; }

    /// <summary>
    /// The same smoothed level as <see cref="IsLoud"/>, as an RMS value
    /// rather than a threshold, exposed for effects that should track the
    /// music's current overall energy (e.g. animation speed).
    /// </summary>
    public double SmoothedRms => Math.Sqrt(_smoothedEnergy);

    public void Process(float[] samples, int sampleRate)
    {
        if (samples.Length == 0)
            return;

        double sumSquares = 0;
        foreach (var sample in samples)
            sumSquares += sample * sample;

        var instantEnergy = sumSquares / samples.Length;
        var bufferSeconds = (double)samples.Length / sampleRate;

        var k = 1.0 - Math.Exp(-bufferSeconds / SmoothingSeconds);
        _smoothedEnergy += k * (instantEnergy - _smoothedEnergy);

        IsLoud = _smoothedEnergy > LoudEnergyThreshold;
    }
}
