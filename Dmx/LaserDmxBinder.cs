using System;
using AEPiLightingSystem.Lighting;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Drives the Gruolin GL-L03 laser's real DMX output from
/// <see cref="LaserFixture.Groups"/>, using the fixture's 34-channel
/// "Professional" mode (set on the unit itself as address A.014). Each of
/// the fixture's two lenses has its own 17-channel block (channels 1-17
/// and 18-34), but per real-hardware testing (2026-08-22, via QLC+) both
/// lenses are driven identically from <see cref="LaserFixture.Groups"/>[0]
/// alone - block 2 just mirrors whatever block 1 ends up with, rather than
/// being derived from its own separate group. The on-screen simulation
/// still independently mirrors group 0 into group 1 for visual variety
/// (see <see cref="Lighting.LaserFanAnimator"/>); this class simply doesn't
/// use that second group for DMX output.
///
/// - Pattern Selection pinned to 19 (within the Line Gallery) - CONFIRMED
///   via direct QLC+ fader testing (2026-08-22) to draw a single horizontal
///   line.
/// - Horizontal Zoom controls that line's width; Horizontal Movement its
///   left/right position - both also confirmed the same way. Horizontal
///   Zoom's usable range is 0-50, not the full 0-127 static range - going
///   past 50 is what caused the beam to collapse toward a dot during
///   earlier tuning.
/// - Color Change values 128-159 (one of the "colorful auto-cycle" modes)
///   show no laser at all on this unit - confirmed and avoided entirely.
///   Color is instead mapped from each group's actual
///   <see cref="LaserGroup.HueDegrees"/> onto the nearest fixed color macro
///   (Red/Yellow/Green/Blue/Indigo/Pink).
/// - Pattern Scaling is animated slowly within its confirmed range,
///   45-115 - per real-hardware feedback, this also visibly moves the
///   pattern vertically as a side effect, so Vertical Movement doesn't need
///   (and no longer gets) any separate animation of its own; an earlier
///   attempt at driving Vertical Movement directly (alone, and previously
///   also tied to Scaling in a more complex combined animation) never
///   produced a working result, up to and including a total regression
///   that had to be fully reverted.
/// - Rotation is animated slowly within its confirmed range, 0-30. An
///   earlier attempt animated it around the wrong range (63+-8, i.e.
///   55-71) and made the laser stop working entirely - that range was never
///   actually verified, just assumed by analogy with the other "static
///   X, 0-127, centered at 63" channels, which turned out not to hold here.
/// - Chord mode (<see cref="LaserFixture.IsChordMode"/>) uses its own
///   confirmed-good values entirely separate from the wave/lava-lamp
///   animation above: Scaling/Rotation are fixed (not wandered), and
///   Horizontal/Vertical Movement each get a fresh random value picked once
///   per chord hit (<see cref="LaserFixture.ChordHitIndex"/>) from their own
///   confirmed ranges, rather than Horizontal Movement tracking
///   CenterFraction and Vertical Movement sitting at 0 the way they do in
///   wave mode.
///
/// Vertical Zoom, Force Tinting, Node Highlighting/Expansion/Gradient/
/// Distortion, Filter Selection, and Out-of-Bounds/Size are left alone
/// entirely (static safe defaults, never touched after startup) - these
/// haven't been individually verified and there's no reason to risk them.
/// </summary>
public sealed class LaserDmxBinder
{
    // Offsets within one 17-channel pattern block (see the manual's 34CH
    // Professional Mode table - block 2 repeats this exact layout at
    // BlockSize past block 1).
    private const int BlockSize = 17;

    private const int TotalSwitchOffset = 0;
    private const int OutOfBoundsSizeOffset = 1;
    private const int GallerySelectionOffset = 2;
    private const int PatternSelectionOffset = 3;
    private const int ScalingOffset = 4;
    private const int RotationOffset = 5;
    private const int HorizontalMovementOffset = 6;
    private const int VerticalMovementOffset = 7;
    private const int HorizontalZoomOffset = 8;
    private const int VerticalZoomOffset = 9;
    private const int ForceTintingOffset = 10;
    private const int ColorChangeOffset = 11;
    private const int NodeHighlightingOffset = 12;
    private const int NodeExpansionOffset = 13;
    private const int GradientOffset = 14;
    private const int DistortionOffset = 15;
    private const int FilterSelectionOffset = 16;

