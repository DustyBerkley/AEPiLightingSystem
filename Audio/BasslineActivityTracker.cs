using System;
using System.Collections.Generic;
using System.Linq;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Tracks whether a steady, consistent bassline is currently playing - as
/// opposed to just a single isolated bass hit - by watching how regular the
/// spacing between recent bass hits has been.
/// </summary>
public sealed class BasslineActivityTracker
{
    private const int RequiredConsecutiveHits = 3;
    private const double RegularityTolerance = 0.35; // each gap must be within +/-35% of the recent mean
    private const double MinBeatIntervalSeconds = 0.25; // 240 BPM cap
    private const double MaxBeatIntervalSeconds = 1.2; // 50 BPM floor
    private const double StaleAfterMissedIntervals = 2.0;

    private readonly Queue<double> _recentGaps = new();
    private DateTime _lastHitTime = DateTime.MinValue;

    /// <summary>
    /// True if recent bass hits have landed at a steady, musically plausible
    /// tempo and one hasn't gone quiet for a couple of beats since.
    /// </summary>
    public bool IsActive
    {
        get
        {
            if (_recentGaps.Count < RequiredConsecutiveHits)
                return false;

            var mean = _recentGaps.Average();

            if (_recentGaps.Any(gap => Math.Abs(gap - mean) > mean * RegularityTolerance))
                return false;

            var elapsedSinceLastHit = (DateTime.UtcNow - _lastHitTime).TotalSeconds;
            return elapsedSinceLastHit < mean * StaleAfterMissedIntervals;
        }
    }

    /// <summary>Call once per detected bass hit.</summary>
    public void RegisterBassHit()
    {
        var now = DateTime.UtcNow;

        if (_lastHitTime != DateTime.MinValue)
        {
            var gap = (now - _lastHitTime).TotalSeconds;

            if (gap is >= MinBeatIntervalSeconds and <= MaxBeatIntervalSeconds)
            {
                _recentGaps.Enqueue(gap);
                while (_recentGaps.Count > RequiredConsecutiveHits)
                    _recentGaps.Dequeue();
            }
            else
            {
                // A gap way outside a plausible tempo range breaks the streak.
                _recentGaps.Clear();
            }
        }

        _lastHitTime = now;
    }
}
