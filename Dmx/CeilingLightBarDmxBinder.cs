using System;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Dmx;

/// <summary>
/// Mirrors each ceiling light bar onto its 75 DMX channels, per the
/// fixture's 75CH table:
/// <code>
///   Ch1      total dimming        <- LightingRig.GlobalBrightness
///   Ch2      strobe speed         <- pinned to StrobeValue
///   Ch3-14   amber bulbs Y1-Y12   <- CeilingLightBar.AmberLevels
///   Ch15-27  white LEDs W1-W13    <- CeilingLightBar.WhiteLevels
///   Ch28-75  RGB1-RGB16 (R,G,B)   <- CeilingLightBar.Pixels
/// </code>
/// Global brightness goes out on the fixture's own total-dimming channel,
/// like the wall striplight's master dimmer, rather than being baked into
/// every individual level.
/// </summary>
public sealed class CeilingLightBarDmxBinder
{
    private const int TotalDimmingOffset = 0;
    private const int StrobeOffset = 1;
    private const int AmberStartOffset = 2;
    private const int WhiteStartOffset = AmberStartOffset + CeilingLightBar.AmberBulbCount;
    private const int RgbStartOffset = WhiteStartOffset + CeilingLightBar.WhiteLedCount;

    // The manual only describes this channel as "0-255 strobe speed from
    // slow to fast", without saying where (or whether) "no strobe" sits the
    // way the 12/14-channel modes' "0-10 closed" ranges do. 0 is the
    // assumption - if the bars strobe constantly on the real fixtures, this
    // is the first thing to check.
    private const byte StrobeValue = 0;

    // Matches the physical address set on each real bar.
    private const int FrontLeftBaseChannel = 129;
    private const int FrontRightBaseChannel = 204;
    private const int BackLeftBaseChannel = 279;
    private const int BackRightBaseChannel = 354;

    private readonly DmxUniverse _universe;

    public CeilingLightBarDmxBinder(LightingRig rig, DmxUniverse universe)
    {
        _universe = universe;

        // The back-left bar is physically mounted the other way round (its
        // fixture "1" end at the back), so its data goes out end-for-end
        // reversed - that way every bar's index 0 still lands at the front.
        var bound = new[]
        {
            (rig.FrontLeftBar, FrontLeftBaseChannel, false),
            (rig.FrontRightBar, FrontRightBaseChannel, false),
            (rig.BackLeftBar, BackLeftBaseChannel, true),
            (rig.BackRightBar, BackRightBaseChannel, false),
        };

        foreach (var (bar, baseChannel, isReversed) in bound)
        {
            universe.SetChannel(baseChannel + StrobeOffset, StrobeValue);
            ApplyLevels(bar, baseChannel, isReversed);
            bar.Changed += (_, _) => ApplyLevels(bar, baseChannel, isReversed);
        }

        ApplyTotalDimming(bound, rig.GlobalBrightness);
        rig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(LightingRig.GlobalBrightness))
                ApplyTotalDimming(bound, rig.GlobalBrightness);
        };
    }

    private void ApplyTotalDimming((CeilingLightBar Bar, int BaseChannel, bool IsReversed)[] bound, double globalBrightness)
    {
        var value = (byte)Math.Round(Math.Clamp(globalBrightness, 0.0, 1.0) * 255.0);
        foreach (var (_, baseChannel, _) in bound)
            _universe.SetChannel(baseChannel + TotalDimmingOffset, value);
    }

    private void ApplyLevels(CeilingLightBar bar, int baseChannel, bool isReversed)
    {
        // Model index i -> the fixture's own element number (0-based).
        static int Physical(int i, int count, bool reversed) => reversed ? count - 1 - i : i;

        for (var i = 0; i < CeilingLightBar.AmberBulbCount; i++)
            _universe.SetChannel(baseChannel + AmberStartOffset + Physical(i, CeilingLightBar.AmberBulbCount, isReversed), bar.AmberLevels[i]);

        for (var i = 0; i < CeilingLightBar.WhiteLedCount; i++)
            _universe.SetChannel(baseChannel + WhiteStartOffset + Physical(i, CeilingLightBar.WhiteLedCount, isReversed), bar.WhiteLevels[i]);

        var dim = Math.Clamp(bar.RgbDimFactor, 0.0, 1.0);
        for (var i = 0; i < CeilingLightBar.RgbPixelCount; i++)
        {
            var color = bar.Pixels[i];
            var channel = baseChannel + RgbStartOffset + Physical(i, CeilingLightBar.RgbPixelCount, isReversed) * 3;
            _universe.SetChannel(channel, (byte)Math.Round(color.R * dim));
            _universe.SetChannel(channel + 1, (byte)Math.Round(color.G * dim));
            _universe.SetChannel(channel + 2, (byte)Math.Round(color.B * dim));
        }
    }
}
