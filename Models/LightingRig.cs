using System.Collections.ObjectModel;

namespace AEPiLightingSystem.Models;

/// <summary>
/// The full physical lighting setup: 4 par lights flanking the DJ stand and
/// 6 WLED-driven diffused LED strips.
/// </summary>
public class LightingRig
{
    public ParLight TopLeftPar { get; }
    public ParLight TopRightPar { get; }
    public ParLight BottomLeftPar { get; }
    public ParLight BottomRightPar { get; }

    public ReadOnlyCollection<ParLight> ParLights { get; }

    public ReadOnlyCollection<LedStrip> LedStrips { get; }

    public LightingRig()
    {
        TopLeftPar = new ParLight(1, "Par 1 - Top Left");
        TopRightPar = new ParLight(2, "Par 2 - Top Right");
        BottomLeftPar = new ParLight(3, "Par 3 - Bottom Left");
        BottomRightPar = new ParLight(4, "Par 4 - Bottom Right");

        ParLights = new ReadOnlyCollection<ParLight>(new[]
        {
            TopLeftPar, TopRightPar, BottomLeftPar, BottomRightPar,
        });

        var strips = new LedStrip[6];
        for (var i = 0; i < strips.Length; i++)
            strips[i] = new LedStrip(i + 1, $"Strip {i + 1}");

        LedStrips = new ReadOnlyCollection<LedStrip>(strips);
    }
}
