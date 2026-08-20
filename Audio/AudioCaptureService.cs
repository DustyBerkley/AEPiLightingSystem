using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Captures whatever audio is currently playing out of the default output
/// device (WASAPI loopback) and raises mono sample buffers for analysis.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;

    // WASAPI loopback only receives packets while some stream is actively
    // rendering on the device - once every app's audio is paused/stopped and
    // the device goes fully idle, Windows' audio engine stops delivering
    // loopback data at all, and everything downstream (bass detection, the
    // melodic-passage/lava-lamp fade) freezes at its last value until real
    // audio resumes. Keeping a silent stream of our own playing at all times
    // keeps the engine "hot" so loopback keeps delivering (all-zero) packets
    // through pauses instead of stalling - the standard workaround for this
    // well-known WASAPI loopback behavior.
    private WasapiOut? _keepAliveOutput;

    public event EventHandler<AudioBufferEventArgs>? BufferAvailable;

    public bool IsCapturing => _capture is not null;

    public void Start()
    {
        if (_capture is not null)
            return;

        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Stop() clears this, but an auto-restart after an unexpected
        // stream death goes straight from OnRecordingStopped into Start()
        // without going through Stop(), so the old output could otherwise
        // leak here.
        _keepAliveOutput?.Stop();
        _keepAliveOutput?.Dispose();

        _keepAliveOutput = new WasapiOut(AudioClientShareMode.Shared, true, 200);
        _keepAliveOutput.Init(new SilenceProvider(_capture.WaveFormat));
        _keepAliveOutput.Play();

        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _keepAliveOutput?.Stop();
        _keepAliveOutput?.Dispose();
        _keepAliveOutput = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null)
            return;

        try
        {
            var samples = ConvertToMonoFloat(e.Buffer, e.BytesRecorded, _capture.WaveFormat);

            if (samples.Length > 0)
                BufferAvailable?.Invoke(this, new AudioBufferEventArgs(samples, _capture.WaveFormat.SampleRate));
        }
        catch (Exception ex)
        {
            // This runs on NAudio's dedicated capture callback thread, not the
            // UI thread - an unhandled exception here (from any of the
            // downstream detectors/animators) would otherwise unwind past
            // NAudio's callback and silently kill the capture stream, which
            // looks like "everything freezes": no more bass hits, no more
            // lava lamp updates, nothing recovers on its own since nothing
            // was left listening for the stream having died. Swallow and log
            // instead so one bad buffer can't take the whole rig down.
            System.Diagnostics.Debug.WriteLine($"Audio buffer processing failed: {ex}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _capture?.Dispose();
        _capture = null;

        // A clean Stop() call nulls this out first, so e.Exception is the
        // only way to tell "stopped because Stop() was called" apart from
        // "the capture stream died on its own" (device change, driver hiccup,
        // etc). The latter used to mean total silence forever after - nothing
        // ever restarted the stream - which read as the whole rig freezing.
        // Auto-restart so a transient hiccup recovers on its own instead.
        if (e.Exception is not null)
        {
            System.Diagnostics.Debug.WriteLine($"Audio capture stopped unexpectedly: {e.Exception}");
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio capture restart failed: {ex}");
            }
        }
    }

    private static float[] ConvertToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // WASAPI loopback on modern Windows delivers 32-bit IEEE float.
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            return Array.Empty<float>();

        var floatCount = bytesRecorded / 4;
        var floatBuffer = new float[floatCount];
        Buffer.BlockCopy(buffer, 0, floatBuffer, 0, bytesRecorded);

        if (format.Channels <= 1)
            return floatBuffer;

        var channels = format.Channels;
        var mono = new float[floatCount / channels];

        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var ch = 0; ch < channels; ch++)
                sum += floatBuffer[i * channels + ch];

            mono[i] = sum / channels;
        }

        return mono;
    }

    public void Dispose() => Stop();
}

public sealed class AudioBufferEventArgs(float[] samples, int sampleRate) : EventArgs
{
    public float[] Samples { get; } = samples;

    public int SampleRate { get; } = sampleRate;
}
