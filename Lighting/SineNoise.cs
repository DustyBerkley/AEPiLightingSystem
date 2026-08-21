using System;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Smooth, perfectly continuous 2D "noise" built from a couple of summed
/// sine waves rather than <see cref="SmoothNoise"/>'s interpolated lattice
/// hash. Value noise like <see cref="SmoothNoise"/> is only as smooth as its
/// interpolation between lattice cells, so sampling it fast along the time
/// axis (as the laser fan's gap/tilt animation does) can look jerky - each
/// cell's hashed value is independent of its neighbors, so the direction of
/// travel can change abruptly cell to cell. Sine waves have no cells and no
/// derivative discontinuities at any speed, so motion driven by this stays
/// visibly smooth (gentle acceleration/deceleration, no sudden direction
/// snaps) no matter how fast it's sampled. Deterministic per seed: the same
/// (seed, x, t) always produces the same value.
/// </summary>
public sealed class SineNoise
{
    private readonly double _phaseX;
    private readonly double _phaseT;
    private readonly double _secondaryFrequency;

    public SineNoise(int seed)
    {
        var random = new Random(seed);
        _phaseX = random.NextDouble() * Math.PI * 2;
        _phaseT = random.NextDouble() * Math.PI * 2;
        _secondaryFrequency = 0.6 + random.NextDouble() * 0.8;
    }

    /// <returns>A smoothly-varying value in [0, 1].</returns>
    public double Sample(double x, double t)
    {
        var v = Math.Sin(x + t + _phaseX) * 0.6
              + Math.Sin(x * 1.3 - t * _secondaryFrequency + _phaseT) * 0.4;
        return (v + 1.0) / 2.0;
    }
}
