using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Normalizes the overall loudness of incoming audio before it reaches any
/// detector. Every detector in this app (bass onset, impact, percussion
/// buildup, loudness) uses a fixed absolute-level floor, tuned against
/// typical consumer playback loudness - but not every source is that loud.
/// Streaming services and browsers commonly apply their own loudness
/// normalization (Spotify targets roughly -14 LUFS); DJ software doesn't -
/// it just plays a track at whatever the monitor/master volume happens to
/// be set to. A genuinely quieter source makes every detector look "less
/// sensitive" simultaneously, purely because its absolute level sits under
/// thresholds tuned for something louder - which is a level mismatch, not a
/// detection problem, and gain is the correct place to fix it.
/// </summary>
public sealed class AutomaticGainControl
{
    // What "normal" playback loudness is assumed to be - matches roughly
    // what the other detectors' absolute floors were tuned against.
    private const double TargetRms = 0.1; // -20 dBFS

    // Slow enough to track "what source/session is this" rather than
    // reacting to a track's own quiet breakdowns or loud drops - the point
    // is normalizing between different playback sources, not flattening a
    // track's own dynamics into mush.
    private const double SmoothingSeconds = 4.0;

    // Only ever boosts, never attenuates - a source already at or above
    // normal loudness doesn't need "fixing", and reducing gain below 1
    // risks making a merely-loud (not clipping) source look quiet instead.
    private const double MinGain = 1.0;
    private const double MaxGain = 8.0;

    // Below this there's nothing meaningful to measure loudness from (near
    // silence) - holding the average steady here avoids the gain swinging
    // wildly to chase noise floor during silence between tracks.
    private const double MinimumRmsToAdapt = 0.002;

    private double _averageRms;
    private double _gain = 1.0;

    /// <summary>Applies gain to <paramref name="samples"/> in place.</summary>
    public void Apply(float[] samples, int sampleRate)
    {
        if (samples.Length == 0)
            return;

        double sumSquares = 0;
        foreach (var sample in samples)
            sumSquares += sample * sample;

        var instantRms = Math.Sqrt(sumSquares / samples.Length);
        var bufferSeconds = (double)samples.Length / sampleRate;

        if (instantRms > MinimumRmsToAdapt)
        {
            var k = 1.0 - Math.Exp(-bufferSeconds / SmoothingSeconds);
            _averageRms += k * (instantRms - _averageRms);
        }

        if (_averageRms > MinimumRmsToAdapt)
            _gain = Math.Clamp(TargetRms / _averageRms, MinGain, MaxGain);

        if (_gain <= 1.0)
            return;

        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Clamp(samples[i] * _gain, -1.0, 1.0);
    }
}
