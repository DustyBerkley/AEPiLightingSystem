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

    // Wall and floor par lights are animated entirely independently - the
    // wall 4 get every beat/impact-driven flash effect, the floor 4 get
    // their own separate ambient behavior - so each group has its own
    // collection rather than one combined list.
    public ReadOnlyCollection<ParLight> WallParLights { get; }

    public ReadOnlyCollection<ParLight> FloorParLights { get; }

    public ReadOnlyCollection<LedStrip> LedStrips { get; }

    public LaserFixture Laser { get; } = new();

    public WallStripLight WallStripLight { get; } = new();

    // Read live off the Rekordbox screen mirror by BpmReader - null until a
    // reading has been confirmed (see BpmReader's debounce requirement).
    public double? Deck1Bpm { get; set; }
    public double? Deck2Bpm { get; set; }

    // Read live off the narrow mirror capture by VolumeReader - 0-100.
    public double Deck1Volume { get; set; }
    public double Deck2Volume { get; set; }

    // Whichever deck currently has the higher channel volume is assumed to
    // be the one actually driving the room, so its BPM is "the" tempo.
    public double? MasterBpm { get; set; }

    // Counts every time Rekordbox flashes the measure-start marker pixel to
    // #3F0000 on each deck - see MainWindow's _topCaptureTimer.Tick.
    public int Deck1MeasureCount { get; set; }
    public int Deck2MeasureCount { get; set; }

    // Read off each corner capture's middle pixel - #575757 means playing,
    // anything else means paused. See MainWindow's _cornerCaptureTimer.Tick
    // / _deck2CornerCaptureTimer.Tick.
    public bool Deck1IsPlaying { get; set; }
    public bool Deck2IsPlaying { get; set; }

    // The (not yet purchased) 8-fader MIDI board - see MidiFader. Each page
    // is its own independent bank of 8, since the physical board only has 8
    // faders total and switching pages re-purposes them to a different set
    // of effects rather than adding more faders.
    public ReadOnlyCollection<MidiFader> BassFaders { get; }
    public ReadOnlyCollection<MidiFader> LaserFaders { get; }
    public ReadOnlyCollection<MidiFader> LedStripFaders { get; }
    public ReadOnlyCollection<MidiFader> ComboFaders { get; }

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

        WallParLights = new ReadOnlyCollection<ParLight>(new[]
        {
            TopLeftPar, TopRightPar, BottomLeftPar, BottomRightPar,
        });

        FloorParLights = new ReadOnlyCollection<ParLight>(new[]
        {
            FloorTopLeftPar, FloorTopRightPar, FloorBottomLeftPar, FloorBottomRightPar,
        });

        var strips = new LedStrip[6];
        for (var i = 0; i < strips.Length; i++)
            strips[i] = new LedStrip(i + 1, $"Strip {i + 1}");

        LedStrips = new ReadOnlyCollection<LedStrip>(strips);

        BassFaders = CreateFaderBank(FaderPage.Bass);
        LaserFaders = CreateFaderBank(FaderPage.Laser);
        LedStripFaders = CreateFaderBank(FaderPage.LedStrip);
        ComboFaders = CreateFaderBank(FaderPage.Combo);

        BassFaders[0].BeatInterval = 1;

        BassFaders[1].BeatInterval = 1;

        // TEMP: manually activating bass fader 5 at 100% to test the
        // downward light bar flash effect before real hardware is connected.
        BassFaders[4].Value = 1.0;
    }

    private static ReadOnlyCollection<MidiFader> CreateFaderBank(FaderPage page)
    {
        var faders = new MidiFader[8];
        for (var i = 0; i < faders.Length; i++)
            faders[i] = new MidiFader(page, i + 1);

        return new ReadOnlyCollection<MidiFader>(faders);
    }
}