    private const byte LineGalleryValue = 255;

    // CONFIRMED: draws a single horizontal line.
    private const byte HorizontalLinePatternIndex = 19;

    private const byte CenteredStaticValue = 63;

    // How far Horizontal Movement swings either side of center for
    // LaserGroup.CenterFraction = +-1 - raw DMX units, no confirmed
    // degrees-per-unit figure for this channel. Narrowed from an initial
    // 63 after real-hardware feedback that the beams' horizontal spread
    // was too wide.
    private const double HorizontalMoveHalfRange = 35.0;

    // CONFIRMED: Horizontal Zoom's real usable range - not the full 0-127
    // static span. A fully-wide group (WidthFraction = 1) uses the top of
    // this range.
    private const double HorizontalZoomMax = 50.0;

    // CONFIRMED: Rotation's animated range for the normal wave/lava-lamp
    // effect.
    private const double RotationMin = 0.0;
    private const double RotationMax = 30.0;

    // CONFIRMED: Pattern Scaling's animated range for the normal wave/
    // lava-lamp effect - also visibly moves the pattern vertically as a
    // side effect, so this alone covers what the earlier separate Vertical
    // Movement animation was trying to do.
    private const double ScalingMin = 45.0;
    private const double ScalingMax = 115.0;

    // CONFIRMED chord-mode baseline for Scaling/Rotation - deliberately
    // kept separate from the wave/lava-lamp animation's own ranges above
    // rather than sharing them, since a chord is a single static "beam"
    // rather than a continuously wandering line and real-hardware testing
    // confirmed good values well outside the wave animation's own range
    // (this is a starting point to keep iterating from, not necessarily
    // final).
    private const byte ChordScalingValue = 125;
    private const byte ChordRotationValue = 123;

    // The 2nd manual laser fader effect's own confirmed ranges for
    // Rotation/Horizontal Movement/Vertical Zoom - deliberately separate
    // from the wave/lava-lamp effect's own RotationMin/Max and
    // HorizontalMoveHalfRange above, and from Vertical Zoom's usual static
    // 0 - see LaserFixture.IsSecondEffectActive.
    private const double SecondEffectRotationMin = 15.0;
    private const double SecondEffectRotationMax = 47.0;
    private const double SecondEffectHorizontalMin = 57.0;
    private const double SecondEffectHorizontalMax = 91.0;
    private const double SecondEffectVerticalZoomMin = 0.0;
    private const double SecondEffectVerticalZoomMax = 60.0;

    // CONFIRMED chord-mode ranges for Horizontal/Vertical Movement - a
    // fresh value is picked from each range once per chord hit (see
    // LaserFixture.ChordHitIndex), independent of the wave animation's own
    // CenterFraction-driven position and always-0 Vertical Movement.
    private const int ChordHorizontalMin = 44;
    private const int ChordHorizontalMax = 78;
    private const int ChordVerticalMin = 33;
    private const int ChordVerticalMax = 67;

    // Slow pace for Scaling's wander - a "lava lamp" pace, not active
    // movement.
    private const double EvolveSpeed = 0.12;

    // Rotation gets its own faster (but still not quick) pace, separate
    // from Scaling, per real-hardware feedback.
    private const double RotationEvolveSpeed = 0.22;

    // Color Change (128-159 is a dead zone on this unit - see class
    // summary) fixed-macro sub-ranges and a representative value in each,
    // paired with an approximate hue center for nearest-match lookup.
    private static readonly (double HueDegrees, byte ChannelValue)[] ColorMacros =
    [
        (0.0, 12),    // Red (8-15)
        (60.0, 20),   // Yellow (16-23)
        (120.0, 28),  // Green (24-31)
        (240.0, 44),  // Blue (40-47)
        (275.0, 36),  // Indigo (32-39)
        (330.0, 52),  // Pink (48-55)
    ];

    private readonly LaserFixture _laser;
    private readonly DmxUniverse _universe;
    private readonly int _baseChannel;

    private readonly SineNoise _scalingNoise;
    private readonly SineNoise _rotationNoise;
    private readonly Random _random;
    private double _time;
    private DateTime _lastAnimTime = DateTime.MinValue;

