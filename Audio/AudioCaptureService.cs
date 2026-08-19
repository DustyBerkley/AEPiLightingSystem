using System;
using NAudio.Wave;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Captures whatever audio is currently playing out of the default output
/// device (WASAPI loopback) and raises mono sample buffers for analysis.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;

    public event EventHandler<AudioBufferEventArgs>? BufferAvailable;

    public bool IsCapturing => _capture is not null;

    public void Start()
    {
        if (_capture is not null)
            return;

        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null)
            return;

        var samples = ConvertToMonoFloat(e.Buffer, e.BytesRecorded, _capture.WaveFormat);

        if (samples.Length > 0)
            BufferAvailable?.Invoke(this, new AudioBufferEventArgs(samples, _capture.WaveFormat.SampleRate));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _capture?.Dispose();
        _capture = null;
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
