using System;
using System.Windows;
using System.Windows.Media;
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

    // Split top/bottom so an extremely fast buildup can alternate between
    // them instead of always flashing all 4 floor lights together - the two
    // stay in sync (SetActive, and Flash() together) for every other case.
    private readonly ParLightAmbientAnimator _floorTopAmbientAnimator;
    private readonly ParLightAmbientAnimator _floorBottomAmbientAnimator;

    private readonly DmxUniverse _dmxUniverse = new();
    private readonly DmxOutputService _dmxOutput;
    private readonly ParLightDmxBinder _dmxBinder;
    private BassOnsetDetector? _bassOnsetDetector;
    private ImpactDetector? _impactDetector;
    private PercussionBuildupDetector? _percussionBuildupDetector;
    private DateTime _lastRigFlashTime = DateTime.MinValue;
    private DateTime _lastBassHitTime = DateTime.MinValue;
    private bool _wasMelodicPassageActive;
    private bool _wasLoud;
    private bool _wasSustainedChord;

    public LightingRig Rig { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(this);
        SystemThemeWatcher.Watch(this);

        // The theme manager/watcher reassert their own background brush around
        // load time, so pin ours after that settles instead of only in the ctor.
        Background = new SolidColorBrush(BackgroundColor);
        Loaded += (_, _) => Background = new SolidColorBrush(BackgroundColor);

        DataContext = Rig;

        _parFlashAnimator = new ParLightFlashAnimator(Rig.WallParLights, decaySeconds: 0.22);
        _stripFlashAnimator = new LedStripFlashAnimator(Rig.LedStrips, decaySeconds: 0.35);
        _lavaLampAnimator = new LedStripLavaLampAnimator(Rig.LedStrips);
        _laserAnimator = new LaserFanAnimator(Rig.Laser);

        // A chord hit only means anything visually if the melodic passage
        // has actually settled into "one sustained chord" by the time it
        // fires - otherwise it's just another attack within a busy
        // arpeggio/vocal line, which should keep using the normal fan.
        _chordSustainDetector.ChordOnset += (_, _) =>
        {
            if (_chordSustainDetector.IsSustainedChord)
                _laserAnimator.TriggerChordHit();
        };

        _floorTopAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorTopLeftPar, Rig.FloorTopRightPar });
        _floorBottomAmbientAnimator = new ParLightAmbientAnimator(new[] { Rig.FloorBottomLeftPar, Rig.FloorBottomRightPar });

        _dmxOutput = new DmxOutputService(_dmxUniverse);
        _dmxOutput.ConnectionLost += (_, ex) => Dispatcher.BeginInvoke(() => OnDmxConnectionLost(ex));
        _dmxBinder = new ParLightDmxBinder(Rig, _dmxUniverse);

        // All fixtures default to off; nothing lights up until an effect fires.
        foreach (var strip in Rig.LedStrips)
            strip.Fill(Colors.Black);

        Loaded += (_, _) => StartListening();
        Closed += (_, _) => _audioCapture.Dispose();
        Closed += (_, _) => _dmxOutput.Dispose();
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

    private void DmxConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmxOutput.IsConnected)
        {
            _dmxOutput.Disconnect();
            DmxConnectButton.Content = "Connect";
            DmxStatusText.Text = "Not connected";
            return;
        }

        if (DmxPortComboBox.SelectedItem is not string portName)
        {
            DmxStatusText.Text = "Select a port first";
            return;
        }

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
    }

    private void OnDmxConnectionLost(Exception ex)
    {
        DmxConnectButton.Content = "Connect";
        DmxStatusText.Text = $"Disconnected: {ex.Message}";
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
        if (_wasSustainedChord && !_chordSustainDetector.IsSustainedChord)
            _laserAnimator.SetChordMode(false);
        _wasSustainedChord = _chordSustainDetector.IsSustainedChord;

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
                _lavaLampAnimator.SetActive(wantsLavaLamp);
                _laserAnimator.SetActive(wantsLavaLamp);
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
                _floorTopAmbientAnimator.SetActive(isLoud);
                _floorBottomAmbientAnimator.SetActive(isLoud);
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
                _floorTopAmbientAnimator.Flash(decaySeconds);
                _floorBottomAmbientAnimator.Flash(decaySeconds);
            });
        };
        return detector;
    }

    // Keeps each percussion-triggered flash decaying well within the gap
    // before the next hit, so a speeding-up buildup reads as increasingly
    // sharp, distinct pulses instead of blurring into one continuous glow
    // once hits arrive faster than a fixed decay time could ever clear.
    private const double PercussionFlashDecayFraction = 0.6;

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
    /// </summary>
    private void OnBassHit()
    {
        _lastBassHitTime = DateTime.UtcNow;
        _basslineTracker.RegisterBassHit();
        _patternSequencer.RegisterBeat();

        var justHadRigFlash = (DateTime.UtcNow - _lastRigFlashTime).TotalSeconds < RigFlashOverrideWindowSeconds;
        if (justHadRigFlash)
            return;

        _parFlashAnimator.FlashColor = _patternSequencer.CurrentFlashColor;
        _parFlashAnimator.Flash(_patternSequencer.GetActiveLights(Rig));
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
        if (_basslineTracker.IsActive)
            return;

        if ((DateTime.UtcNow - _lastRigFlashTime).TotalSeconds < RigFlashCooldownSeconds)
            return;

        _lastRigFlashTime = DateTime.UtcNow;

        // Floor par lights flash on every rig-flash moment, spiking over
        // whatever their ambient baseline currently is.
        _floorTopAmbientAnimator.Flash();
        _floorBottomAmbientAnimator.Flash();

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
