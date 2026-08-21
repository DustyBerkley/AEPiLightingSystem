using System;

namespace AEPiLightingSystem.Audio;

/// <summary>
/// Turns the mid-arpeggio and bassline loudness into a single smoothed
/// brightness multiplier, shared by every effect that should breathe with
/// the music's volume (currently the LED strips' lava lamp and the laser's
/// wave effect). Mid/high content drives most of it; bass contributes only
/// a little. Deliberately subtle: output stays within
/// [<see cref="MinBrightness"/>, <see cref="MaxBrightness"/>] so the effects
/// are always clearly visible even during a quiet passage, just dimmer than
/// when the music is loud.
/// </summary>
public sealed class EffectBrightnessTracker
{
    // Reference RMS levels at which each band is considered "loud enough"
    // to contribute its full share - past this, more volume doesn't add
    // more brightness. Tuned against each detector's own "clearly present"
    // threshold (MelodicPassageDetector.MinimumMidHighRms /
    // BassOnsetDetector's MinimumBassRms), not just guessed independently,
    // so a track that's merely "audible" in a band doesn't already register
    // as maximally loud in that band.
    private const double MidHighReferenceRms = 0.12;
    private const double BassReferenceRms = 0.25;

    // Mid/high is the primary driver; bass only nudges it a little, per the
    // "controlled mostly by the mid arpeggios, a little by the bassline" ask.
    private const double MidHighWeight = 0.8;
    private const double BassWeight = 0.2;

    // Kept a narrow range on purpose - this should read as a gentle
    // breathing with the music, not a dramatic strobe between dark and
    // bright.
    private const double MinBrightness = 0.7;
    private const double MaxBrightness = 1.0;

    // Slower than either input level's own smoothing, so the combined
    // brightness itself doesn't jitter even if one band's level does.
    private const double SmoothingSeconds = 0.5;

    /// <summary>The current smoothed brightness multiplier.</summary>
    public double Brightness { get; private set; } = MinBrightness;

    public void Process(double midHighLevel, double bassLevel, double bufferSeconds)
    {
        var midHighFraction = Math.Clamp(midHighLevel / MidHighReferenceRms, 0.0, 1.0);
        var bassFraction = Math.Clamp(bassLevel / BassReferenceRms, 0.0, 1.0);

        var combined01 = Math.Clamp(midHighFraction * MidHighWeight + bassFraction * BassWeight, 0.0, 1.0);
        var target = MinBrightness + (MaxBrightness - MinBrightness) * combined01;

        var smoothing = 1.0 - Math.Exp(-bufferSeconds / SmoothingSeconds);
        Brightness += smoothing * (target - Brightness);
    }
}
