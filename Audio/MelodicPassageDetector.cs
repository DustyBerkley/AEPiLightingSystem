using System;
using System.Collections.Generic;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Detects a melodic passage carrying the track - an arpeggio or vocal line
/// - with no bassline and little percussion. Combines three signals, each
/// smoothed/debounced so brief blips don't flicker the result:
///   - sustained mid/high-frequency presence (the melody/vocal itself),
///     isolated with a high-pass filter so bass content can't trigger it
///   - no steady bassline (caller supplies this from BasslineActivityTracker)
///   - a low rate of percussive hits (kicks/snares/hats) in that same band
///
/// Percussion isn't distinguished from melody by loudness - a lead vocal or
/// arpeggio can be mixed just as loud as a hi-hat. Instead it's distinguished
/// by attack sharpness: a drum hit's energy rises within a couple of
/// milliseconds, while even a fast plucked/sung note takes noticeably longer
/// to reach full level. Two envelope followers with different attack speeds
/// (fast vs. slow) track the same signal; when the fast one shoots well
/// above the slow one, that's a sharp attack - percussion, not melody.
/// </summary>
public sealed class MelodicPassageDetector
{
    private const double HighPassCutoffHz = 400.0;
    private const double LevelSmoothingSeconds = 0.2;

    // Mid/high content must clear this to count as a melody/vocal actually
    // playing, not just noise floor or near-silence.
    private const double MinimumMidHighRms = 0.02;

    private const double FastEnvelopeMs = 3.0;
    private const double SlowEnvelopeMs = 45.0;
    private const double PercussionSharpnessRatio = 2.5;
    private const double PercussionMinimumLevel = 0.05;
    private const double PercussionRefractorySeconds = 0.08;

    private const double PercussionWindowSeconds = 2.5;
    private const int MaxPercussionHitsAllowed = 1;

    private const double StateChangeHoldSeconds = 1.0;

    private readonly Queue<DateTime> _recentPercussionHits = new();

    private double _highPassState;
    private double _highPassPrevInput;
    private double _smoothedMidHighRms;
    private double _fastEnvelope;
    private double _slowEnvelope;
    private DateTime _lastPercussionHitTime = DateTime.MinValue;

    private bool _rawConditionsMet;
    private DateTime _streakStart = DateTime.MinValue;

    public bool IsActive { get; private set; }

    public void Process(float[] samples, int sampleRate, bool basslineActive)
    {
        if (samples.Length == 0)
            return;

        // One-pole high-pass coefficient (distinct from a low-pass filter's
        // alpha - this form needs alpha close to 1 for a low cutoff).
        var hpAlpha = sampleRate / (sampleRate + 2.0 * Math.PI * HighPassCutoffHz);
        var fastAlpha = 1.0 - Math.Exp(-1.0 / (FastEnvelopeMs / 1000.0 * sampleRate));
        var slowAlpha = 1.0 - Math.Exp(-1.0 / (SlowEnvelopeMs / 1000.0 * sampleRate));

        double sumSquares = 0;
        var sharpAttackDetected = false;

        foreach (var sample in samples)
        {
            // One-pole high-pass: isolates everything above the bass band.
            var output = hpAlpha * (_highPassState + sample - _highPassPrevInput);
            _highPassPrevInput = sample;
            _highPassState = output;
            sumSquares += output * output;

            var magnitude = Math.Abs(output);
            _fastEnvelope += fastAlpha * (magnitude - _fastEnvelope);
            _slowEnvelope += slowAlpha * (magnitude - _slowEnvelope);

            if (_fastEnvelope > PercussionMinimumLevel && _fastEnvelope > _slowEnvelope * PercussionSharpnessRatio)
                sharpAttackDetected = true;
        }

        var instantRms = Math.Sqrt(sumSquares / samples.Length);
        var bufferSeconds = (double)samples.Length / sampleRate;

        var levelSmoothing = 1.0 - Math.Exp(-bufferSeconds / LevelSmoothingSeconds);
        _smoothedMidHighRms += levelSmoothing * (instantRms - _smoothedMidHighRms);

        var now = DateTime.UtcNow;

        if (sharpAttackDetected && (now - _lastPercussionHitTime).TotalSeconds > PercussionRefractorySeconds)
        {
            _lastPercussionHitTime = now;
            _recentPercussionHits.Enqueue(now);
        }

        while (_recentPercussionHits.Count > 0 &&
               (now - _recentPercussionHits.Peek()).TotalSeconds > PercussionWindowSeconds)
        {
            _recentPercussionHits.Dequeue();
        }

        var hasMelodicPresence = _smoothedMidHighRms > MinimumMidHighRms;
        var percussionIsLimited = _recentPercussionHits.Count <= MaxPercussionHitsAllowed;
        var conditionsNow = hasMelodicPresence && percussionIsLimited && !basslineActive;

        if (conditionsNow != _rawConditionsMet)
        {
            _rawConditionsMet = conditionsNow;
            _streakStart = now;
        }

        var streakSeconds = (now - _streakStart).TotalSeconds;

        if (_rawConditionsMet && !IsActive && streakSeconds >= StateChangeHoldSeconds)
            IsActive = true;
        else if (!_rawConditionsMet && IsActive && streakSeconds >= StateChangeHoldSeconds)
            IsActive = false;
    }
}
