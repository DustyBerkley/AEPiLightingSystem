using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AEPiLightingSystem.Audio;
using AEPiLightingSystem.Dmx;
using AEPiLightingSystem.Lighting;
using AEPiLightingSystem.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AEPiLightingSystem;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private static readonly Color BackgroundColor = Color.FromRgb(0x08, 0x08, 0x08);

    private readonly AudioCaptureService _audioCapture = new();
    private readonly ParLightFlashAnimator _parFlashAnimator;
    private readonly ParLightFlashAnimator _wallStripFlashAnimator;
    private readonly WallStripLightRotationAnimator _wallStripRotationAnimator;
    private readonly LedStripFlashAnimator _stripFlashAnimator;
    private readonly LedStripLavaLampAnimator _lavaLampAnimator;
    private readonly LaserFanAnimator _laserAnimator;
    private readonly ParLightPatternSequencer _patternSequencer = new();
    private readonly BasslineActivityTracker _basslineTracker = new();
    private readonly MelodicPassageDetector _melodicPassageDetector = new();
    private readonly LoudnessTracker _loudnessTracker = new();
    private readonly AutomaticGainControl _gainControl = new();
    private readonly EffectBrightnessTracker _effectBrightness = new();
    private readonly EffectSpeedTracker _effectSpeed = new();
    private readonly ChordSustainDetector _chordSustainDetector = new();

    // Individual per-light rather than grouped so OnBassHit can flash just
    // the specific floor light(s) that map onto whichever wall par
    // grouping is currently active (including diagonal pairs, which cut
    // across a simple top/bottom split) - every other case (ambient
    // SetActive, whole-rig Flash()) just calls all 4 together.
    private readonly ParLightAmbientAnimator _floorTopLeftAmbientAnimator;
    private readonly ParLightAmbientAnimator _floorTopRightAmbientAnimator;
    private readonly ParLightAmbientAnimator _floorBottomLeftAmbientAnimator;
    private readonly ParLightAmbientAnimator _floorBottomRightAmbientAnimator;

    private readonly DmxUniverse _dmxUniverse = new();
    private readonly DmxOutputService _dmxOutput;
    private readonly ParLightDmxBinder _dmxBinder;
    private readonly WallStripLightDmxBinder _wallStripDmxBinder;
    private readonly LaserDmxBinder _laserDmxBinder;

    // Live mirror of the center 700x150px of the middle monitor (Rekordbox,
    // fullscreen) - see ScreenMirrorCapture for the monitor/region math. Its
    // own preview panel is hidden now that BpmReader's crop regions are
    // tuned, but the capture keeps running - BpmReader still reads off of it.
    private readonly ScreenMirrorCapture _screenMirrorCapture = new();
    private readonly DispatcherTimer _screenMirrorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // A second, narrower mirror - same height as the one above, a quarter
    // the width, and centered on that same fixed point (see
    // ScreenMirrorCapture) - this one's preview panel stays visible.
    private readonly ScreenMirrorCapture _narrowMirrorCapture = new(width: 85, height: 60, centerOffsetXPixels: -10, centerOffsetYPixels: 15);
    private readonly DispatcherTimer _narrowMirrorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // Reads each deck's channel fader volume off that same narrow capture -
    // see VolumeReader. Cheap pure pixel-counting, so it just runs inline on
    // every narrow-mirror tick rather than needing its own slower timer.
    private readonly VolumeReader _volumeReader = new();

    // A separate 100x40 capture, horizontally centered on the middle
    // monitor and 200px down from its top edge - independent of the shared
    // tuned center the other 2 captures use.
    private readonly ScreenMirrorCapture _topCapture = ScreenMirrorCapture.CenteredHorizontallyFromTop(width: 30, height: 40, topPixelsFromMonitorTop: 200, offsetRightPixels: 482);
    // 60Hz so a measure-start flash (see MeasureStartColors below) can't land
    // in the gap between ticks and get missed - the other captures update
    // at 100ms since nothing they read changes anywhere near that fast.
    private readonly DispatcherTimer _topCaptureTimer = new() { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };

    // 30x30, positioned right below _topCapture's own bottom-right corner -
    // relative to that capture's OriginX/OriginY/Width/Height rather than
    // the shared tuned center, so it stays put if _topCapture ever moves.
    private readonly ScreenMirrorCapture _cornerCapture;
    private readonly DispatcherTimer _cornerCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // Same box as _cornerCapture, 300px further right - deck 2's playing/
    // paused indicator.
    private readonly ScreenMirrorCapture _deck2CornerCapture;
    private readonly DispatcherTimer _deck2CornerCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // Middle pixel of either capture reads exactly one of these when that
    // deck is playing - anything else means paused.
    private static readonly HashSet<Color> PlayingColors = new()
    {
        Color.FromRgb(0x57, 0x57, 0x57),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    // Full monitor width, 150px tall, starting 150px down from the
    // monitor's own top edge - shown docked at the very bottom of the app
    // window regardless of the capture's own aspect ratio.
    private readonly ScreenMirrorCapture _fullWidthCapture = ScreenMirrorCapture.FullWidthFromTop(height: 144, topPixelsFromMonitorTop: 150, widthFraction: 2.0 / 3.0, offsetRightPixels: 960);

    // See CaptureLoop - this capture alone is much bigger than the others
    // (full monitor width vs. 30-85px), and doing the BitBlt/bitmap-creation
    // work synchronously on the UI thread at 60Hz was stalling WPF's own
    // render pump enough to make the display visibly jumpy despite "running
    // at 60fps" on paper. Moving every capture to its own background thread
    // made things worse instead (likely cross-thread GDI contention plus
    // sheer thread count), so only this one - the one that actually needed
    // it - gets a dedicated thread; the rest stay on plain DispatcherTimers.
    private CaptureLoop? _fullWidthCaptureLoop;

    // Rekordbox flashes the marker pixel to exactly this color the instant
    // a new measure starts on deck 1 - tracked as a rising edge (see
    // ApplyTopCaptureFrame) so each transition into it counts once, not
    // once per tick it happens to still be lit.
    private static readonly HashSet<Color> MeasureStartColors = new()
    {
        Color.FromRgb(0x3F, 0x00, 0x00),
        Color.FromRgb(0x7F, 0x00, 0x00),
    };
    private bool _wasDeck1MeasureColor;
    private bool _wasDeck2MeasureColor;

    // Beat-in-measure tracking: a master-deck measure-start marks beat 1;
    // every other beat is inferred purely from elapsed time against the
    // master BPM (assumes 4/4 time) until the next measure-start marker
    // resyncs it, correcting any drift.
    private DateTime? _masterMeasureStartTime;
    private int _lastDisplayedBeat;

    // Reads the 2 deck BPM numbers out of that mirror via OCR - see
    // BpmReader. Runs on its own slower timer since OCR is far more
    // expensive than the plain screen grab, and BPM basically never changes
    // faster than this.
    private readonly BpmReader _bpmReader = new();
    private readonly DispatcherTimer _bpmReadTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isReadingBpm;
    private BassOnsetDetector? _bassOnsetDetector;
    private ImpactDetector? _impactDetector;
    private PercussionBuildupDetector? _percussionBuildupDetector;
    private DateTime _lastRigFlashTime = DateTime.MinValue;
    private DateTime _lastBassHitTime = DateTime.MinValue;
    private bool _wasMelodicPassageActive;
    private bool _wasLoud;
    private bool _wasSustainedChord;

    // Full auto = automated animations only. Semi auto = both automated and
    // manual fader effects run together. Manual = automated animations off,
    // manual fader effects only. See AutomationEnabled/ManualEffectsEnabled.
    private LightingMode _lightingMode = LightingMode.Manual;

    private bool AutomationEnabled => _lightingMode != LightingMode.Manual;
    private bool ManualEffectsEnabled => _lightingMode != LightingMode.FullAuto;

    public LightingRig Rig { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        SelectFaderPage(FaderPageBassButton, FaderPage.Bass);
        SetLightingMode(LightingMode.Manual, ManualModeButton);

        _cornerCapture = ScreenMirrorCapture.AtOrigin(
            width: 30, height: 30,
            originX: _topCapture.OriginX + _topCapture.Width - 275,
            originY: _topCapture.OriginY + _topCapture.Height + 260);

        _deck2CornerCapture = ScreenMirrorCapture.AtOrigin(
            width: 30, height: 30,
            originX: _topCapture.OriginX + _topCapture.Width - 275 + 330,
            originY: _topCapture.OriginY + _topCapture.Height + 260);

        // Has to wait for the window's actual Win32 handle to exist -
        // setting Left/Top any earlier (e.g. directly here in the
        // constructor) doesn't reliably influence which monitor
        // WindowState.Maximized resolves to.
        SourceInitialized += (_, _) => PositionFullScreenOnLeftmostMonitor();

        ApplicationThemeManager.Apply(this);
        SystemThemeWatcher.Watch(this);

        // The theme manager/watcher reassert their own background brush around
        // load time, so pin ours after that settles instead of only in the ctor.
        Background = new SolidColorBrush(BackgroundColor);
        Loaded += (_, _) => Background = new SolidColorBrush(BackgroundColor);

        DataContext = Rig;

        _parFlashAnimator = new ParLightFlashAnimator(Rig.WallParLights, decaySeconds: 0.22);
        _wallStripFlashAnimator = new ParLightFlashAnimator(Rig.WallStripLight.Lights, decaySeconds: 0.22);
        _wallStripRotationAnimator = new WallStripLightRotationAnimator(Rig.WallStripLight);
        _stripFlashAnimator = new LedStripFlashAnimator(Rig.LedStrips, decaySeconds: 0.35);
        _lavaLampAnimator = new LedStripLavaLampAnimator(Rig.LedStrips);
        _laserAnimator = new LaserFanAnimator(Rig.Laser);

        // A chord hit only means anything visually if the melodic passage
        // has actually settled into "one sustained chord" by the time it
        // fires - otherwise it's just another attack within a busy
        // arpeggio/vocal line, which should keep using the normal fan.
        _chordSustainDetector.ChordOnset += (_, _) =>
        {
            if (AutomationEnabled && _chordSustainDetector.IsSustainedChord)
                _laserAnimator.TriggerChordHit();
        };

        _floorTopLeftAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorTopLeftPar });
        _floorTopRightAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorTopRightPar });
        _floorBottomLeftAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorBottomLeftPar });
        _floorBottomRightAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorBottomRightPar });

        _dmxOutput = new DmxOutputService(_dmxUniverse);
        _dmxOutput.ConnectionLost += (_, ex) => Dispatcher.BeginInvoke(() => OnDmxConnectionLost(ex));
        _dmxBinder = new ParLightDmxBinder(Rig, _dmxUniverse);
        _wallStripDmxBinder = new WallStripLightDmxBinder(Rig.WallStripLight, _dmxUniverse, baseChannel: 91);
        _laserDmxBinder = new LaserDmxBinder(Rig.Laser, _dmxUniverse, baseChannel: 15);

        // All fixtures default to off; nothing lights up until an effect fires.
        foreach (var strip in Rig.LedStrips)
            strip.Fill(Colors.Black);

        SetupFaderConflicts();

        // TEMP: activating fader 4 while fader 5 (set in LightingRig's own
        // constructor) is already on, to demonstrate the conflict/
        // invalidation system live before real hardware is connected.
        Rig.BassFaders[3].Value = 1.0;

        _screenMirrorTimer.Tick += (_, _) =>
        {
            if (_screenMirrorCapture.Capture() is { } frame)
                RekordboxMirrorImage.Source = frame;
        };

        _narrowMirrorTimer.Tick += (_, _) =>
        {
            if (_narrowMirrorCapture.Capture() is not { } frame)
                return;

            NarrowMirrorImage.Source = frame;

            _volumeReader.Read(frame);
            Rig.Deck1Volume = _volumeReader.Deck1Volume;
            Rig.Deck2Volume = _volumeReader.Deck2Volume;
            Deck1VolumeText.Text = $"{_volumeReader.Deck1Volume:0}%";
            Deck2VolumeText.Text = $"{_volumeReader.Deck2Volume:0}%";

            UpdateMasterBpm();
        };

        PositionRegionOverlay(Deck1RegionOverlay, BpmReader.Deck1Region);
        PositionRegionOverlay(Deck2RegionOverlay, BpmReader.Deck2Region);

        _bpmReadTimer.Tick += async (_, _) => await ReadBpmAsync();

        _topCaptureTimer.Tick += (_, _) =>
        {
            if (_topCapture.Capture() is not { } frame)
                return;

            // Matches the marker dots in the (currently hidden) debug
            // overlay: deck 1's at (13,18), deck 2's 5px further down at (13,23).
            var deck1Color = SamplePixelColor(frame, x: 13, y: 18);
            var deck2Color = SamplePixelColor(frame, x: 13, y: 23);
            ApplyTopCaptureFrame(frame, deck1Color, deck2Color);
        };

        _cornerCaptureTimer.Tick += (_, _) =>
        {
            if (_cornerCapture.Capture() is not { } frame)
                return;

            var middleColor = SamplePixelColor(frame, x: 15, y: 15);
            CornerCaptureImage.Source = frame;
            CornerCaptureColorSwatch.Fill = new SolidColorBrush(middleColor);
            CornerCaptureColorText.Text = $"#{middleColor.R:X2}{middleColor.G:X2}{middleColor.B:X2}";

            Rig.Deck1IsPlaying = PlayingColors.Contains(middleColor);
            Deck1PlaybackStatusText.Text = Rig.Deck1IsPlaying ? "Deck 1: Playing" : "Deck 1: Paused";
            UpdateStatusBanners();
        };

        _deck2CornerCaptureTimer.Tick += (_, _) =>
        {
            if (_deck2CornerCapture.Capture() is not { } frame)
                return;

            var middleColor = SamplePixelColor(frame, x: 15, y: 15);
            Deck2CornerCaptureImage.Source = frame;
            Deck2CornerCaptureColorSwatch.Fill = new SolidColorBrush(middleColor);
            Deck2CornerCaptureColorText.Text = $"#{middleColor.R:X2}{middleColor.G:X2}{middleColor.B:X2}";

            Rig.Deck2IsPlaying = PlayingColors.Contains(middleColor);
            Deck2PlaybackStatusText.Text = Rig.Deck2IsPlaying ? "Deck 2: Playing" : "Deck 2: Paused";
            UpdateStatusBanners();
        };

        Loaded += (_, _) => StartListening();
        Loaded += (_, _) => TryAutoConnectDmx();
        Loaded += (_, _) => _screenMirrorTimer.Start();
        Loaded += (_, _) => _narrowMirrorTimer.Start();
        Loaded += (_, _) => _bpmReadTimer.Start();
        Loaded += (_, _) => _topCaptureTimer.Start();
        Loaded += (_, _) => _cornerCaptureTimer.Start();
        Loaded += (_, _) => _deck2CornerCaptureTimer.Start();
        Loaded += (_, _) => StartFullWidthCaptureLoop();
        Closed += (_, _) => _audioCapture.Dispose();
        Closed += (_, _) => _dmxOutput.Dispose();
        Closed += (_, _) => _screenMirrorTimer.Stop();
        Closed += (_, _) => _narrowMirrorTimer.Stop();
        Closed += (_, _) => _bpmReadTimer.Stop();
        Closed += (_, _) => _topCaptureTimer.Stop();
        Closed += (_, _) => _cornerCaptureTimer.Stop();
        Closed += (_, _) => _deck2CornerCaptureTimer.Stop();
        Closed += (_, _) => StopFullWidthCaptureLoop();
    }

    private void StartFullWidthCaptureLoop()
    {
        _fullWidthCaptureLoop = new CaptureLoop("Full-width capture", 1000 / 60, () =>
        {
            if (_fullWidthCapture.Capture() is { } frame)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () => FullWidthCaptureImage.Source = frame);
        });
    }

    private void StopFullWidthCaptureLoop() => _fullWidthCaptureLoop?.Stop();

    private void ApplyTopCaptureFrame(BitmapSource frame, Color deck1Color, Color deck2Color)
    {
        TopCaptureImage.Source = frame;

        var deck1IsMeasureColor = MeasureStartColors.Contains(deck1Color);
        Deck1MarkerIndicator.Fill = new SolidColorBrush(deck1IsMeasureColor ? Colors.Red : Color.FromRgb(0x44, 0x44, 0x44));
        Deck1MarkerText.Text = deck1IsMeasureColor ? "Red" : "--";
        Deck1ColorSwatch.Fill = new SolidColorBrush(deck1Color);
        Deck1ColorText.Text = $"#{deck1Color.R:X2}{deck1Color.G:X2}{deck1Color.B:X2}";

        // Whichever deck is currently master (same rule as UpdateMasterBpm)
        // is the one whose measure-start marks beat 1 of a new measure - the
        // other deck's marker is ignored for beat-tracking purposes even
        // while it's still being counted above.
        var deck2IsMaster = IsDeck2Master();

        // Rising edge only - the marker stays this color for as long as the
        // measure indicator is lit, so this must fire once per transition
        // into it, not once per tick it happens to hold.
        if (deck1IsMeasureColor && !_wasDeck1MeasureColor)
        {
            Rig.Deck1MeasureCount++;
            Deck1MeasureText.Text = $"Deck 1 measures: {Rig.Deck1MeasureCount}";
            if (!deck2IsMaster)
                ResetBeatCounter();
        }
        _wasDeck1MeasureColor = deck1IsMeasureColor;

        var deck2IsMeasureColor = MeasureStartColors.Contains(deck2Color);
        Deck2MarkerIndicator.Fill = new SolidColorBrush(deck2IsMeasureColor ? Colors.Red : Color.FromRgb(0x44, 0x44, 0x44));
        Deck2MarkerText.Text = deck2IsMeasureColor ? "Red" : "--";
        Deck2ColorSwatch.Fill = new SolidColorBrush(deck2Color);
        Deck2ColorText.Text = $"#{deck2Color.R:X2}{deck2Color.G:X2}{deck2Color.B:X2}";

        if (deck2IsMeasureColor && !_wasDeck2MeasureColor)
        {
            Rig.Deck2MeasureCount++;
            Deck2MeasureText.Text = $"Deck 2 measures: {Rig.Deck2MeasureCount}";
            if (deck2IsMaster)
                ResetBeatCounter();
        }
        _wasDeck2MeasureColor = deck2IsMeasureColor;

        UpdateBeatNumber();
        UpdateBassFaderEffect();
        UpdateDiagonalFaderEffect();
        UpdateTopBottomFaderEffect();
        UpdateLightBarFaderEffect();
        UpdateLightBarDownFaderEffect();
    }

    // Reads both deck BPM circles out of whatever the mirror last captured.
    // Guarded by _isReadingBpm rather than just relying on the timer's own
    // interval, since a slow OCR pass (first run especially, while the OCR
    // engine warms up) could otherwise overlap with the next tick.
    private async Task ReadBpmAsync()
    {
        if (_isReadingBpm || !_bpmReader.IsAvailable)
            return;

        if (RekordboxMirrorImage.Source is not BitmapSource capture)
            return;

        _isReadingBpm = true;
        try
        {
            await _bpmReader.ReadAsync(capture);

            Rig.Deck1Bpm = _bpmReader.Deck1Bpm;
            Rig.Deck2Bpm = _bpmReader.Deck2Bpm;

            Deck1BpmText.Text = _bpmReader.Deck1Bpm?.ToString("0.00") ?? "--";
            Deck2BpmText.Text = _bpmReader.Deck2Bpm?.ToString("0.00") ?? "--";
            Deck1BpmDebugImage.Source = _bpmReader.Deck1DebugPreview;
            Deck2BpmDebugImage.Source = _bpmReader.Deck2DebugPreview;

            UpdateMasterBpm();
        }
        finally
        {
            _isReadingBpm = false;
        }
    }

    // Both banners share the same spot on screen, so only one shows at a
    // time - DMX takes priority when both conditions are true.
    private void UpdateStatusBanners()
    {
        var dmxNotDetected = !_dmxOutput.IsConnected;
        var bothPaused = !Rig.Deck1IsPlaying && !Rig.Deck2IsPlaying;

        DmxNotDetectedText.Visibility = dmxNotDetected ? Visibility.Visible : Visibility.Collapsed;
        RekordboxPausedText.Visibility = bothPaused && !dmxNotDetected ? Visibility.Visible : Visibility.Collapsed;
    }

    // The master deck must actually be playing - a playing deck always
    // outranks a paused one regardless of volume. Between 2 playing decks
    // (or 2 paused ones, or a volume tie), the higher channel volume wins;
    // deck 1 is the default when that doesn't settle it either (both
    // paused, or both playing at equal volume).
    private bool IsDeck2Master()
    {
        if (Rig.Deck1IsPlaying && !Rig.Deck2IsPlaying)
            return false;
        if (Rig.Deck2IsPlaying && !Rig.Deck1IsPlaying)
            return true;

        return Rig.Deck1IsPlaying && Rig.Deck2IsPlaying && Rig.Deck2Volume > Rig.Deck1Volume;
    }

    // Called from both the (slower) BPM read and the (faster) volume read,
    // so this stays current regardless of which one just changed.
    private void UpdateMasterBpm()
    {
        var deck2IsMaster = IsDeck2Master();
        Rig.MasterBpm = deck2IsMaster ? Rig.Deck2Bpm : Rig.Deck1Bpm;
        MasterBpmText.Text = Rig.MasterBpm?.ToString("0.00") ?? "--";
        MasterDeckText.Text = deck2IsMaster ? "Deck 2" : "Deck 1";
    }

    // Called the instant the master deck's measure-start marker fires -
    // snaps the beat display straight to 1 and flashes it immediately,
    // rather than waiting on UpdateBeatNumber's own elapsed-time math to
    // land on 1 (which it will anyway, but not necessarily before this
    // tick's redraw, and not with a guaranteed flash if it happened to
    // already be showing 1).
    private void ResetBeatCounter()
    {
        _masterMeasureStartTime = DateTime.UtcNow;
        _lastDisplayedBeat = 1;
        _bassFaderLastBucket = null;
        BeatNumberText.Text = "1";
        FlashBeatNumber();
    }

    // Computes which of the 4 beats in the current measure we're on purely
    // from elapsed time since the last master-deck measure-start against the
    // master BPM, and flashes the display whenever that number advances.
    private void UpdateBeatNumber()
    {
        if (!Rig.Deck1IsPlaying && !Rig.Deck2IsPlaying)
        {
            BeatNumberText.Text = "-";
            // Sentinel so playback resuming on the same beat number it
            // paused on still flashes, instead of silently matching
            // _lastDisplayedBeat and skipping the flash.
            _lastDisplayedBeat = 0;
            return;
        }

        if (_masterMeasureStartTime is not { } measureStart || Rig.MasterBpm is not > 0)
        {
            BeatNumberText.Text = "-";
            return;
        }

        var beatIntervalSeconds = 60.0 / Rig.MasterBpm.Value;
        var elapsedSeconds = (DateTime.UtcNow - measureStart).TotalSeconds;
        var beatNumber = (int)(elapsedSeconds / beatIntervalSeconds) % 4 + 1;

        if (beatNumber == _lastDisplayedBeat)
            return;

        _lastDisplayedBeat = beatNumber;
        BeatNumberText.Text = beatNumber.ToString();
        FlashBeatNumber();
    }

    // The bass page's first fader: flashes the 4 wall par lights + wall
    // striplight on a schedule set by its BeatInterval (1 = every beat, 2 =
    // beats 1 & 3, 4 = beat 1 only, 0.5 = twice per beat) rather than a
    // fixed per-beat rate. Uses the same continuous beat-position math as
    // UpdateBeatNumber but without rounding down to a whole beat number -
    // floor(beatPosition / BeatInterval) changing is what "N beats apart"
    // means even for a fractional interval, and ResetBeatCounter's own
    // reset already lands this back on beat 1 of the new measure every
    // time, which is exactly what "interval 4 fires on beat 1" needs
    // without any special-casing here.
    private double? _bassFaderLastBucket;
    private double? _diagonalFaderLastBucket;
    private double? _topBottomFaderLastBucket;
    private double? _lightBarFaderLastBucket;
    private double? _lightBarDownFaderLastBucket;

    // Reference point for the flash color's slowly-cycling hue - see
    // UpdateBassFaderEffect. A full rotation every 30 seconds is "slowly."
    private readonly DateTime _bassFaderHueEpoch = DateTime.UtcNow;
    private const double BassFaderHueDegreesPerSecond = 360.0 / 30.0;

    /// <summary>
    /// Shared gate for every beat-scheduled manual fader effect: only fires
    /// (returns true) once per bucket, where a "bucket" is
    /// floor(beatPosition / fader.BeatInterval) - see UpdateBassFaderEffect's
    /// original comment for why that one formula covers every interval,
    /// fractional or not. <paramref name="lastBucket"/> is the calling
    /// effect's own bucket-tracking field (each fader needs its own, since
    /// they can have different BeatInterval values and fire independently).
    /// <paramref name="gapSeconds"/> comes out as the real time between this
    /// trigger and the next one, for scaling a decay to match.
    /// </summary>
    private bool ShouldFireFaderEffect(MidiFader fader, ref double? lastBucket, out double gapSeconds)
    {
        gapSeconds = 0;

        if (!Rig.Deck1IsPlaying && !Rig.Deck2IsPlaying)
        {
            // Sentinel: playback resuming on the same bucket it paused on
            // should still fire, not silently match the stale value.
            lastBucket = null;
            return false;
        }

        if (_masterMeasureStartTime is not { } measureStart || Rig.MasterBpm is not > 0)
            return false;

        // Fader all the way down means this effect is off entirely, not
        // just running at some minimal intensity.
        if (fader.Value <= 0 || fader.BeatInterval <= 0)
            return false;

        var beatIntervalSeconds = 60.0 / Rig.MasterBpm.Value;
        var elapsedSeconds = (DateTime.UtcNow - measureStart).TotalSeconds;
        var beatPosition = elapsedSeconds / beatIntervalSeconds;
        var bucket = Math.Floor(beatPosition / fader.BeatInterval);
        gapSeconds = fader.BeatInterval * beatIntervalSeconds;

        if (lastBucket is not null && bucket == lastBucket)
            return false;

        lastBucket = bucket;
        return true;
    }

    // How much of the gap until the next flash a fader effect's decay
    // actually spans - just under the full gap, so it visually reaches
    // black right before the next one rather than exactly overlapping it.
    private const double BassFlashDecayFraction = 0.9;

    private void UpdateBassFaderEffect()
    {
        if (!ManualEffectsEnabled || _selectedFaderPage != FaderPage.Bass)
            return;

        var fader = Rig.BassFaders[0];
        if (!ShouldFireFaderEffect(fader, ref _bassFaderLastBucket, out var gapSeconds))
            return;

        // Hue slowly cycles over real time (100% saturation, 50% lightness -
        // the same HSL convention the automated effects use for color),
        // sampled fresh at the moment each flash actually fires - the same
        // effect as animating it continuously, since color is never
        // observed except at that instant anyway. Value still scales
        // brightness on top of that (a fader at 50% flashes to 50% of
        // whatever that hue's full-brightness color is), same reasoning as
        // before: ParLightFlashAnimator has no intensity parameter of its
        // own, so scaling FlashColor itself is what stands in for one.
        var hueDegrees = (DateTime.UtcNow - _bassFaderHueEpoch).TotalSeconds * BassFaderHueDegreesPerSecond % 360.0;
        var hueColor = HslColor.FromHsl(hueDegrees, 1.0, 0.5);
        var flashColor = ScaleColor(hueColor, fader.Value);
        var decaySeconds = BassFlashDecayFraction * gapSeconds;

        // The wall striplight is a moving head - a manual effect flashing
        // it should always find it aimed forward, not wherever the
        // automated pattern sequencer (or another manual effect) last left
        // it pointed.
        Rig.WallStripLight.FacingAngleDegrees = WallStripLight.ForwardAngleDegrees;

        _parFlashAnimator.FlashColor = flashColor;
        _parFlashAnimator.FlashAll(decaySeconds);
        _wallStripFlashAnimator.FlashColor = flashColor;
        _wallStripFlashAnimator.FlashAll(decaySeconds);
    }

    // The 4th bass fader: flashes just the wall striplight, aimed forward,
    // once per this fader's own beat interval.
    private void UpdateLightBarFaderEffect() =>
        UpdateLightBarFaderEffect(Rig.BassFaders[3], ref _lightBarFaderLastBucket, WallStripLight.ForwardAngleDegrees);

    // The 5th bass fader: identical to the 4th, except the striplight is
    // aimed straight down (90deg) instead of forward - a different, steeper
    // angle than the shallow WallStripLight.DownAngleDegrees the automated
    // pattern sequencer uses for its own "down" position.
    private void UpdateLightBarDownFaderEffect() =>
        UpdateLightBarFaderEffect(Rig.BassFaders[4], ref _lightBarDownFaderLastBucket, 90.0);

    // Bass faders 4 and 5 both claim the wall striplight's facing angle
    // (forward vs. straight down) - the physical fixture can only point one
    // way at once, so whichever one just turned on wins and the other gets
    // force-zeroed and flagged (MidiFader.Invalidate) until its own real
    // fader is physically moved back to 0%.
    private void SetupFaderConflicts()
    {
        RegisterConflict(Rig.BassFaders[3], Rig.BassFaders[4]);
    }

    private static void RegisterConflict(MidiFader a, MidiFader b)
    {
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MidiFader.Value) && a.Value > 0 && b.Value > 0)
                b.Invalidate();
        };
        b.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MidiFader.Value) && b.Value > 0 && a.Value > 0)
                a.Invalidate();
        };
    }

    private void UpdateLightBarFaderEffect(MidiFader fader, ref double? lastBucket, double facingAngleDegrees)
    {
        if (!ManualEffectsEnabled || _selectedFaderPage != FaderPage.Bass)
            return;

        if (!ShouldFireFaderEffect(fader, ref lastBucket, out var gapSeconds))
            return;

        Rig.WallStripLight.FacingAngleDegrees = facingAngleDegrees;

        // Same slowly-cycling-hue color logic as fader 1 (see its own
        // comment), sharing that same hue clock so these effects land on
        // matching colors if they ever fire at the same moment.
        var hueDegrees = (DateTime.UtcNow - _bassFaderHueEpoch).TotalSeconds * BassFaderHueDegreesPerSecond % 360.0;
        var hueColor = HslColor.FromHsl(hueDegrees, 1.0, 0.5);
        var flashColor = ScaleColor(hueColor, fader.Value);
        var decaySeconds = BassFlashDecayFraction * gapSeconds;

        _wallStripFlashAnimator.FlashColor = flashColor;
        _wallStripFlashAnimator.FlashAll(decaySeconds);
    }

    // Alternates which diagonal pair of the 4 wall par lights flashes each
    // time this fader's own beat interval comes around - top-left/bottom-
    // right on one trigger, top-right/bottom-left on the next.
    private void UpdateDiagonalFaderEffect()
    {
        if (!ManualEffectsEnabled || _selectedFaderPage != FaderPage.Bass)
            return;

        var fader = Rig.BassFaders[1];
        if (!ShouldFireFaderEffect(fader, ref _diagonalFaderLastBucket, out var gapSeconds))
            return;

        var flashColor = ScaleColor(Colors.White, fader.Value);
        var decaySeconds = BassFlashDecayFraction * gapSeconds;

        var isEvenBucket = (long)_diagonalFaderLastBucket! % 2 == 0;
        var diagonal = isEvenBucket
            ? new[] { Rig.TopLeftPar, Rig.BottomRightPar }
            : new[] { Rig.TopRightPar, Rig.BottomLeftPar };

        _parFlashAnimator.FlashColor = flashColor;
        _parFlashAnimator.Flash(diagonal, decaySeconds);
    }

    // Alternates between the top pair and bottom pair of the 4 wall par
    // lights each time this fader's own beat interval comes around - same
    // alternation mechanism as UpdateDiagonalFaderEffect, just a different
    // pairing.
    private void UpdateTopBottomFaderEffect()
    {
        if (!ManualEffectsEnabled || _selectedFaderPage != FaderPage.Bass)
            return;

        var fader = Rig.BassFaders[2];
        if (!ShouldFireFaderEffect(fader, ref _topBottomFaderLastBucket, out var gapSeconds))
            return;

        var flashColor = ScaleColor(Colors.White, fader.Value);
        var decaySeconds = BassFlashDecayFraction * gapSeconds;

        var isEvenBucket = (long)_topBottomFaderLastBucket! % 2 == 0;
        var pair = isEvenBucket
            ? new[] { Rig.TopLeftPar, Rig.TopRightPar }
            : new[] { Rig.BottomLeftPar, Rig.BottomRightPar };

        _parFlashAnimator.FlashColor = flashColor;
        _parFlashAnimator.Flash(pair, decaySeconds);
    }

    private static Color ScaleColor(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private void FlashBeatNumber()
    {
        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeatNumberText.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    // Enumerating monitors via System.Windows.Forms.Screen and picking the
    // smallest Bounds.Left didn't actually land on the leftmost monitor in
    // practice (likely a DPI/timing quirk reading Left/Top this early in
    // startup) - a direct, confirmed-correct offset from the primary
    // monitor is simpler and actually works.
    private const int LeftmostMonitorOffsetPixels = -4320;

    private void PositionFullScreenOnLeftmostMonitor()
    {
        WindowState = WindowState.Normal;
        Left = LeftmostMonitorOffsetPixels;
        Top = 0;
        WindowState = WindowState.Maximized;
    }

    private static Color SamplePixelColor(BitmapSource source, int x, int y)
    {
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(source, new Int32Rect(x, y, 1, 1));
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static void PositionRegionOverlay(System.Windows.Shapes.Rectangle overlay, Int32Rect region)
    {
        Canvas.SetLeft(overlay, region.X);
        Canvas.SetTop(overlay, region.Y);
        overlay.Width = region.Width;
        overlay.Height = region.Height;
    }

    private void DmxPortComboBox_DropDownOpened(object sender, EventArgs e)
    {
        var selected = DmxPortComboBox.SelectedItem as string;

        DmxPortComboBox.ItemsSource = DmxOutputService.GetAvailablePorts();

        if (selected is not null && DmxPortComboBox.Items.Contains(selected))
            DmxPortComboBox.SelectedItem = selected;
        else if (DmxPortComboBox.Items.Count > 0)
            DmxPortComboBox.SelectedIndex = 0;
    }

    // Runs once on startup: if exactly the kind of dongle this app expects
    // (an FTDI-based Open DMX USB interface) is already plugged in, connect
    // to it without making the user pick it from the dropdown and click
    // Connect every time. Silent on failure/absence - this is a convenience
    // for the common case, not a requirement, and the dropdown/button are
    // still there for picking a specific device or reconnecting by hand.
    private void TryAutoConnectDmx()
    {
        var ports = DmxOutputService.GetAvailablePorts();
        DmxPortComboBox.ItemsSource = ports;

        if (ports.Length == 0)
            return;

        DmxPortComboBox.SelectedIndex = 0;
        ConnectDmx(ports[0]);
    }

    // Each page is its own independent bank of 8 faders (Rig.BassFaders/
    // LaserFaders/LedStripFaders/ComboFaders) - only the selected page's
    // bank is ever bound to FaderItemsControl, and only the Bass page has
    // an actual effect wired up yet (see UpdateBassFaderEffect's own guard).
    private Wpf.Ui.Controls.Button? _selectedFaderPageButton;
    private FaderPage _selectedFaderPage;

    private void FaderPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button button)
            return;

        var page = button == FaderPageBassButton ? FaderPage.Bass
            : button == FaderPageLaserButton ? FaderPage.Laser
            : button == FaderPageLedStripButton ? FaderPage.LedStrip
            : FaderPage.Combo;

        SelectFaderPage(button, page);
    }

    private void SelectFaderPage(Wpf.Ui.Controls.Button button, FaderPage page)
    {
        _selectedFaderPageButton?.ClearValue(BackgroundProperty);
        button.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9E));
        _selectedFaderPageButton = button;

        _selectedFaderPage = page;
        FaderItemsControl.ItemsSource = page switch
        {
            FaderPage.Bass => Rig.BassFaders,
            FaderPage.Laser => Rig.LaserFaders,
            FaderPage.LedStrip => Rig.LedStripFaders,
            _ => Rig.ComboFaders,
        };
    }

    private Wpf.Ui.Controls.Button? _selectedLightingModeButton;

    private void LightingModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button button)
            return;

        var mode = button == FullAutoModeButton ? LightingMode.FullAuto
            : button == SemiAutoModeButton ? LightingMode.SemiAuto
            : LightingMode.Manual;

        SetLightingMode(mode, button);
    }

    private void SetLightingMode(LightingMode mode, Wpf.Ui.Controls.Button button)
    {
        _lightingMode = mode;
        LightingModeText.Text = mode switch
        {
            LightingMode.FullAuto => "Full Auto",
            LightingMode.SemiAuto => "Semi Auto",
            _ => "Manual",
        };

        _selectedLightingModeButton?.ClearValue(BackgroundProperty);
        button.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9E));
        _selectedLightingModeButton = button;

        // Manual means the automated animations are off, full stop - not
        // just "won't start again," anything already mid-animation needs to
        // be cut immediately rather than left to decay on its own schedule.
        if (mode == LightingMode.Manual)
        {
            _lavaLampAnimator.SetActive(false);
            _laserAnimator.SetActive(false);
            _laserAnimator.SetChordMode(false);
            _floorTopLeftAmbientAnimator.SetActive(false);
            _floorTopRightAmbientAnimator.SetActive(false);
            _floorBottomLeftAmbientAnimator.SetActive(false);
            _floorBottomRightAmbientAnimator.SetActive(false);
        }
    }

    private void DmxConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmxOutput.IsConnected)
        {
            _dmxOutput.Disconnect();
            DmxConnectButton.Content = "Connect";
            DmxStatusText.Text = "Not connected";
            UpdateStatusBanners();
            return;
        }

        if (DmxPortComboBox.SelectedItem is not string portName)
        {
            DmxStatusText.Text = "Select a port first";
            return;
        }

        ConnectDmx(portName);
    }

    private void ConnectDmx(string portName)
    {
        try
        {
            _dmxOutput.Connect(portName);
            DmxConnectButton.Content = "Disconnect";
            DmxStatusText.Text = $"Connected on {portName}";
        }
        catch (Exception ex)
        {
            DmxStatusText.Text = $"Failed: {ex.Message}";
        }

        UpdateStatusBanners();
    }

    private void OnDmxConnectionLost(Exception ex)
    {
        DmxConnectButton.Content = "Connect";
        DmxStatusText.Text = $"Disconnected: {ex.Message}";
        UpdateStatusBanners();
    }

    /// <summary>
    /// Starts WASAPI loopback capture (the device is chosen automatically -
    /// see <see cref="AudioCaptureService.Start"/>) and feeds every buffer
    /// through the bass onset detector, flashing the par lights on each
    /// detected hit.
    /// </summary>
    private void StartListening()
    {
        try
        {
            _audioCapture.BufferAvailable += OnAudioBufferAvailable;
            _audioCapture.Start();
            AudioDeviceStatusText.Text = _audioCapture.CurrentDevice is { } device
                ? $"Listening on {device.FriendlyName}"
                : "Listening";
        }
        catch (Exception ex)
        {
            // No default playback device, no active audio session, etc. The
            // visualizer still works without live audio; just log for now.
            System.Diagnostics.Debug.WriteLine($"Audio capture failed to start: {ex.Message}");
            AudioDeviceStatusText.Text = $"Failed: {ex.Message}";
        }
    }

    private void OnAudioBufferAvailable(object? sender, AudioBufferEventArgs e)
    {
        // Normalizes overall loudness before anything else sees these
        // samples, so every detector below - all of which use fixed
        // absolute-level floors tuned against typical consumer playback
        // loudness - stays consistently sensitive regardless of how loud
        // the current source/session actually is (DJ software in
        // particular applies no loudness normalization of its own, unlike
        // streaming services and browsers).
        _gainControl.Apply(e.Samples, e.SampleRate);

        _bassOnsetDetector ??= CreateBassOnsetDetector(e.SampleRate);
        _bassOnsetDetector.Process(e.Samples);

        _impactDetector ??= CreateImpactDetector(e.SampleRate);
        _impactDetector.Process(e.Samples);

        // BasslineActivityTracker needs 3 consecutive regular hits before it
        // calls a groove "steady", which leaves a warm-up window at the start
        // of every groove (and after any gap) where bass hits are clearly
        // happening but that hasn't been confirmed yet. A buildup is
        // musically a breakdown - the kick/bass drops out entirely - so any
        // bass hit at all recently is reason enough to suppress it, without
        // waiting on regularity.
        var recentlyHadBassHit = (DateTime.UtcNow - _lastBassHitTime).TotalSeconds < RecentBassHitWindowSeconds;

        _percussionBuildupDetector ??= CreatePercussionBuildupDetector();
        _percussionBuildupDetector.Process(e.Samples, e.SampleRate, _basslineTracker.IsActive || recentlyHadBassHit);

        _melodicPassageDetector.Process(e.Samples, e.SampleRate, _basslineTracker.IsActive);

        // Distinguishes one isolated, sustained chord from a busy
        // arpeggio/vocal line within the melodic passage - only matters
        // while a passage is actually active. ChordOnset (subscribed in the
        // constructor) fires the laser's single-beam hit; here we only need
        // to notice when a sustained chord ends (either the passage itself
        // ended, or note activity picked back up into a real arpeggio) so
        // the laser can resume its normal fan.
        _chordSustainDetector.Process(e.Samples, e.SampleRate, _melodicPassageDetector.IsActive);
        var isChordActive = _chordSustainDetector.IsSustainedChord;
        if (isChordActive != _wasSustainedChord)
        {
            if (!isChordActive)
                _laserAnimator.SetChordMode(false);
            _wasSustainedChord = isChordActive;

            // TEMP: debug status text, for correlating against real audio. Remove when done.
            Dispatcher.BeginInvoke(() => ChordStatusText.Text = isChordActive ? "Active" : "Inactive");
        }

        // Brightness follows the mid arpeggios mostly and the bassline a
        // little - a plain double, so it's safe to set directly from this
        // thread without dispatching; the animators just read it on their
        // own render tick.
        var bufferSeconds = (double)e.Samples.Length / e.SampleRate;
        _effectBrightness.Process(_melodicPassageDetector.MidHighLevel, _bassOnsetDetector.SmoothedLevel, bufferSeconds);
        _lavaLampAnimator.BrightnessFactor = _effectBrightness.Brightness;
        _laserAnimator.BrightnessFactor = _effectBrightness.Brightness;

        var wantsLavaLamp = _melodicPassageDetector.IsActive;
        if (wantsLavaLamp != _wasMelodicPassageActive)
        {
            _wasMelodicPassageActive = wantsLavaLamp;
            Dispatcher.BeginInvoke(() =>
            {
                // Reflects what the audio actually wants regardless of mode
                // (WaveStatusText below still shows the real detector
                // state), but only actually drives the animators when
                // automation is on.
                var effectiveActive = wantsLavaLamp && AutomationEnabled;
                _lavaLampAnimator.SetActive(effectiveActive);
                _laserAnimator.SetActive(effectiveActive);

                // TEMP: debug status text, for correlating against real audio. Remove when done.
                WaveStatusText.Text = wantsLavaLamp ? "Active" : "Inactive";
            });
        }

        _loudnessTracker.Process(e.Samples, e.SampleRate);

        // Animation speed follows the music's overall energy - same
        // direct-set, no-dispatcher reasoning as brightness above.
        _effectSpeed.Process(_loudnessTracker.SmoothedRms, bufferSeconds);
        _lavaLampAnimator.SpeedFactor = _effectSpeed.SpeedFactor;
        _laserAnimator.SpeedFactor = _effectSpeed.SpeedFactor;

        var isLoud = _loudnessTracker.IsLoud;
        if (isLoud != _wasLoud)
        {
            _wasLoud = isLoud;
            Dispatcher.BeginInvoke(() =>
            {
                var effectiveLoud = isLoud && AutomationEnabled;
                _floorTopLeftAmbientAnimator.SetActive(effectiveLoud);
                _floorTopRightAmbientAnimator.SetActive(effectiveLoud);
                _floorBottomLeftAmbientAnimator.SetActive(effectiveLoud);
                _floorBottomRightAmbientAnimator.SetActive(effectiveLoud);
            });
        }
    }

    private BassOnsetDetector CreateBassOnsetDetector(int sampleRate)
    {
        var detector = new BassOnsetDetector(sampleRate);
        detector.BassHitDetected += (_, _) => Dispatcher.BeginInvoke(OnBassHit);
        return detector;
    }

    private ImpactDetector CreateImpactDetector(int sampleRate)
    {
        var detector = new ImpactDetector(sampleRate);
        detector.ImpactDetected += (_, args) => Dispatcher.BeginInvoke(() => OnImpact(args.IsExtreme));
        return detector;
    }

    private PercussionBuildupDetector CreatePercussionBuildupDetector()
    {
        var detector = new PercussionBuildupDetector();
        detector.Hit += (_, args) =>
        {
            var decaySeconds = PercussionFlashDecayFraction * args.GapSeconds;

            Dispatcher.BeginInvoke(() =>
            {
                if (!AutomationEnabled)
                    return;

                _floorTopLeftAmbientAnimator.FlashColor = Colors.White;
                _floorTopRightAmbientAnimator.FlashColor = Colors.White;
                _floorBottomLeftAmbientAnimator.FlashColor = Colors.White;
                _floorBottomRightAmbientAnimator.FlashColor = Colors.White;
                _floorTopLeftAmbientAnimator.Flash(decaySeconds, PercussionFlashPeakIntensity);
                _floorTopRightAmbientAnimator.Flash(decaySeconds, PercussionFlashPeakIntensity);
                _floorBottomLeftAmbientAnimator.Flash(decaySeconds, PercussionFlashPeakIntensity);
                _floorBottomRightAmbientAnimator.Flash(decaySeconds, PercussionFlashPeakIntensity);
            });
        };
        return detector;
    }

    // Keeps each percussion-triggered flash decaying well within the gap
    // before the next hit, so a speeding-up buildup reads as increasingly
    // sharp, distinct pulses instead of blurring into one continuous glow
    // once hits arrive faster than a fixed decay time could ever clear.
    private const double PercussionFlashDecayFraction = 0.6;

    // Floor par lights peak at 60% brightness for a percussion-buildup
    // flash rather than full white - a touch softer than the whole-rig
    // impact flash (which stays full brightness).
    private const double PercussionFlashPeakIntensity = 0.6;

    // Once the whole-rig white flash fires, suppress it from firing again for
    // this long - long enough to cover the handful of hits it typically takes
    // a bassline to lock into "steady" (see BasslineActivityTracker), so the
    // buildup into a groove doesn't strobe white on every one of those hits.
    private const double RigFlashCooldownSeconds = 3.0;

    // How long after a big flash fires the normal per-beat colored flash is
    // held back, so a bass hit landing right on top of the same moment can't
    // visually cut into the white flash's decay.
    private const double RigFlashOverrideWindowSeconds = 0.3;

    // How recently a bass hit must have landed to suppress percussion buildup
    // detection outright - comfortably wider than any plausible beat interval
    // (see BasslineActivityTracker's own MaxBeatIntervalSeconds) so it stays
    // suppressed through the gaps between hits in a normal groove, not just
    // in the instant right after one.
    private const double RecentBassHitWindowSeconds = 1.5;

    /// <summary>
    /// Registers the beat with the pattern sequencer (which rotates the
    /// active-light pattern every 8 beats) and flashes whichever par lights
    /// the current pattern selects. Also feeds the bassline tracker so it
    /// knows whether a steady groove is currently going.
    ///
    /// The wall striplight (a Rockville Motionstrip 80W) rides along with
    /// this same rotation rather than having its own logic: its head faces
    /// forward for the All pattern and down for AlternateTopBottom, and it
    /// only actually flashes for All or for AlternateTopBottom's top half -
    /// never for the bottom half or for AlternateDiagonals, matching how a
    /// physical moving head mounted with the wall pars would only make
    /// sense to fire when aimed at a par grouping it's actually part of.
    ///
    /// The floor par lights don't echo per-bass-hit flashes at all - they're
    /// reserved for percussion buildups and the big whole-rig white flash.
    ///
    /// The real fixture takes real time to physically rotate, so its facing
    /// isn't just set reactively for whatever pattern is active right now -
    /// on the last beat of a phrase, <see cref="ParLightPatternSequencer.UpcomingPattern"/>
    /// already knows what the next phrase's pattern will be, and the facing
    /// is pushed toward whatever that pattern needs immediately (right as
    /// this, the previous, beat finishes), rather than waiting for the next
    /// phrase's first beat to actually arrive. That's the whole gap between
    /// two beats to get there instead of zero.
    /// </summary>
    private void OnBassHit()
    {
        _lastBassHitTime = DateTime.UtcNow;
        _basslineTracker.RegisterBassHit();
        _patternSequencer.RegisterBeat();

        if (!AutomationEnabled)
            return;

        var justHadRigFlash = (DateTime.UtcNow - _lastRigFlashTime).TotalSeconds < RigFlashOverrideWindowSeconds;
        if (justHadRigFlash)
            return;

        var activeWallLights = _patternSequencer.GetActiveLights(Rig);
        _parFlashAnimator.FlashColor = _patternSequencer.CurrentFlashColor;
        _parFlashAnimator.Flash(activeWallLights);

        var pattern = _patternSequencer.CurrentPattern;

        if (pattern == ParLightPattern.All)
            Rig.WallStripLight.FacingAngleDegrees = WallStripLight.ForwardAngleDegrees;
        else if (pattern == ParLightPattern.AlternateTopBottom)
            Rig.WallStripLight.FacingAngleDegrees = WallStripLight.DownAngleDegrees;

        var wallStripShouldFire = pattern == ParLightPattern.All
            || (pattern == ParLightPattern.AlternateTopBottom && _patternSequencer.LastActiveWasTop);

        if (wallStripShouldFire)
        {
            _wallStripFlashAnimator.FlashColor = _patternSequencer.CurrentFlashColor;
            _wallStripFlashAnimator.FlashAll();
        }

        // Floor par lights no longer echo per-bass-hit flashes - they're
        // reserved for percussion buildups (CreatePercussionBuildupDetector)
        // and the big whole-rig white flash (OnImpact) only.

        // Known a beat early - see the method summary - so start moving now
        // rather than waiting for the upcoming pattern's own first beat.
        if (_patternSequencer.UpcomingPattern == ParLightPattern.All)
            Rig.WallStripLight.FacingAngleDegrees = WallStripLight.ForwardAngleDegrees;
        else if (_patternSequencer.UpcomingPattern == ParLightPattern.AlternateTopBottom)
            Rig.WallStripLight.FacingAngleDegrees = WallStripLight.DownAngleDegrees;
    }

    /// <summary>
    /// A sudden, dramatically loud broadband moment (crash, big isolated hit,
    /// drop). Only fires when there's no steady bassline already going -
    /// during a groove, the regular per-beat flashing already has it covered
    /// - and only once per such moment: a cooldown stops the first few hits
    /// of a bassline spinning up from each re-triggering their own flash.
    /// The floor par lights always flash full white for this. The wall par
    /// lights and every LED strip only join in for a genuinely extreme
    /// moment (abnormally quiet for the preceding few seconds, then suddenly
    /// loud - a real drop) - otherwise just the far left and right strips
    /// flash, so the full whole-rig flash stays reserved for the biggest
    /// moments.
    /// </summary>
    private void OnImpact(bool isExtreme)
    {
        if (!AutomationEnabled)
            return;

        if (_basslineTracker.IsActive)
            return;

        if ((DateTime.UtcNow - _lastRigFlashTime).TotalSeconds < RigFlashCooldownSeconds)
            return;

        _lastRigFlashTime = DateTime.UtcNow;

        // Floor par lights flash on every rig-flash moment, spiking over
        // whatever their ambient baseline currently is.
        _floorTopLeftAmbientAnimator.FlashColor = Colors.White;
        _floorTopRightAmbientAnimator.FlashColor = Colors.White;
        _floorBottomLeftAmbientAnimator.FlashColor = Colors.White;
        _floorBottomRightAmbientAnimator.FlashColor = Colors.White;
        _floorTopLeftAmbientAnimator.Flash();
        _floorTopRightAmbientAnimator.Flash();
        _floorBottomLeftAmbientAnimator.Flash();
        _floorBottomRightAmbientAnimator.Flash();

        _stripFlashAnimator.FlashColor = Colors.White;
        if (isExtreme)
        {
            // Big white flash: every strip and every par light (wall + floor).
            _stripFlashAnimator.FlashAll();

            _parFlashAnimator.FlashColor = Colors.White;
            _parFlashAnimator.FlashAll();
        }
        else
        {
            var strips = Rig.LedStrips;
            _stripFlashAnimator.Flash(new[] { strips[0], strips[^1] });
        }
    }
}
