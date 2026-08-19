using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Reusable core of an energy-based onset detector: feed it a stream of
/// instantaneous energy values (already isolated to whatever band matters -
/// bass, full spectrum, etc.) and it flags a "hit" when energy both clears an
/// absolute loudness floor and spikes well above the recent adaptive average,
/// with a refractory period so one hit doesn't retrigger while it decays.
/// </summary>
public sealed class AdaptiveOnsetDetector
{
    private readonly double _minimumEnergyThreshold;
    private readonly double _averageTimeConstantSeconds;
    private readonly double _refractoryPeriodSeconds;

    private double _averageEnergy;
    private double _averageEnergySquared;
    private DateTime _lastHitTime = DateTime.MinValue;

    /// <summary>Most recent instantaneous energy, exposed for diagnostics/metering.</summary>
    public double LastEnergy { get; private set; }

    public AdaptiveOnsetDetector(
        double minimumEnergyThreshold,
        double averageTimeConstantSeconds = 1.0,
        double refractoryPeriodSeconds = 0.12)
    {
        _minimumEnergyThreshold = minimumEnergyThreshold;
        _averageTimeConstantSeconds = averageTimeConstantSeconds;
        _refractoryPeriodSeconds = refractoryPeriodSeconds;
    }

    /// <returns>true if this energy sample is a new onset.</returns>
    public bool ProcessEnergy(double instantEnergy, double bufferSeconds)
    {
        LastEnergy = instantEnergy;

        var k = 1.0 - Math.Exp(-bufferSeconds / _averageTimeConstantSeconds);
        _averageEnergy += k * (instantEnergy - _averageEnergy);
        _averageEnergySquared += k * (instantEnergy * instantEnergy - _averageEnergySquared);

        var variance = Math.Max(0, _averageEnergySquared - _averageEnergy * _averageEnergy);

        // Classic energy-based onset heuristic: the more variable recent energy has
        // been, the higher above the average a hit must spike to count as a new onset.
        var sensitivity = Math.Clamp((-0.0025714 * variance) + 1.5142857, 1.05, 3.0);

        var isLoudEnough = instantEnergy > _minimumEnergyThreshold;
        var isAboveAdaptiveThreshold = instantEnergy > sensitivity * _averageEnergy;
        var isPastRefractoryPeriod = (DateTime.UtcNow - _lastHitTime).TotalSeconds > _refractoryPeriodSeconds;

        if (!isLoudEnough || !isAboveAdaptiveThreshold || !isPastRefractoryPeriod)
            return false;

        _lastHitTime = DateTime.UtcNow;
        return true;
    }
}
