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
    private readonly ParLightPatternSequencer _patternSequencer = new();
    private readonly BasslineActivityTracker _basslineTracker = new();
    private readonly MelodicPassageDetector _melodicPassageDetector = new();
    private readonly DmxUniverse _dmxUniverse = new();
    private readonly DmxOutputService _dmxOutput;
    private readonly ParLightDmxBinder _dmxBinder;
    private BassOnsetDetector? _bassOnsetDetector;
    private ImpactDetector? _impactDetector;
    private DateTime _lastRigFlashTime = DateTime.MinValue;
    private bool _wasMelodicPassageActive;

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

        _parFlashAnimator = new ParLightFlashAnimator(Rig.ParLights, decaySeconds: 0.22);
        _stripFlashAnimator = new LedStripFlashAnimator(Rig.LedStrips, decaySeconds: 0.35);
        _lavaLampAnimator = new LedStripLavaLampAnimator(Rig.LedStrips);

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
    /// Starts WASAPI loopback capture on the default output device and feeds
    /// every buffer through the bass onset detector, flashing the par lights
    /// on each detected hit.
    /// </summary>
    private void StartListening()
    {
        try
        {
            _audioCapture.BufferAvailable += OnAudioBufferAvailable;
            _audioCapture.Start();
        }
        catch (Exception ex)
        {
            // No default playback device, no active audio session, etc. The
            // visualizer still works without live audio; just log for now.
            System.Diagnostics.Debug.WriteLine($"Audio capture failed to start: {ex.Message}");
        }
    }

    private void OnAudioBufferAvailable(object? sender, AudioBufferEventArgs e)
    {
        _bassOnsetDetector ??= CreateBassOnsetDetector(e.SampleRate);
        _bassOnsetDetector.Process(e.Samples);

        _impactDetector ??= CreateImpactDetector(e.SampleRate);
        _impactDetector.Process(e.Samples);

        _melodicPassageDetector.Process(e.Samples, e.SampleRate, _basslineTracker.IsActive);

        var wantsLavaLamp = _melodicPassageDetector.IsActive;
        if (wantsLavaLamp != _wasMelodicPassageActive)
        {
            _wasMelodicPassageActive = wantsLavaLamp;
            Dispatcher.BeginInvoke(() => _lavaLampAnimator.SetActive(wantsLavaLamp));
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

    // Once the whole-rig white flash fires, suppress it from firing again for
    // this long - long enough to cover the handful of hits it typically takes
    // a bassline to lock into "steady" (see BasslineActivityTracker), so the
    // buildup into a groove doesn't strobe white on every one of those hits.
    private const double RigFlashCooldownSeconds = 3.0;

    // How long after a big flash fires the normal per-beat colored flash is
    // held back, so a bass hit landing right on top of the same moment can't
    // visually cut into the white flash's decay.
    private const double RigFlashOverrideWindowSeconds = 0.3;

    /// <summary>
    /// Registers the beat with the pattern sequencer (which rotates the
    /// active-light pattern every 8 beats) and flashes whichever par lights
    /// the current pattern selects. Also feeds the bassline tracker so it
    /// knows whether a steady groove is currently going.
    /// </summary>
    private void OnBassHit()
    {
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
    /// All 4 par lights always flash white. The LED strips only get the full
    /// whole-strip white flash for a genuinely extreme moment (abnormally
    /// quiet for the preceding few seconds, then suddenly loud - a real
    /// drop); otherwise just the far left and right strips flash, so the
    /// full-strip flash stays reserved for the biggest moments.
    /// </summary>
    private void OnImpact(bool isExtreme)
    {
        if (_basslineTracker.IsActive)
            return;

        if ((DateTime.UtcNow - _lastRigFlashTime).TotalSeconds < RigFlashCooldownSeconds)
            return;

        _lastRigFlashTime = DateTime.UtcNow;

        // Totally overwrite whatever the par lights/strips were doing.
        _parFlashAnimator.FlashColor = Colors.White;
        _parFlashAnimator.FlashAll();

        _stripFlashAnimator.FlashColor = Colors.White;
        if (isExtreme)
        {
            _stripFlashAnimator.FlashAll();
        }
        else
        {
            var strips = Rig.LedStrips;
            _stripFlashAnimator.Flash(new[] { strips[0], strips[^1] });
        }
    }
}
