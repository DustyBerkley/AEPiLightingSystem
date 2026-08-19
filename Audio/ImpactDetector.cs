using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Detects sudden, dramatically loud broadband transients - crash cymbals,
/// big isolated hits, drops - by watching full-spectrum (unfiltered) energy
/// rather than just the bass band, so it also catches loud events that have
/// little low-frequency content.
/// </summary>
public sealed class ImpactDetector
{
    // Deliberately stricter than the bass-hit gate: this is reserved for
    // genuinely dramatic, sudden loud moments, not every kick drum.
    // -12 dBFS RMS across the full signal.
    private const double MinimumImpactRms = 0.251; // 10^(-12/20)

    private readonly int _sampleRate;
    private readonly AdaptiveOnsetDetector _onset = new(MinimumImpactRms * MinimumImpactRms);

    public event EventHandler? ImpactDetected;

    public ImpactDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Process(float[] samples)
    {
        if (samples.Length == 0)
            return;

        double sumSquares = 0;
        foreach (var sample in samples)
            sumSquares += sample * sample;

        var instantEnergy = sumSquares / samples.Length;
        var bufferSeconds = (double)samples.Length / _sampleRate;

        if (_onset.ProcessEnergy(instantEnergy, bufferSeconds))
            ImpactDetected?.Invoke(this, EventArgs.Empty);
    }
}
