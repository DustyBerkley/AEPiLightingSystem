using System;
using System.Windows;
using System.Windows.Media;
using AEPiLightingSystem.Audio;
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
    private readonly ParLightPatternSequencer _patternSequencer = new();
    private BassOnsetDetector? _bassOnsetDetector;

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

        // All fixtures default to off; nothing lights up until an effect fires.
        foreach (var strip in Rig.LedStrips)
            strip.Fill(Colors.Black);

        Loaded += (_, _) => StartListening();
        Closed += (_, _) => _audioCapture.Dispose();
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
    }

    private BassOnsetDetector CreateBassOnsetDetector(int sampleRate)
    {
        var detector = new BassOnsetDetector(sampleRate);
        detector.BassHitDetected += (_, _) => Dispatcher.BeginInvoke(OnBassHit);
        return detector;
    }

    /// <summary>
    /// Registers the beat with the pattern sequencer (which rotates the
    /// active-light pattern every 8 beats) and flashes whichever par lights
    /// the current pattern selects.
    /// </summary>
    private void OnBassHit()
    {
        _patternSequencer.RegisterBeat();
        _parFlashAnimator.FlashColor = _patternSequencer.CurrentFlashColor;
        _parFlashAnimator.Flash(_patternSequencer.GetActiveLights(Rig));
    }
}
