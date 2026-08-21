using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Dsp;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Detects a percussion buildup - hi-hats/snares that are both prominent and
/// speeding up, the classic tension-building pattern leading into a drop -
/// and fires <see cref="Hit"/> for every qualifying hit for as long as the
/// buildup is ongoing, so lights can flash in time with it. Never activates
/// while a steady bassline is holding down a groove (caller supplies this
/// from <see cref="BasslineActivityTracker"/>) - a buildup is a break from
/// the groove, not something that coexists with one.
///
/// Individual hits are found via spectral flux: an FFT is taken every hop,
/// and a hit is a sudden broadband *increase* in energy across the
/// hi-hat/snare frequency range versus the previous frame. This is the
/// standard onset-detection technique for exactly this problem, and it's
/// deliberately not a fixed amplitude/frequency threshold on the raw signal
/// - a real mix has plenty of sustained high-frequency content (cymbal
/// wash, vocal sibilance, synths) that a level threshold can't tell apart
/// from an actual transient, but a genuine hit is characterized by a sudden
/// *change*, not just presence. Comparing frame-to-frame also means this
/// naturally adapts to the overall loudness of the mix rather than needing
/// a level tuned for one track and wrong for the next.
///
/// The adaptive threshold here is its own mean+deviation tracker over the
/// flux signal, not a reuse of <see cref="AdaptiveOnsetDetector"/> - that
/// class's sensitivity formula has constants curve-fit for the RMS-squared
/// energy scale the bass/impact detectors use (tiny values, ~0.001-0.06).
/// Flux lives on a completely different, much larger scale (a sum of FFT
/// magnitude differences across ~350 bins), which saturated that formula's
/// variance term and pinned it at its most lenient setting almost
/// permanently - the direct cause of this over-triggering. Computing mean
/// and standard deviation directly on the flux signal itself keeps the
/// threshold correctly scaled to whatever range flux actually lives in.
/// </summary>
public sealed class PercussionBuildupDetector
{
    // 1024 samples is ~21ms at 48kHz - short enough for good time resolution
    // on fast hits, long enough for meaningful frequency resolution.
    private const int FftSize = 1024;
    private const int FftPower = 10; // log2(FftSize)

    // How often a new FFT frame is analyzed. Smaller than FftSize so
    // consecutive frames overlap, which keeps the flux signal itself
    // smooth enough to catch fast consecutive hits distinctly.
    private const int HopSize = 256;

    // Only bins above this count toward flux - excludes kick drum/bass/vocal
    // fundamental energy, keeping this focused on the hi-hat/snare range the
    // buildup pattern actually lives in.
    private const double MinBinFrequencyHz = 1500.0;

    // A real noise floor, not just a "reject digital silence" guard - during
    // an extended quiet stretch the adaptive mean+deviation below decays
    // toward zero right along with actual flux, and once both are near-zero
    // even tiny windowing/quantization artifacts from otherwise-sustained
    // content can exceed a purely relative threshold. This puts a hard floor
    // under the threshold so a real jump is still required no matter how
    // quiet things have been. Lowered from 0.08 to catch more real hits -
    // some extra sibilants slipping through is an acceptable trade.
    private const double MinimumFlux = 0.05;

    // How fast the running mean of flux adapts. Rise slower than fall, same
    // asymmetric reasoning as AdaptiveOnsetDetector: a hit should barely
    // drag the average up, but the average should snap back down between
    // hits so the threshold doesn't quietly rise to swallow future hits.
    private const double FluxAverageRiseSeconds = 1.5;
    private const double FluxAverageFallSeconds = 0.1;

    // A hop counts as a hit once flux clears the running mean by this many
    // standard deviations - the standard formulation for adaptive spectral
    // flux onset detection. Lowered from 2.2 for the same reason as
    // MinimumFlux above - more sensitivity to real hits, accepting some
    // extra sibilants as the trade.
    private const double FluxDeviationMultiplier = 1.8;

