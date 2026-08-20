using System.Collections.ObjectModel;

namespace AEPiLightingSystem.Models;

/// <summary>
/// The full physical lighting setup: 4 wall-mounted par lights flanking the
/// DJ stand, 4 smaller floor par lights below them, and 6 WLED-driven
/// diffused LED strips.
/// </summary>
public class LightingRig
{
    public ParLight TopLeftPar { get; }
    public ParLight TopRightPar { get; }
    public ParLight BottomLeftPar { get; }
    public ParLight BottomRightPar { get; }

    public ParLight FloorTopLeftPar { get; }
    public ParLight FloorTopRightPar { get; }
    public ParLight FloorBottomLeftPar { get; }
    public ParLight FloorBottomRightPar { get; }

    public ReadOnlyCollection<ParLight> ParLights { get; }

    public ReadOnlyCollection<LedStrip> LedStrips { get; }

    public LightingRig()
    {
        TopLeftPar = new ParLight(1, "Par 1 - Wall Top Left");
        TopRightPar = new ParLight(2, "Par 2 - Wall Top Right");
        BottomLeftPar = new ParLight(3, "Par 3 - Wall Bottom Left");
        BottomRightPar = new ParLight(4, "Par 4 - Wall Bottom Right");

        FloorTopLeftPar = new ParLight(5, "Par 5 - Floor Top Left");
        FloorTopRightPar = new ParLight(6, "Par 6 - Floor Top Right");
        FloorBottomLeftPar = new ParLight(7, "Par 7 - Floor Bottom Left");
        FloorBottomRightPar = new ParLight(8, "Par 8 - Floor Bottom Right");

        ParLights = new ReadOnlyCollection<ParLight>(new[]
        {
            TopLeftPar, TopRightPar, BottomLeftPar, BottomRightPar,
            FloorTopLeftPar, FloorTopRightPar, FloorBottomLeftPar, FloorBottomRightPar,
        });

        var strips = new LedStrip[6];
        for (var i = 0; i < strips.Length; i++)
            strips[i] = new LedStrip(i + 1, $"Strip {i + 1}");

        LedStrips = new ReadOnlyCollection<LedStrip>(strips);
    }
}
