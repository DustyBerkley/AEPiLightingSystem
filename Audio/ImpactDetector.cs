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

    // Tracks ambient loudness over roughly the preceding few seconds (both
    // rise and fall use this same slow time constant, unlike the onset
    // detector's asymmetric average) so a real impact can be classified as
    // "extreme" - a sudden loud moment out of genuine quiet, like a drop -
    // versus one that lands on top of already-present sound.
    private const double BackgroundTimeConstantSeconds = 3.0;

    // -20 dBFS RMS: quiet enough to call the room "abnormally quiet", not
    // just a momentary dip between hits. Raised from -30 dBFS since that bar
    // was rarely being cleared, so nearly every impact fell back to the
    // partial (far left/right only) flash instead of the full 6-strip one.
    private const double QuietBackgroundRms = 0.1; // 10^(-20/20)
    private const double QuietBackgroundEnergyThreshold = QuietBackgroundRms * QuietBackgroundRms;

    private readonly int _sampleRate;
    private readonly AdaptiveOnsetDetector _onset = new(MinimumImpactRms * MinimumImpactRms);
    private double _recentBackgroundEnergy;

    public event EventHandler<ImpactEventArgs>? ImpactDetected;

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

        // Captured before folding this buffer in, so a loud buffer that
        // triggers the impact itself can't retroactively count as "not quiet".
        var wasBackgroundQuiet = _recentBackgroundEnergy < QuietBackgroundEnergyThreshold;

        var k = 1.0 - Math.Exp(-bufferSeconds / BackgroundTimeConstantSeconds);
        _recentBackgroundEnergy += k * (instantEnergy - _recentBackgroundEnergy);

        if (_onset.ProcessEnergy(instantEnergy, bufferSeconds))
            ImpactDetected?.Invoke(this, new ImpactEventArgs(wasBackgroundQuiet));
    }
}

/// <summary>
/// <see cref="ImpactDetector.IsExtreme"/> is true when the preceding few
/// seconds were abnormally quiet right up until this sudden loud moment -
/// the "drop" case - as opposed to a loud hit landing during already-active
/// sound.
/// </summary>
public sealed class ImpactEventArgs : EventArgs
{
    public bool IsExtreme { get; }

    public ImpactEventArgs(bool isExtreme)
    {
        IsExtreme = isExtreme;
    }
}
