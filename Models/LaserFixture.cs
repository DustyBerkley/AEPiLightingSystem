using System;
using System.Windows.Media;

namespace AEPiLightingSystem.Models;

/// <summary>
/// The 34-channel DMX laser - a 2-lens galvo projector where each lens
/// draws exactly one shape from its own built-in pattern gallery at a time.
/// Confirmed via direct hardware testing (2026-08-22): pattern index 19
/// within the Line Gallery draws a horizontal line, with its width
/// controlled by a separate zoom channel. The model reflects that directly
/// - exactly <see cref="GroupCount"/> groups, one per lens, each a single
/// horizontal line with its own position, width, and color - rather than a
/// fantasy of individually addressable beams the hardware could never
/// actually draw (an earlier design modeled 35 independent beams, which
/// never had a real DMX equivalent).
/// </summary>
public class LaserFixture
{
    public const int GroupCount = 2;

    public LaserGroup[] Groups { get; }

    /// <summary>
    /// Whether the fixture is currently showing a single sustained-chord
    /// "beam" rather than the normal wandering wave - some DMX channels
    /// (see <see cref="Dmx.LaserDmxBinder"/>) use different confirmed-good
    /// values depending on which is active, since a chord's static single
    /// line and the wave's continuously wandering one don't share the same
    /// real-hardware calibration.
    /// </summary>
    public bool IsChordMode { get; set; }

    /// <summary>
    /// Increments once per chord attack (see
    /// <see cref="Lighting.LaserFanAnimator.TriggerChordHit"/>) - a
    /// real-hardware DMX consumer that wants a fresh random value picked
    /// specifically "once per new chord" (as opposed to every render frame)
    /// can watch for this to change rather than reacting to every
    /// <see cref="GroupsChanged"/> tick.
    /// </summary>
    public int ChordHitIndex { get; set; }

    /// <summary>
    /// Whether the wave/lava-lamp effect (1st manual laser fader, or the
    /// automated system) is actually wanted on right now - set immediately
    /// on activate/deactivate by <see cref="Lighting.LaserFanAnimator"/>,
    /// deliberately NOT tied to its own smooth intensity fade-out, so
    /// <see cref="Dmx.LaserDmxBinder"/> can flip the fixture fully off the
    /// instant the fader/automation actually wants it off rather than
    /// waiting through that cosmetic fade - real laser hardware has no
    /// variable-brightness channel to fade in the first place, so that
    /// delay was pure lag with no payoff.
    /// </summary>
    public bool IsWaveEffectActive { get; set; }

    /// <summary>
    /// Whether the 2nd manual laser fader effect (see
    /// <see cref="Lighting.LaserSecondEffectAnimator"/>) is currently
    /// driving the fixture - when true, <see cref="Dmx.LaserDmxBinder"/>
    /// takes Rotation/Horizontal Movement/Vertical Zoom from the
    /// SecondEffect* properties below (their own confirmed ranges) and the
    /// color from <see cref="SecondEffectHueDegrees"/>, instead of the
    /// normal wave/lava-lamp group-driven values - every other channel stays
    /// exactly the same either way.
    /// </summary>
    public bool IsSecondEffectActive { get; set; }

    /// <summary>0-1 wander driving Rotation's confirmed 15-47 DMX range for the 2nd manual laser effect.</summary>
    public double SecondEffectRotationFraction { get; set; }

    /// <summary>0-1 wander driving Horizontal Movement's confirmed 57-91 DMX range for the 2nd manual laser effect.</summary>
    public double SecondEffectHorizontalFraction { get; set; }

    /// <summary>0-1 wander driving Vertical Zoom's confirmed 0-60 DMX range for the 2nd manual laser effect.</summary>
    public double SecondEffectVerticalZoomFraction { get; set; }

    /// <summary>Hue behind the 2nd manual laser effect's own Color Change channel - picked once per activation, same as the wave effect's own hue.</summary>
    public double SecondEffectHueDegrees { get; set; }

    /// <summary>Raised after every group has been updated in bulk, so views can redraw.</summary>
    public event EventHandler? GroupsChanged;

    public LaserFixture()
    {
        Groups = new LaserGroup[GroupCount];
        for (var i = 0; i < GroupCount; i++)
            Groups[i] = new LaserGroup();
    }

    public void RaiseGroupsChanged() => GroupsChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One lens's current output: a single horizontal line, positioned and sized within the fixture's usable span.</summary>
public sealed class LaserGroup
{
    /// <summary>Horizontal center, -1 (far left) to 1 (far right), 0 = straight ahead.</summary>
    public double CenterFraction { get; set; }

    /// <summary>Line width as a fraction of the fixture's full usable span, 0-1.</summary>
    public double WidthFraction { get; set; }

    public Color Color { get; set; } = Colors.Black;

    /// <summary>
    /// The raw hue (degrees, [0, 360)) behind <see cref="Color"/> - kept
    /// alongside the already-converted RGB so a real-hardware consumer that
    /// only has fixed color macros to choose from (see
    /// <see cref="Dmx.LaserDmxBinder"/>) can pick the nearest one directly,
    /// without reverse-converting RGB back to HSL.
    /// </summary>
    public double HueDegrees { get; set; }

    /// <summary>Small vertical wobble in radians, on top of the fixture's fixed ceiling-mount aim.</summary>
    public double TiltRadians { get; set; }
}