    private int _lastSeenChordHitIndex = -1;
    private byte _chordHorizontalValue;
    private byte _chordVerticalValue;

    public LaserDmxBinder(LaserFixture laser, DmxUniverse universe, int baseChannel = 1)
    {
        _laser = laser;
        _universe = universe;
        _baseChannel = baseChannel;

        _random = new Random();
        _scalingNoise = new SineNoise(_random.Next());
        _rotationNoise = new SineNoise(_random.Next());

        for (var i = 0; i < LaserFixture.GroupCount; i++)
            InitStaticChannels(_baseChannel + i * BlockSize);

        ApplyAll();
        laser.GroupsChanged += (_, _) => ApplyAll();
    }

    private void InitStaticChannels(int block)
    {
        _universe.SetChannel(block + GallerySelectionOffset, LineGalleryValue);
        _universe.SetChannel(block + PatternSelectionOffset, HorizontalLinePatternIndex);
        // Vertical Zoom is NOT initialized here even though it's usually 0 -
        // the 2nd manual laser effect animates it, so it's written every
        // ApplyAll() call instead (see below), the same as every other
        // channel that isn't always static.
        _universe.SetChannel(block + ForceTintingOffset, 0);
        _universe.SetChannel(block + NodeHighlightingOffset, 0);
        _universe.SetChannel(block + NodeExpansionOffset, 0);
        _universe.SetChannel(block + GradientOffset, 0);
        _universe.SetChannel(block + DistortionOffset, 0);
        _universe.SetChannel(block + FilterSelectionOffset, 0);
        _universe.SetChannel(block + OutOfBoundsSizeOffset, 0);
    }

