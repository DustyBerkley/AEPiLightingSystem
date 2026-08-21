using System;
using NAudio.Dsp;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Picks out sharp, prominent chord hits from the quieter background
/// melodic texture that's also present during a melodic passage (both look
/// identical to <see cref="MelodicPassageDetector"/> - sustained mid/high
/// energy, no percussion, no bassline). Attacks are found via spectral
/// flux, the same family of technique <see cref="PercussionBuildupDetector"/>
/// uses, but compared against a slowly-following reference spectrum (an
/// EMA) rather than the immediately previous hop: a chord/pad attack can
/// ramp in over 100-300ms, far too gradual to show up as a spike between
/// two ~5ms hops, but shows up clearly as a sustained gap against a
/// reference that takes a few hundred milliseconds to catch up.
///
/// What distinguishes a real chord hit from background texture turned out -
/// checked against a real reference track - to be prominence, not timing:
/// an adaptive mean+deviation threshold (see <see cref="ProcessHop"/>)
/// already flags anything above the ambient noise floor, but background
/// melodic noise clears that same bar just as
/// often as real chord hits do - pace between attacks didn't reliably tell
/// them apart either, since a delay effect echoing each real chord lands at
/// a similar cadence to the background texture's own fluctuation. What does
/// separate them cleanly is raw size: real chord hits measured roughly
/// double the gap magnitude of anything in the background cluster. Hence
/// <see cref="SharpGapMinimum"/> - a second, much higher bar on top of the
/// adaptive one - and once something clears it, <see cref="IsSustainedChord"/>
/// stays true (further hits just refresh it and re-fire
/// <see cref="ChordOnset"/>) until nothing sharp arrives for
/// <see cref="StaleAfterSeconds"/>, i.e. until the progression has audibly
/// faded into just the background texture (or the melodic passage itself
/// ends, e.g. a bassline starting).
/// </summary>
public sealed class ChordSustainDetector
{
    private const int FftSize = 1024;
    private const int FftPower = 10; // log2(FftSize)
    private const int HopSize = 256;

    // Matches MelodicPassageDetector's highpass cutoff, so "onset" here
    // means an attack in the same frequency range that detector already
    // calls melody.
    private const double MinBinFrequencyHz = 400.0;

    // How far the reference spectrum lags behind the real one - long enough
    // that even a gradual pad/chord attack outruns it for a while, short
    // enough that it's fully caught up (ready to catch the next attack)
    // well before a musically plausible next chord change.
    private const double ReferenceLagSeconds = 0.25;

    // Same asymmetric mean+deviation adaptive threshold approach as
    // PercussionBuildupDetector, tracked on this detector's own gap signal
    // (a different scale than either that class's flux or
    // AdaptiveOnsetDetector's RMS-squared energy). This alone only rejects
    // silence/near-silence - see SharpGapMinimum for what actually rejects
    // background texture.
    private const double GapAverageRiseSeconds = 1.5;
    private const double GapAverageFallSeconds = 0.3;
    private const double GapDeviationMultiplier = 2.0;
    private const double MinimumGap = 0.03;
    private const double HysteresisReleaseFraction = 0.5;

    // The real chord-vs-background discriminator. Measured against a real
    // reference track: background melodic texture's own attacks landed
    // roughly 0.06-0.14 in this detector's gap units, while genuine sharp
    // chord hits landed at 0.25+ - comfortably more than double. Sitting
    // this partway between the two (rather than right at the background
    // ceiling) leaves margin for a track that's a little busier/noisier
    // than the one this was tuned against.
    private const double SharpGapMinimum = 0.19;

    // Just long enough that one attack's own multi-hop gap pulse can't
    // register as two hits.
    private const double OnsetRefractorySeconds = 0.15;

    // How long a sustained-chord state survives with no further sharp hit -
    // long enough to span the pause between individual chords in the same
    // progression (measured around 3-4.5s apart on the reference track),
    // short enough to actually end once the progression has genuinely faded
    // into background texture rather than lingering indefinitely.
    private const double StaleAfterSeconds = 6.0;

    private readonly float[] _ringBuffer = new float[FftSize];
    private readonly float[] _referenceMagnitudes = new float[FftSize / 2];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private int _ringWritePos;
    private int _samplesSinceLastHop;
    private bool _hasReference;

    private double _gapAverage;
    private double _gapAverageSquared;
    private DateTime _lastOnsetTime = DateTime.MinValue;
    private DateTime _lastSharpHitTime = DateTime.MinValue;
    private bool _armedForNextOnset = true;

    /// <summary>Fires each time a sharp chord hit is detected while the melodic passage is active.</summary>
    public event EventHandler? ChordOnset;

    /// <summary>True from a sharp chord hit until none arrive for <see cref="StaleAfterSeconds"/> or the melodic passage ends.</summary>
    public bool IsSustainedChord { get; private set; }

    public void Process(float[] samples, int sampleRate, bool melodicPassageActive)
    {
        foreach (var sample in samples)
        {
            _ringBuffer[_ringWritePos] = sample;
            _ringWritePos = (_ringWritePos + 1) % FftSize;
            _samplesSinceLastHop++;

            if (_samplesSinceLastHop >= HopSize)
            {
                _samplesSinceLastHop = 0;
                ProcessHop(sampleRate, melodicPassageActive);
            }
        }

        if (!melodicPassageActive)
            IsSustainedChord = false;
    }

    private void ProcessHop(int sampleRate, bool melodicPassageActive)
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
        var hopSeconds = HopSize / (double)sampleRate;
        var referenceAlpha = 1.0 - Math.Exp(-hopSeconds / ReferenceLagSeconds);

        double gap = 0;

        for (var bin = 0; bin < FftSize / 2; bin++)
        {
            var real = _fftBuffer[bin].X;
            var imaginary = _fftBuffer[bin].Y;
            var magnitude = MathF.Sqrt(real * real + imaginary * imaginary);

            if (_hasReference && bin >= minBin)
            {
                var diff = magnitude - _referenceMagnitudes[bin];
                if (diff > 0)
                    gap += diff;
            }

            _referenceMagnitudes[bin] += (float)(referenceAlpha * (magnitude - _referenceMagnitudes[bin]));
        }

        _hasReference = true;

        var now = DateTime.UtcNow;

        var timeConstant = gap > _gapAverage ? GapAverageRiseSeconds : GapAverageFallSeconds;
        var k = 1.0 - Math.Exp(-hopSeconds / timeConstant);
        _gapAverage += k * (gap - _gapAverage);
        _gapAverageSquared += k * (gap * gap - _gapAverageSquared);

        var variance = Math.Max(0, _gapAverageSquared - _gapAverage * _gapAverage);
        var deviation = Math.Sqrt(variance);
        var threshold = _gapAverage + GapDeviationMultiplier * deviation;

        if (gap < threshold * HysteresisReleaseFraction)
            _armedForNextOnset = true;

        var isPastRefractory = (now - _lastOnsetTime).TotalSeconds > OnsetRefractorySeconds;

        if (melodicPassageActive && _armedForNextOnset && gap > MinimumGap && gap > threshold && isPastRefractory)
        {
            _armedForNextOnset = false;
            _lastOnsetTime = now;

            if (gap > SharpGapMinimum)
            {
                _lastSharpHitTime = now;
                IsSustainedChord = true;
                ChordOnset?.Invoke(this, EventArgs.Empty);
            }
        }

        if (IsSustainedChord && (now - _lastSharpHitTime).TotalSeconds > StaleAfterSeconds)
            IsSustainedChord = false;
    }
}
