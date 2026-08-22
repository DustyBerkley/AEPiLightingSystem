using System;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Drives the Gruolin GL-L03 laser's real DMX output from the on-screen
/// <see cref="LaserFixture"/> simulation, using the fixture's 34-channel
/// "Professional" mode (set on the unit itself as address A.001 - see the
/// printed/PDF manual's DMX512 Control Mode table).
///
/// The hardware doesn't work like the simulation. <see cref="LaserFanAnimator"/>
/// (and <see cref="Controls.LaserPanelView"/>) model the fan as
/// <see cref="LaserFixture.BeamCount"/> independently colored/tilted beams,
/// which is a convenient way to draw a lava-lamp-style effect on screen, but
/// the real unit is a 2-lens galvo projector that draws one vector pattern
/// per lens from a built-in gallery (selected via DMX, not drawn point by
/// point) - it can't light 35 independent beams. So rather than a 1:1
/// mapping, each lens is driven by an *aggregate* of one half of the
/// simulated fan: the 34 channels split into two identical 17-channel
/// blocks (channels 1-17 and 18-34 - confirmed from the manual's channel
/// table, which repeats the same 17-function layout twice), one per lens,
/// matching the fan's existing left-half-simulated/right-half-mirrored
/// design.
///
/// For each half: whether it's on comes from whether any beam in it has any
/// brightness; the vertical-movement channel follows a brightness-weighted
/// average of that half's beam tilt (approximating "which way this half's
/// light is currently panned"); color is left on the fixture's own
/// "colorful color change" auto-cycle rather than mapped from the sim's
/// per-beam hue, since these channels only select fixed/cycling color
/// macros, not arbitrary RGB. Everything else (pattern gallery, scaling,
/// rotation, zoom, distortion, etc.) is pinned to a static default - most
/// importantly <see cref="PatternIndex"/>, which is a placeholder. The
/// manual documents which *ranges* of the pattern-select channel mean
/// "animation gallery" vs. "line gallery" but not which specific value
/// within a gallery draws which shape (there's no index chart for it) - so
/// which value actually draws something fan-like has to be found by
/// watching the real beam and adjusting this constant.
/// </summary>
public sealed class LaserDmxBinder
{
    // Offsets within one 17-channel pattern block (see the manual's 34CH
    // Professional Mode table - block 2 repeats this exact layout at
    // BlockSize past block 1, except its GallerySelection slot (channel 20)
    // is documented as "no function reserve" - harmless to still write.
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

    // CH3/CH20 (gallery select): manual says 0-239 = Animation Gallery,
    // 240-255 = Line Gallery. The fan/wave/zoom/dot/flip patterns from the
    // product listing read as "Line" effects, so pin to the top of that
    // range.
    private const byte LineGalleryValue = 255;

    // CH4/CH21 (pattern-within-gallery select, 0-255): PLACEHOLDER. No
    // index chart exists for what each value actually draws - this needs to
    // be tuned by hand while watching the real beam. Starts near the high
    // end of the range on the theory that fan/wave-type line effects are as
    // likely as anything else to live there, but treat this as a starting
    // guess, not a confirmed value.
    private const byte PatternIndex = 250;

    // CH5/CH22 (pattern scaling): 0-127 = static size. Mid-high so the
    // pattern reads at a reasonably full size without tipping into the
    // dynamic-zoom ranges (128+).
    private const byte StaticScaleValue = 100;

    // CH6/CH23, CH7/CH24, CH9/CH25... "static X" is 0-127 with the dynamic
    // ranges starting at 128 - centering at 63 keeps rotation/horizontal
    // pan/zoom parked in the middle of their static range (no drift, no
    // distortion) since the simulation doesn't drive those.
    private const byte CenteredStaticValue = 63;

    // CH12/CH29 (color change): 128-159 = "colorful color change of the
    // whole picture" (auto rainbow cycle) - the closest real-hardware analog
    // to the simulation's continuously drifting hue, since these channels
    // only pick fixed/cycling color macros rather than arbitrary RGB.
    private const byte ColorfulCycleValue = 140;

    // Below this brightness-weighted total, treat a half as "nothing lit"
    // rather than flickering the total-switch channel on/off from residual
    // near-black display color during fades.
    private const double OnThreshold = 3.0;

    // Beam tilt (radians) is produced by LaserFanAnimator bounded well
    // inside this range; used only to normalize into CH8/CH25's 0-127
    // static-shift span, so it doesn't need to match that animator's
    // internal constant exactly - just comfortably cover it.
    private const double AssumedMaxTiltRadians = 0.25;

    private readonly LaserFixture _laser;
    private readonly DmxUniverse _universe;
    private readonly int _baseChannel;

    public LaserDmxBinder(LaserFixture laser, DmxUniverse universe, int baseChannel = 1)
    {
        _laser = laser;
        _universe = universe;
        _baseChannel = baseChannel;

        InitStaticChannels(_baseChannel);
        InitStaticChannels(_baseChannel + BlockSize);

        ApplyDynamicChannels();
        laser.BeamsChanged += (_, _) => ApplyDynamicChannels();
    }

    private void InitStaticChannels(int block)
    {
        _universe.SetChannel(block + GallerySelectionOffset, LineGalleryValue);
        _universe.SetChannel(block + PatternSelectionOffset, PatternIndex);
        _universe.SetChannel(block + ScalingOffset, StaticScaleValue);
        _universe.SetChannel(block + RotationOffset, CenteredStaticValue);
        _universe.SetChannel(block + HorizontalMovementOffset, CenteredStaticValue);
        _universe.SetChannel(block + HorizontalZoomOffset, CenteredStaticValue);
        _universe.SetChannel(block + VerticalZoomOffset, CenteredStaticValue);
        _universe.SetChannel(block + ForceTintingOffset, 0);
        _universe.SetChannel(block + ColorChangeOffset, ColorfulCycleValue);
        _universe.SetChannel(block + NodeHighlightingOffset, 0);
        _universe.SetChannel(block + NodeExpansionOffset, 0);
        _universe.SetChannel(block + GradientOffset, 0);
        _universe.SetChannel(block + DistortionOffset, 0);
        _universe.SetChannel(block + FilterSelectionOffset, 0);
        _universe.SetChannel(block + OutOfBoundsSizeOffset, 0);
    }

    private void ApplyDynamicChannels()
    {
        var beamCount = LaserFixture.BeamCount;
        var domainEnd = (beamCount - 1) / 2;

        ApplyHalf(_baseChannel, 0, domainEnd);
        ApplyHalf(_baseChannel + BlockSize, domainEnd + 1, beamCount - 1);
    }

    private void ApplyHalf(int block, int startIndex, int endIndex)
    {
        var weightTotal = 0.0;
        var weightedTilt = 0.0;

        for (var i = startIndex; i <= endIndex; i++)
        {
            var color = _laser.BeamColors[i];
            var weight = (color.R + color.G + color.B) / 3.0;
            weightTotal += weight;
            weightedTilt += weight * _laser.BeamTiltRadians[i];
        }

        var isOn = weightTotal > OnThreshold;
        var tilt = weightTotal > 0.0 ? weightedTilt / weightTotal : 0.0;

        var verticalShift = CenteredStaticValue + tilt / AssumedMaxTiltRadians * CenteredStaticValue;
        verticalShift = Math.Clamp(verticalShift, 0.0, 127.0);

        _universe.SetChannel(block + TotalSwitchOffset, isOn ? (byte)255 : (byte)0);
        _universe.SetChannel(block + VerticalMovementOffset, (byte)verticalShift);
    }
}