    // A genuine percussion hit is a transient - it decays back toward
    // silence before the next one - whereas a sustained vocal consonant
    // (an "s"/"sh") is closer to stationary noise, and noise has
    // continuously fluctuating magnitude frame to frame even while
    // perceptually steady. That fluctuation alone can cross a plain
    // level threshold multiple times within one syllable. Requiring flux to
    // fall back to this fraction of the threshold before arming for the next
    // hit demands the "quiet in between" a real percussive attack has, which
    // sustained frication generally doesn't provide. In a busy/loud real mix
    // (cymbal wash, reverb tails, other instruments sharing this frequency
    // range) flux never decays as far as it does for an isolated click in
    // silence, so this needs real headroom above 0.5 or genuinely fast real
    // hits stay un-rearmed and get silently dropped - a hair below "no
    // release required at all" is still enough to reject sustained noise's
    // frame-to-frame fluctuation, which rarely dips even this far.
    private const double HysteresisReleaseFraction = 0.7;

    // Genuinely fast buildups (32nd/64th notes at real tempos, which can
    // exceed 174 BPM) need well under 25ms between hits - that used to be
    // the refractory period, silently capping the detectable rate below what
    // real buildups reach and dropping hits exactly at the fastest, most
    // important part. 20ms is still comfortably above one FFT hop (~5.3ms)
    // so it can't double-fire on a single hit's own decay ripple.
    private const double HitRefractorySeconds = 0.02;

    // How many recent hits are considered when judging whether the pattern
    // is accelerating.
    private const int HitHistoryCount = 6;

    // Every gap between the tracked hits must be at least this fast - a
    // buildup is a flurry, not just a steady, moderate beat.
    private const double MaxHitGapSeconds = 0.45;

    // No hit for this long ends the buildup.
    private const double StaleAfterSeconds = 0.6;

    // The second half of the tracked gaps must average at most this
    // fraction of the first half's average. Close to 1.0 rather than
    // strictly demanding a sharp speedup - a sustained fast, roughly steady
    // run of hits should still read as "buildup", not just ones that are
    // visibly accelerating hit to hit.
    private const double AccelerationRatio = 0.95;

    private readonly float[] _ringBuffer = new float[FftSize];
    private readonly float[] _previousMagnitudes = new float[FftSize / 2];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private int _ringWritePos;
    private int _samplesSinceLastHop;
    private bool _hasPreviousFrame;

    private double _fluxAverage;
    private double _fluxAverageSquared;
    private DateTime _lastFluxHitTime = DateTime.MinValue;
    private bool _armedForNextHit = true;

    private readonly Queue<DateTime> _recentHits = new();
    private DateTime _lastHitTime = DateTime.MinValue;
    private DateTime? _lastQualifyingHitTime;
    private bool _basslineActive;

    /// <summary>Fires for every hit that counts toward an ongoing buildup (including the one that starts it).</summary>
    public event EventHandler<PercussionHitEventArgs>? Hit;

    public bool IsActive { get; private set; }

    /// <param name="basslineActive">
    /// Whether a steady bassline is currently holding down a groove (see
    /// <see cref="BasslineActivityTracker"/>) - a buildup never activates
    /// while one is, and any in-progress buildup ends the moment one starts.
    /// </param>
    public void Process(float[] samples, int sampleRate, bool basslineActive)
    {
        _basslineActive = basslineActive;

        if (basslineActive && IsActive)
        {
            IsActive = false;
            _recentHits.Clear();
        }

        foreach (var sample in samples)
        {
            _ringBuffer[_ringWritePos] = sample;
            _ringWritePos = (_ringWritePos + 1) % FftSize;
            _samplesSinceLastHop++;

            if (_samplesSinceLastHop >= HopSize)
            {
                _samplesSinceLastHop = 0;
                ProcessHop(sampleRate);
            }
        }

        CheckStale();
    }

