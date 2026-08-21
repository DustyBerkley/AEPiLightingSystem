using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Turns the music's current overall energy (see
/// <see cref="LoudnessTracker.SmoothedRms"/>) into a single smoothed speed
/// multiplier, shared by every animation that should move faster during
/// energetic passages and slower during calmer ones (currently the LED
/// strips' lava lamp and the laser's wave effect). Applied by scaling the
/// animator's own internal clock (see how each multiplies dt by
/// <c>SpeedFactor</c> before advancing its time accumulator), so a change
/// here speeds up or slows down every time-based movement in that effect
/// uniformly rather than needing to touch each individual rate constant.
/// </summary>
public sealed class EffectSpeedTracker
{
    // RMS at which the music is considered energetic enough to earn the
    // full speed-up - past this, more loudness doesn't speed things up
    // further.
    private const double EnergeticReferenceRms = 0.15;

    // Deliberately not a huge range - a noticeable tempo shift, not
    // something that makes the effect look broken during a quiet verse or
    // frantic during a loud chorus.
    private const double MinSpeedFactor = 0.7;
    private const double MaxSpeedFactor = 1.3;

    // Slower than the brightness effect's smoothing - a speed change should
    // feel like the music's energy gradually shifting, not flicker with
    // every beat.
    private const double SmoothingSeconds = 1.0;

    /// <summary>The current smoothed speed multiplier.</summary>
    public double SpeedFactor { get; private set; } = MinSpeedFactor;

    public void Process(double loudnessRms, double bufferSeconds)
    {
        var energyFraction = Math.Clamp(loudnessRms / EnergeticReferenceRms, 0.0, 1.0);
        var target = MinSpeedFactor + (MaxSpeedFactor - MinSpeedFactor) * energyFraction;

        var smoothing = 1.0 - Math.Exp(-bufferSeconds / SmoothingSeconds);
        SpeedFactor += smoothing * (target - SpeedFactor);
    }
}
