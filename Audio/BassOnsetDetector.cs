using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Detects bass "hits" (kick drums, sub-bass onsets) from a stream of mono
/// audio buffers: a cascaded low-pass filter isolates the bass band, then an
/// adaptive energy threshold flags sudden spikes above the recent average.
/// </summary>
public sealed class BassOnsetDetector
{
    private const double CutoffHz = 150.0;

    // A hit must be loud in absolute terms, not just louder than a quiet
    // background, or quiet bass would still spike the (adaptive) relative
    // threshold below. -20 dBFS RMS in the isolated bass band is a clearly
    // audible, prominent kick/sub - not just technically present.
    private const double MinimumBassRms = 0.1; // 10^(-20/20)

    private readonly int _sampleRate;
    private readonly AdaptiveOnsetDetector _onset = new(MinimumBassRms * MinimumBassRms);
    private double _filterState1;
    private double _filterState2;

    public event EventHandler? BassHitDetected;

    /// <summary>Most recent instantaneous bass-band energy, exposed for diagnostics/metering.</summary>
    public double LastEnergy => _onset.LastEnergy;

    public BassOnsetDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Process(float[] samples)
    {
        if (samples.Length == 0)
            return;

        var alpha = 1.0 - Math.Exp(-2.0 * Math.PI * CutoffHz / _sampleRate);
        double sumSquares = 0;

        foreach (var sample in samples)
        {
            // Two cascaded one-pole low-pass filters for a steeper bass-band rolloff.
            _filterState1 += alpha * (sample - _filterState1);
            _filterState2 += alpha * (_filterState1 - _filterState2);
            sumSquares += _filterState2 * _filterState2;
        }

        var instantEnergy = sumSquares / samples.Length;
        var bufferSeconds = (double)samples.Length / _sampleRate;

        if (_onset.ProcessEnergy(instantEnergy, bufferSeconds))
            BassHitDetected?.Invoke(this, EventArgs.Empty);
    }
}
