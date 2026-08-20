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
    private readonly double _averageRiseTimeConstantSeconds;
    private readonly double _averageFallTimeConstantSeconds;
    private readonly double _refractoryPeriodSeconds;

    private double _averageEnergy;
    private double _averageEnergySquared;
    private DateTime _lastHitTime = DateTime.MinValue;

    /// <summary>Most recent instantaneous energy, exposed for diagnostics/metering.</summary>
    public double LastEnergy { get; private set; }

    public AdaptiveOnsetDetector(
        double minimumEnergyThreshold,
        double averageRiseTimeConstantSeconds = 2.5,
        double averageFallTimeConstantSeconds = 0.15,
        double refractoryPeriodSeconds = 0.12)
    {
        _minimumEnergyThreshold = minimumEnergyThreshold;
        _averageRiseTimeConstantSeconds = averageRiseTimeConstantSeconds;
        _averageFallTimeConstantSeconds = averageFallTimeConstantSeconds;
        _refractoryPeriodSeconds = refractoryPeriodSeconds;
    }

    /// <returns>true if this energy sample is a new onset.</returns>
    public bool ProcessEnergy(double instantEnergy, double bufferSeconds)
    {
        LastEnergy = instantEnergy;

        // Rise slowly but fall quickly: a loud hit should barely drag the
        // average up, but the average should snap back down to the quiet
        // floor between hits. With a single symmetric time constant, sustained
        // playing (a steady bassline that never fully goes quiet) creeps the
        // average up toward the peak level itself over time, so hits stop
        // clearing the adaptive threshold - the detector quietly quenches
        // itself the longer music keeps playing. Asymmetric tracking keeps
        // headroom between the floor and the peaks indefinitely.
        var timeConstant = instantEnergy > _averageEnergy
            ? _averageRiseTimeConstantSeconds
            : _averageFallTimeConstantSeconds;
        var k = 1.0 - Math.Exp(-bufferSeconds / timeConstant);
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
