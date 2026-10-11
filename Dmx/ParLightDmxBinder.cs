using System;
using System.Collections.Generic;
using System.ComponentModel;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Mirrors each par light's on-screen <see cref="ParLight.Color"/> onto its
/// 7 DMX channels (VYmaksran A84-236 layout: Dimmer, Red, Green, Blue,
/// Strobe, Auto/Program, Program Speed), keeping the universe in sync as
/// colors change. Dimmer is pinned to full - the app's flash/fade animations
/// already bake brightness into the RGB values themselves - and
/// Strobe/Auto/Speed are pinned to 0 so the fixture stays in plain DMX RGB
/// mode rather than running its own built-in programs.
///
/// <see cref="LightingRig.GlobalBrightness"/> (the Global fader page's own
/// max-brightness fader) additionally scales every R/G/B value written out
/// here, on top of whatever the light's own Color already is - applied at
/// the very last step before the DMX universe, so it affects the real
/// fixture regardless of which mode (or which effect, manual or automated)
/// produced that color in the first place.
/// </summary>
public sealed class ParLightDmxBinder
{
    private const int DimmerOffset = 0;
    private const int RedOffset = 1;
    private const int GreenOffset = 2;
    private const int BlueOffset = 3;
    private const int StrobeOffset = 4;
    private const int AutoProgramOffset = 5;
    private const int ProgramSpeedOffset = 6;

    // Matches the physical address dials set on the 4 real floor par
    // fixtures.
    private const int TopLeftBaseChannel = 63;
    private const int BottomLeftBaseChannel = 70;
    private const int BottomRightBaseChannel = 77;
    private const int TopRightBaseChannel = 84;

    // Matches the physical address dials set on the 4 real wall par
    // fixtures - same 7-channel layout as the floor pars, but addressed
    // lower (1-62) to stay clear of the laser's 34-channel block (15-48).
    private const int WallTopRightBaseChannel = 1;
    private const int WallBottomRightBaseChannel = 8;
    private const int WallBottomLeftBaseChannel = 49;
    private const int WallTopLeftBaseChannel = 56;

    private readonly LightingRig _rig;
    private readonly DmxUniverse _universe;
    private readonly List<(ParLight Light, int BaseChannel)> _bound = new();

    public ParLightDmxBinder(LightingRig rig, DmxUniverse universe)
    {
        _rig = rig;
        _universe = universe;

        Bind(rig.FloorTopLeftPar, TopLeftBaseChannel);
        Bind(rig.FloorBottomLeftPar, BottomLeftBaseChannel);
        Bind(rig.FloorBottomRightPar, BottomRightBaseChannel);
        Bind(rig.FloorTopRightPar, TopRightBaseChannel);

        Bind(rig.TopRightPar, WallTopRightBaseChannel);
        Bind(rig.BottomRightPar, WallBottomRightBaseChannel);
        Bind(rig.BottomLeftPar, WallBottomLeftBaseChannel);
        Bind(rig.TopLeftPar, WallTopLeftBaseChannel);

        rig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(LightingRig.GlobalBrightness))
                ReapplyAll();
        };
    }

    private void Bind(ParLight light, int baseChannel)
    {
        _universe.SetChannel(baseChannel + DimmerOffset, 255);
        _universe.SetChannel(baseChannel + StrobeOffset, 0);
        _universe.SetChannel(baseChannel + AutoProgramOffset, 0);
        _universe.SetChannel(baseChannel + ProgramSpeedOffset, 0);

        _bound.Add((light, baseChannel));
        ApplyColor(light, baseChannel);

        light.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ParLight.Color))
                ApplyColor(light, baseChannel);
        };
    }

    private void ReapplyAll()
    {
        foreach (var (light, baseChannel) in _bound)
            ApplyColor(light, baseChannel);
    }

    private void ApplyColor(ParLight light, int baseChannel)
    {
        var brightness = Math.Clamp(_rig.GlobalBrightness, 0.0, 1.0);
        var color = light.Color;
        _universe.SetChannel(baseChannel + RedOffset, (byte)Math.Round(color.R * brightness));
        _universe.SetChannel(baseChannel + GreenOffset, (byte)Math.Round(color.G * brightness));
        _universe.SetChannel(baseChannel + BlueOffset, (byte)Math.Round(color.B * brightness));
    }
}
