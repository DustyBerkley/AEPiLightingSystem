using System;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Mirrors the wall striplight's 8 <see cref="ParLight"/> cells and its pan
/// angle onto real DMX, using the Rockville Motionstrip's 38-channel mode
/// (Ch1 Pan, Ch2 Pan Speed, Ch3 Built-in Program, Ch4 Program Speed, Ch5
/// Master Dimmer, Ch6 Strobe, then 8 blocks of 4 - Red/Green/Blue/White -
/// one per bar segment, per the fixture's owner's manual).
///
/// White is pinned to 0 for every segment - <see cref="ParLight.Color"/> is
/// a plain RGB <see cref="System.Windows.Media.Color"/> with no separate
/// white channel modeled on screen, and RGB alone already covers the full
/// color range the app ever produces, so there's nothing meaningful to put
/// there.
///
/// Pan is driven from <see cref="WallStripLight.DisplayFacingAngleDegrees"/>
/// (the eased value, not the raw target) since
/// <see cref="Lighting.WallStripLightRotationAnimator"/> already simulates
/// this fixture's realistic rotation lag in software. The DMX Pan Speed
/// channel is pinned to <see cref="PanSpeedValue"/> = 0 - confirmed by
/// testing directly against the real fixture (2026-08-21) that the motor
/// only tracked Pan at all with this channel at 0, not 255 as originally
/// assumed ("255 = fastest" isn't this fixture's convention).
///
/// <see cref="PanValueAtLevel"/> = 127 (range center) is confirmed: the
/// unit is wall-mounted so true horizontal - <see cref="WallStripLight.FacingAngleDegrees"/>
/// == 0, which is *not* necessarily either of the two angles the app
/// actually switches between - sits at the middle of its rotation, not
/// either mechanical extreme. <see cref="WallStripLight.FacingAngleDegrees"/>
/// is always in degrees down from that same true-horizontal zero throughout
/// the app (see <see cref="WallStripLight"/>), so DMX units are converted
/// straight from the raw angle via <see cref="DmxUnitsPerDegree"/> (255 DMX
/// units across the fixture's full 270 degrees of rotation, per the
/// manual) - not relative to <see cref="WallStripLight.ForwardAngleDegrees"/>,
/// which is just whichever of the two target angles currently means
/// "forward" and has no bearing on where physical zero is. Still a
/// PLACEHOLDER: only that 127 = true horizontal is confirmed, not that
/// increasing the value actually rotates toward "down" specifically (vs.
/// up) - needs confirming against the real fixture.
/// </summary>
public sealed class WallStripLightDmxBinder
{
    private const int PanOffset = 0;
    private const int PanSpeedOffset = 1;
    private const int BuiltInProgramOffset = 2;
    private const int BuiltInProgramSpeedOffset = 3;
    private const int MasterDimmerOffset = 4;
    private const int StrobeOffset = 5;
    private const int BarBlockStartOffset = 6;
    private const int BarChannelsPerBar = 4;

    // See class summary - confirmed against the real fixture, not assumed.
    private const byte PanSpeedValue = 0;

    // PanValueAtLevel confirmed (see class summary); direction/magnitude
    // per degree still a placeholder guess pending confirmation.
    private const double PanValueAtLevel = 127.0;
    private const double DmxUnitsPerDegree = 255.0 / 270.0;

    private readonly WallStripLight _light;
    private readonly DmxUniverse _universe;
    private readonly int _baseChannel;

    /// <param name="rig">
    /// Only consulted for <see cref="LightingRig.GlobalBrightness"/> (the
    /// Global fader page's own max-brightness fader), which drives this
    /// fixture's real Master Dimmer channel directly - applied regardless
    /// of which mode or effect is otherwise driving <paramref name="light"/>.
    /// </param>
    public WallStripLightDmxBinder(LightingRig rig, WallStripLight light, DmxUniverse universe, int baseChannel)
    {
        _light = light;
        _universe = universe;
        _baseChannel = baseChannel;

        universe.SetChannel(baseChannel + PanSpeedOffset, PanSpeedValue);
        universe.SetChannel(baseChannel + BuiltInProgramOffset, 0);
        universe.SetChannel(baseChannel + BuiltInProgramSpeedOffset, 0);
        universe.SetChannel(baseChannel + StrobeOffset, 0);

        ApplyMasterDimmer(rig.GlobalBrightness);
        rig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(LightingRig.GlobalBrightness))
                ApplyMasterDimmer(rig.GlobalBrightness);
        };

        ApplyPan(light.DisplayFacingAngleDegrees);
        light.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(WallStripLight.DisplayFacingAngleDegrees))
                ApplyPan(light.DisplayFacingAngleDegrees);
        };

        for (var i = 0; i < light.Lights.Count; i++)
        {
            var index = i;
            ApplyBarColor(index);
            light.Lights[index].PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is null or nameof(ParLight.Color))
                    ApplyBarColor(index);
            };
        }
    }

    private void ApplyMasterDimmer(double globalBrightness)
    {
        var value = Math.Clamp(globalBrightness, 0.0, 1.0) * 255.0;
        _universe.SetChannel(_baseChannel + MasterDimmerOffset, (byte)Math.Round(value));
    }

    private void ApplyPan(double facingAngleDegrees)
    {
        var panValue = PanValueAtLevel + facingAngleDegrees * DmxUnitsPerDegree;
        panValue = Math.Clamp(panValue, 0.0, 255.0);
        _universe.SetChannel(_baseChannel + PanOffset, (byte)panValue);
    }

    private void ApplyBarColor(int index)
    {
        var color = _light.Lights[index].Color;
        var barBase = _baseChannel + BarBlockStartOffset + index * BarChannelsPerBar;
        _universe.SetChannel(barBase + 0, color.R);
        _universe.SetChannel(barBase + 1, color.G);
        _universe.SetChannel(barBase + 2, color.B);
        _universe.SetChannel(barBase + 3, 0);
    }
}
