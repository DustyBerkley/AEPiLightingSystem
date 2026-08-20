using System;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Smooth 2D value noise: pseudo-random values on an integer lattice,
/// interpolated with a quintic smoothstep so there are no visible grid
/// seams - similar in spirit to Perlin noise but much simpler. Deterministic
/// per seed: the same (seed, x, y) always produces the same value.
/// </summary>
public sealed class SmoothNoise
{
    private readonly int _seed;

    public SmoothNoise(int seed)
    {
        _seed = seed;
    }

    /// <returns>A smoothly-varying value in [0, 1).</returns>
    public double Sample(double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = x0 + 1;
        var y1 = y0 + 1;

        var tx = Smoothstep(x - x0);
        var ty = Smoothstep(y - y0);

        var v00 = Hash(x0, y0);
        var v10 = Hash(x1, y0);
        var v01 = Hash(x0, y1);
        var v11 = Hash(x1, y1);

        var ix0 = Lerp(v00, v10, tx);
        var ix1 = Lerp(v01, v11, tx);

        return Lerp(ix0, ix1, ty);
    }

    private double Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + _seed * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    private static double Smoothstep(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
