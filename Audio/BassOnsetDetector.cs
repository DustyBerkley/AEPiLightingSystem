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
    private const double AverageTimeConstantSeconds = 1.0;
    private const double RefractoryPeriodSeconds = 0.12;

    // A hit must be loud in absolute terms, not just louder than a quiet
    // background, or quiet bass would still spike the (adaptive) relative
    // threshold below. -20 dBFS RMS in the isolated bass band is a clearly
    // audible, prominent kick/sub - not just technically present.
    private const double MinimumBassRms = 0.1; // 10^(-20/20)
    private const double MinimumEnergyThreshold = MinimumBassRms * MinimumBassRms;

    private readonly int _sampleRate;
    private double _filterState1;
    private double _filterState2;
    private double _averageEnergy;
    private double _averageEnergySquared;
    private DateTime _lastHitTime = DateTime.MinValue;

    public event EventHandler? BassHitDetected;

    /// <summary>Most recent instantaneous bass-band energy, exposed for diagnostics/metering.</summary>
    public double LastEnergy { get; private set; }

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
        LastEnergy = instantEnergy;

        var bufferSeconds = (double)samples.Length / _sampleRate;
        var k = 1.0 - Math.Exp(-bufferSeconds / AverageTimeConstantSeconds);

        _averageEnergy += k * (instantEnergy - _averageEnergy);
        _averageEnergySquared += k * (instantEnergy * instantEnergy - _averageEnergySquared);

        var variance = Math.Max(0, _averageEnergySquared - _averageEnergy * _averageEnergy);

        // Classic energy-based onset heuristic: the more variable recent bass energy has
        // been, the higher above the average a hit must spike to count as a new onset.
        var sensitivity = Math.Clamp((-0.0025714 * variance) + 1.5142857, 1.05, 3.0);

        var isLoudEnough = instantEnergy > MinimumEnergyThreshold;
        var isAboveAdaptiveThreshold = instantEnergy > sensitivity * _averageEnergy;
        var isPastRefractoryPeriod = (DateTime.UtcNow - _lastHitTime).TotalSeconds > RefractoryPeriodSeconds;

        if (isLoudEnough && isAboveAdaptiveThreshold && isPastRefractoryPeriod)
        {
            _lastHitTime = DateTime.UtcNow;
            BassHitDetected?.Invoke(this, EventArgs.Empty);
        }
    }
}
