using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AEPiLightingSystem.Models;

/// <summary>
/// The full physical lighting setup: 4 wall-mounted par lights flanking the
/// DJ stand, 4 smaller floor par lights below them, and 4 ceiling light bars
/// in a staggered zigzag.
/// </summary>
public class LightingRig : INotifyPropertyChanged
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

    // Staggered front-to-back in a zigzag: each one sits further back than
    // the one before it, alternating sides.
    public CeilingLightBar FrontLeftBar { get; }
    public CeilingLightBar FrontRightBar { get; }
    public CeilingLightBar BackLeftBar { get; }
    public CeilingLightBar BackRightBar { get; }

    /// <summary>Ordered front-most to back-most - effects rely on this to treat the ceiling as one depth-ordered field.</summary>
    public ReadOnlyCollection<CeilingLightBar> LightBars { get; }

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
    public ReadOnlyCollection<MidiFader> LightBarFaders { get; }
    public ReadOnlyCollection<MidiFader> ComboFaders { get; }

    // Not reachable from the on-screen page-select buttons, only from the
    // Launchpad's row-1 pad 4 (see MainWindow) - global, fixture-wide
    // controls rather than a per-effect bank.
    public ReadOnlyCollection<MidiFader> GlobalFaders { get; }

    // Which page's bank of 8 the physical board currently drives - kept
    // here (rather than just as a private field on MainWindow) so the
    // active-fader-effects strip can bind a fader's page-name label color to
    // it (see MainWindow.SelectFaderPage and the PageActiveToBrushConverter
    // in MainWindow.xaml).
    private FaderPage _selectedFaderPage;

    public FaderPage SelectedFaderPage
    {
        get => _selectedFaderPage;
        set
        {
            if (_selectedFaderPage == value)
                return;

            _selectedFaderPage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFaderPage)));
        }
    }

    // Ceiling every light's output gets scaled to, regardless of mode or
    // which effect (manual or automated) produced its color - see the
    // Global fader page's 1st fader (MainWindow.UpdateGlobalBrightnessFaderEffect)
    // and the real-DMX binders that actually apply it
    // (ParLightDmxBinder/WallStripLightDmxBinder). 1.0 = no restriction, the
    // default - unlike every other fader's 0%-default "off" convention,
    // this one starts fully open since it's a ceiling, not an activation
    // switch (see GlobalFaders[0].Value set below).
    private double _globalBrightness = 1.0;

    public double GlobalBrightness
    {
        get => _globalBrightness;
        set
        {
            if (_globalBrightness == value)
                return;

            _globalBrightness = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalBrightness)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

        // Depth offsets follow the ceiling plan in MainWindow.xaml (bars 120px
        // long, fronts at 22/76/168/222px) - each side's two bars end to end
        // with a small gap, the right side shifted back by about half a bar.
        FrontLeftBar = new CeilingLightBar(1, "Bar 1 - Front Left", isLeftSide: true, depthOffset: 0.0);
        FrontRightBar = new CeilingLightBar(2, "Bar 2 - Front Right", isLeftSide: false, depthOffset: 0.45);
        BackLeftBar = new CeilingLightBar(3, "Bar 3 - Back Left", isLeftSide: true, depthOffset: 1.22);
        BackRightBar = new CeilingLightBar(4, "Bar 4 - Back Right", isLeftSide: false, depthOffset: 1.67);

        LightBars = new ReadOnlyCollection<CeilingLightBar>(new[]
        {
            FrontLeftBar, FrontRightBar, BackLeftBar, BackRightBar,
        });

        BassFaders = CreateFaderBank(FaderPage.Bass);
        LaserFaders = CreateFaderBank(FaderPage.Laser);
        LightBarFaders = CreateFaderBank(FaderPage.LightBar);
        ComboFaders = CreateFaderBank(FaderPage.Combo);
        GlobalFaders = CreateFaderBank(FaderPage.Global);

        // The max-brightness fader is a ceiling, not an activation switch -
        // starts fully open (see GlobalBrightness's own default above)
        // rather than following every other fader's 0%-default convention.
        GlobalFaders[0].Value = 1.0;

        BassFaders[0].BeatInterval = 1;

        BassFaders[1].BeatInterval = 1;

        // Faders 6, 7, and 8 (all floor-par effects) default to twice a beat
        // rather than once, since the floor pars are meant to feel busier
        // than the wall pars' effects.
        BassFaders[5].BeatInterval = 0.5;
        BassFaders[6].BeatInterval = 0.5;
        BassFaders[7].BeatInterval = 0.5;

    }

    private static ReadOnlyCollection<MidiFader> CreateFaderBank(FaderPage page)
    {
        var faders = new MidiFader[8];
        for (var i = 0; i < faders.Length; i++)
            faders[i] = new MidiFader(page, i + 1);

        return new ReadOnlyCollection<MidiFader>(faders);
    }
}
