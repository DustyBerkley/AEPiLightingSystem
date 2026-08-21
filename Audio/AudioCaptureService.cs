using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Captures whatever audio is currently playing out of the actual physical
/// speakers (WASAPI loopback) and raises mono sample buffers for analysis.
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

    /// <summary>The device currently being captured from, once started - shown in the UI so a mismatch is diagnosable at a glance.</summary>
    public MMDevice? CurrentDevice { get; private set; }

    public void Start()
    {
        if (_capture is not null)
            return;

        var device = FindPreferredDevice();

        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Stop() clears this, but an auto-restart after an unexpected
        // stream death goes straight from OnRecordingStopped into Start()
        // without going through Stop(), so the old output could otherwise
        // leak here.
        _keepAliveOutput?.Stop();
        _keepAliveOutput?.Dispose();

        _keepAliveOutput = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
        _keepAliveOutput.Init(new SilenceProvider(_capture.WaveFormat));
        _keepAliveOutput.Play();

        _capture.StartRecording();
        CurrentDevice = device;
    }

    /// <summary>
    /// Picks the device to capture from. Deliberately does not just trust
    /// Windows' "default" playback device - a DJ audio interface (or
    /// similar) commonly claims default-device status the moment it's
    /// connected, even when the DJ software actually sends its audio out
    /// through that hardware's own ASIO driver, which bypasses Windows'
    /// audio engine entirely and never appears as loopback-visible data on
    /// that WASAPI endpoint. The physical speakers - whatever is actually
    /// audible - are what should be captured, so a device literally named
    /// "Speakers" is preferred outright over trusting default status.
    /// </summary>
    private static MMDevice FindPreferredDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        var speakerDevice = devices.FirstOrDefault(
            d => d.FriendlyName.Contains("Speaker", StringComparison.OrdinalIgnoreCase));

        return speakerDevice ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _keepAliveOutput?.Stop();
        _keepAliveOutput?.Dispose();
        _keepAliveOutput = null;
        CurrentDevice = null;
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

    private static bool _loggedUnsupportedFormat;

    private static float[] ConvertToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // Onboard audio (Realtek etc.) almost always exposes a 32-bit IEEE
        // float shared-mode mix format, but that's not universal - many pro
        // audio interfaces (DJ controllers/mixers among them) expose 16- or
        // 24-bit integer PCM instead. This used to just silently return an
        // empty array for anything but float, with no indication to the
        // user at all - "device selected correctly, meter never moves" is
        // exactly what that looks like.
        //
        // Multi-channel/pro devices commonly report WaveFormatExtensible
        // rather than a plain WaveFormat - the real sample type there is in
        // SubFormat, not BitsPerSample/Encoding on the wrapper itself, so
        // guessing float vs PCM from just the bit depth would silently
        // guess wrong for exactly this kind of hardware.
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)
                encoding = WaveFormatEncoding.IeeeFloat;
            else if (extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM)
                encoding = WaveFormatEncoding.Pcm;
        }

        // Couldn't positively identify the sub-format (either this wasn't
        // actually a WaveFormatExtensible instance despite reporting
        // Extensible, or its SubFormat GUID wasn't one of the two checked
        // above) - 32-bit float is what every device that reaches this path
        // used to be assumed to be, before sub-format detection existed, and
        // that assumption was correct often enough that guessing "unsupported"
        // instead regressed normal playback capture entirely.
        if (encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
            encoding = WaveFormatEncoding.IeeeFloat;

        var interleaved = encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => ReadFloat32(buffer, bytesRecorded),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => ReadPcm16(buffer, bytesRecorded),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => ReadPcm24(buffer, bytesRecorded),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => ReadPcm32(buffer, bytesRecorded),
            _ => null,
        };

        if (interleaved is null)
        {
            if (!_loggedUnsupportedFormat)
            {
                _loggedUnsupportedFormat = true;
                System.Diagnostics.Debug.WriteLine(
                    $"Unsupported capture format: {encoding} ({format.Encoding} wrapper), {format.BitsPerSample}-bit - no samples will be processed.");
            }

            return Array.Empty<float>();
        }

        if (format.Channels <= 1)
            return interleaved;

        var channels = format.Channels;
        var mono = new float[interleaved.Length / channels];

        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var ch = 0; ch < channels; ch++)
                sum += interleaved[i * channels + ch];

            mono[i] = sum / channels;
        }

        return mono;
    }

    private static float[] ReadFloat32(byte[] buffer, int bytesRecorded)
    {
        var floatBuffer = new float[bytesRecorded / 4];
        Buffer.BlockCopy(buffer, 0, floatBuffer, 0, bytesRecorded);
        return floatBuffer;
    }

    private static float[] ReadPcm16(byte[] buffer, int bytesRecorded)
    {
        var count = bytesRecorded / 2;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        return result;
    }

    private static float[] ReadPcm24(byte[] buffer, int bytesRecorded)
    {
        var count = bytesRecorded / 3;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 3;
            var sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000); // sign-extend
            result[i] = sample / 8388608f;
        }
        return result;
    }

    private static float[] ReadPcm32(byte[] buffer, int bytesRecorded)
    {
        var count = bytesRecorded / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
        return result;
    }

    public void Dispose() => Stop();
}

public sealed class AudioBufferEventArgs(float[] samples, int sampleRate) : EventArgs
{
    public float[] Samples { get; } = samples;

    public int SampleRate { get; } = sampleRate;
}
