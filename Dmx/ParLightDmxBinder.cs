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

    public ParLightDmxBinder(LightingRig rig, DmxUniverse universe)
    {
        Bind(rig.FloorTopLeftPar, TopLeftBaseChannel, universe);
        Bind(rig.FloorBottomLeftPar, BottomLeftBaseChannel, universe);
        Bind(rig.FloorBottomRightPar, BottomRightBaseChannel, universe);
        Bind(rig.FloorTopRightPar, TopRightBaseChannel, universe);

        Bind(rig.TopRightPar, WallTopRightBaseChannel, universe);
        Bind(rig.BottomRightPar, WallBottomRightBaseChannel, universe);
        Bind(rig.BottomLeftPar, WallBottomLeftBaseChannel, universe);
        Bind(rig.TopLeftPar, WallTopLeftBaseChannel, universe);
    }

    private static void Bind(ParLight light, int baseChannel, DmxUniverse universe)
    {
        universe.SetChannel(baseChannel + DimmerOffset, 255);
        universe.SetChannel(baseChannel + StrobeOffset, 0);
        universe.SetChannel(baseChannel + AutoProgramOffset, 0);
        universe.SetChannel(baseChannel + ProgramSpeedOffset, 0);

        ApplyColor(light, baseChannel, universe);

        light.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ParLight.Color))
                ApplyColor(light, baseChannel, universe);
        };
    }

    private static void ApplyColor(ParLight light, int baseChannel, DmxUniverse universe)
    {
        var color = light.Color;
        universe.SetChannel(baseChannel + RedOffset, color.R);
        universe.SetChannel(baseChannel + GreenOffset, color.G);
        universe.SetChannel(baseChannel + BlueOffset, color.B);
    }
}