    private void ApplyAll()
    {
        var now = DateTime.UtcNow;
        var dt = _lastAnimTime == DateTime.MinValue ? 0.0 : (now - _lastAnimTime).TotalSeconds;
        _lastAnimTime = now;
        _time += dt;

        var group = _laser.Groups[0];

        // Scaling wanders the same "default" way regardless of mode (chord
        // mode's own fixed baseline aside, applied below) - shared by both
        // the normal wave path and the 2nd manual laser effect, which
        // doesn't touch this channel.
        var scalingNoise01 = _scalingNoise.Sample(0.0, _time * EvolveSpeed);
        var scalingValue = ScalingMin + scalingNoise01 * (ScalingMax - ScalingMin);
        var scaling = (byte)Math.Round(Math.Clamp(scalingValue, 0.0, 127.0));

        byte rotation, horizontalShift, verticalMovement, verticalZoom, colorMacro;
        bool isOn;

        if (_laser.IsChordMode)
        {
            // Chord mode is driven by the automated system, not the manual
            // laser faders, and keeps its own original on/off and color
            // rule (whether TriggerChordHit has actually painted a color
            // into the group) rather than the manual-fader composition
            // below - it can be active independently of
            // LaserFixture.IsWaveEffectActive.
            isOn = group.Color.R > 0 || group.Color.G > 0 || group.Color.B > 0;
            colorMacro = NearestColorMacro(group.HueDegrees);

            // Chord mode: fixed confirmed-good baseline, not wandered -
            // see class summary. Horizontal/Vertical Movement get a fresh
            // random value once per chord hit rather than every tick.
            scaling = ChordScalingValue;
            rotation = ChordRotationValue;

            if (_laser.ChordHitIndex != _lastSeenChordHitIndex)
            {
                _lastSeenChordHitIndex = _laser.ChordHitIndex;
                _chordHorizontalValue = (byte)_random.Next(ChordHorizontalMin, ChordHorizontalMax + 1);
                _chordVerticalValue = (byte)_random.Next(ChordVerticalMin, ChordVerticalMax + 1);
            }

            horizontalShift = _chordHorizontalValue;
            verticalMovement = _chordVerticalValue;
            verticalZoom = 0;
        }
        else if (_laser.IsSecondEffectActive)
        {
            // The wave effect (1st laser fader) and the 2nd manual laser
            // effect are independent - either can be on with the other off,
            // and both being on at once composes rather than one
            // suppressing the other: whichever one wants the fixture on
            // wins that vote, and color prefers the wave effect's own hue
            // whenever it's actually contributing, falling back to the 2nd
            // effect's own hue only when the wave effect isn't lit (so the
            // 2nd effect still reads correctly used entirely on its own).
            // IsWaveEffectActive flips the instant the fader wants it off,
            // not once its own cosmetic fade finishes - see
            // LaserFixture.IsWaveEffectActive.
            //
            // Rotation/Horizontal Movement/Vertical Zoom get their own
            // independently-wandered values (see LaserSecondEffectAnimator)
            // within their own confirmed ranges - a physical DMX channel
            // can only carry one value at a time, so this effect takes
            // priority over the wave effect's own formulas for just these
            // 3 channels while it's active. Everything else (Scaling above,
            // Horizontal Zoom below) keeps composing with the wave effect
            // normally.
            isOn = true;
            colorMacro = _laser.IsWaveEffectActive
                ? NearestColorMacro(group.HueDegrees)
                : NearestColorMacro(_laser.SecondEffectHueDegrees);

            var rotationValue = SecondEffectRotationMin + _laser.SecondEffectRotationFraction * (SecondEffectRotationMax - SecondEffectRotationMin);
            rotation = (byte)Math.Round(Math.Clamp(rotationValue, 0.0, 127.0));

            var horizontalValue = SecondEffectHorizontalMin + _laser.SecondEffectHorizontalFraction * (SecondEffectHorizontalMax - SecondEffectHorizontalMin);
            horizontalShift = (byte)Math.Round(Math.Clamp(horizontalValue, 0.0, 127.0));

            var verticalZoomValue = SecondEffectVerticalZoomMin + _laser.SecondEffectVerticalZoomFraction * (SecondEffectVerticalZoomMax - SecondEffectVerticalZoomMin);
            verticalZoom = (byte)Math.Round(Math.Clamp(verticalZoomValue, 0.0, 127.0));

            verticalMovement = 0;
        }
        else
        {
            // Neither chord mode nor the 2nd manual laser effect - plain
            // wave path (or fully off, if the wave effect isn't active
            // either). IsWaveEffectActive flips the instant the fader/
            // automation wants it off - see LaserFixture.IsWaveEffectActive.
            isOn = _laser.IsWaveEffectActive;
            colorMacro = NearestColorMacro(group.HueDegrees);

            var rotationNoise01 = _rotationNoise.Sample(0.0, _time * RotationEvolveSpeed);
            var rotationValue = RotationMin + rotationNoise01 * (RotationMax - RotationMin);
            rotation = (byte)Math.Round(Math.Clamp(rotationValue, 0.0, 127.0));

            var horizontalShiftValue = CenteredStaticValue + group.CenterFraction * HorizontalMoveHalfRange;
            horizontalShift = (byte)Math.Clamp(horizontalShiftValue, 0.0, 127.0);
            verticalMovement = 0;
            verticalZoom = 0;
        }

        var zoom = Math.Clamp(group.WidthFraction, 0.0, 1.0) * HorizontalZoomMax;
        zoom = Math.Clamp(zoom, 0.0, HorizontalZoomMax);

        for (var i = 0; i < LaserFixture.GroupCount; i++)
        {
            var block = _baseChannel + i * BlockSize;
            _universe.SetChannel(block + TotalSwitchOffset, isOn ? (byte)255 : (byte)0);
            _universe.SetChannel(block + HorizontalMovementOffset, horizontalShift);
            _universe.SetChannel(block + VerticalMovementOffset, verticalMovement);
            _universe.SetChannel(block + HorizontalZoomOffset, (byte)zoom);
            _universe.SetChannel(block + VerticalZoomOffset, verticalZoom);
            _universe.SetChannel(block + ColorChangeOffset, colorMacro);
            _universe.SetChannel(block + ScalingOffset, scaling);
            _universe.SetChannel(block + RotationOffset, rotation);
        }
    }

    private static byte NearestColorMacro(double hueDegrees)
    {
        hueDegrees = ((hueDegrees % 360.0) + 360.0) % 360.0;

        var best = ColorMacros[0];
        var bestDistance = CircularHueDistance(hueDegrees, best.HueDegrees);

        foreach (var macro in ColorMacros)
        {
            var distance = CircularHueDistance(hueDegrees, macro.HueDegrees);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = macro;
            }
        }

        return best.ChannelValue;
    }

    private static double CircularHueDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return Math.Min(diff, 360.0 - diff);
    }
}