    private void ProcessHop(int sampleRate)
    {
        for (var i = 0; i < FftSize; i++)
        {
            var index = (_ringWritePos + i) % FftSize;
            var windowed = _ringBuffer[index] * (float)FastFourierTransform.HammingWindow(i, FftSize);
            _fftBuffer[i].X = windowed;
            _fftBuffer[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FftPower, _fftBuffer);

        var minBin = (int)(MinBinFrequencyHz * FftSize / sampleRate);
        double flux = 0;

        for (var bin = 0; bin < FftSize / 2; bin++)
        {
            var real = _fftBuffer[bin].X;
            var imaginary = _fftBuffer[bin].Y;
            var magnitude = MathF.Sqrt(real * real + imaginary * imaginary);

            if (_hasPreviousFrame && bin >= minBin)
            {
                var diff = magnitude - _previousMagnitudes[bin];
                if (diff > 0)
                    flux += diff;
            }

            _previousMagnitudes[bin] = magnitude;
        }

        _hasPreviousFrame = true;

        var hopSeconds = HopSize / (double)sampleRate;
        var now = DateTime.UtcNow;

        // Same asymmetric-EMA approach as AdaptiveOnsetDetector, but tracked
        // directly on flux's own scale instead of reusing that class's
        // energy-scaled formula.
        var timeConstant = flux > _fluxAverage ? FluxAverageRiseSeconds : FluxAverageFallSeconds;
        var k = 1.0 - Math.Exp(-hopSeconds / timeConstant);
        _fluxAverage += k * (flux - _fluxAverage);
        _fluxAverageSquared += k * (flux * flux - _fluxAverageSquared);

        var variance = Math.Max(0, _fluxAverageSquared - _fluxAverage * _fluxAverage);
        var deviation = Math.Sqrt(variance);
        var threshold = _fluxAverage + FluxDeviationMultiplier * deviation;

        if (flux < threshold * HysteresisReleaseFraction)
            _armedForNextHit = true;

        var isPastRefractory = (now - _lastFluxHitTime).TotalSeconds > HitRefractorySeconds;

        if (!_basslineActive && _armedForNextHit && flux > MinimumFlux && flux > threshold && isPastRefractory)
        {
            _armedForNextHit = false;
            _lastFluxHitTime = now;
            RegisterHit(now);
        }
    }

    private void RegisterHit(DateTime now)
    {
        _lastHitTime = now;

        var gapFromPrevious = _lastQualifyingHitTime is { } prev ? (now - prev).TotalSeconds : (double?)null;
        _lastQualifyingHitTime = now;

        _recentHits.Enqueue(now);
        while (_recentHits.Count > HitHistoryCount)
            _recentHits.Dequeue();

        if (IsActive)
        {
            // Entering a buildup demands real evidence of speeding up (see
            // EvaluateBuildup) - that's what keeps a merely-fast-but-steady
            // beat from false-triggering. But re-demanding that same strict
            // test on every single hit meant a buildup died the instant its
            // tempo leveled off after a transition (e.g. straight sixteenth
            // notes, once no longer freshly speeding up within the window),
            // producing brief blips at each transition instead of one
            // continuous flash through the whole fast passage. Staying
            // active only requires hits to keep arriving fast - the
            // stricter check is for getting in, not for staying in.
            IsActive = gapFromPrevious is null or <= MaxHitGapSeconds;
            if (!IsActive)
                _recentHits.Clear();
        }
        else
        {
            IsActive = EvaluateBuildup();
        }

        if (IsActive)
            Hit?.Invoke(this, new PercussionHitEventArgs(gapFromPrevious ?? MaxHitGapSeconds));
    }

    private void CheckStale()
    {
        if (IsActive && (DateTime.UtcNow - _lastHitTime).TotalSeconds > StaleAfterSeconds)
        {
            IsActive = false;
            _recentHits.Clear();
        }
    }

    private bool EvaluateBuildup()
    {
        if (_recentHits.Count < HitHistoryCount)
            return false;

        var hits = _recentHits.ToArray();
        var gaps = new double[hits.Length - 1];
        for (var i = 0; i < gaps.Length; i++)
            gaps[i] = (hits[i + 1] - hits[i]).TotalSeconds;

        if (gaps.Any(gap => gap > MaxHitGapSeconds))
            return false;

        var half = gaps.Length / 2;
        var firstAverage = gaps.Take(half).Average();
        var secondAverage = gaps.Skip(gaps.Length - half).Average();

        return secondAverage <= firstAverage * AccelerationRatio;
    }
}

/// <summary>The time since the previous qualifying hit, so a flash can be timed to it.</summary>
public sealed class PercussionHitEventArgs : EventArgs
{
    public double GapSeconds { get; }

    public PercussionHitEventArgs(double gapSeconds)
    {
        GapSeconds = gapSeconds;
    }
}
