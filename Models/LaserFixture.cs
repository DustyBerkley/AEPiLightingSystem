using System;
using System.Windows.Media;

namespace AEPiLightingSystem.Models;

/// <summary>
/// The 32-channel DMX laser. DMX channel mapping isn't wired up yet - for
/// now this only drives the on-screen visualization: a fan of beams, each
/// with its own color and vertical tilt so the fan can be broken up into
/// lava-lamp-style groups (varying brightness/gaps and tilt together)
/// rather than lighting as one uniform block. Beams always travel the same
/// distance - they're light, not something that can fall short at 10 or 20
/// feet - so only their angle (and therefore where they land) moves.
/// </summary>
public class LaserFixture
{
    public const int BeamCount = 35;

    public Color[] BeamColors { get; }

    /// <summary>Each beam's vertical tilt, in radians, applied around the projector point.</summary>
    public double[] BeamTiltRadians { get; }

    /// <summary>
    /// Raised after <see cref="BeamColors"/> and <see cref="BeamTiltRadians"/> have been updated in bulk, so views can redraw.
    /// </summary>
    public event EventHandler? BeamsChanged;

    public LaserFixture()
    {
        BeamColors = new Color[BeamCount];
        Array.Fill(BeamColors, Colors.Black);

        BeamTiltRadians = new double[BeamCount];
    }

    public void RaiseBeamsChanged() => BeamsChanged?.Invoke(this, EventArgs.Empty);
}
