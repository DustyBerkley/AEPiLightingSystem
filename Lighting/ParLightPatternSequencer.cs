using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Rotates which par lights participate in each bass-hit flash - and what
/// color they flash - every 8 beats - a standard musical phrase length - so
/// a steady, repeating bass pattern doesn't look identical flash after
/// flash. Because the phrase length is fixed at 8 beats, changes always
/// land on standard musical boundaries (8, 16, 24, 32, ...).
///
/// The half-rig patterns (left/right, top/bottom, diagonals) alternate their
/// two halves every beat, producing a back-and-forth chase rather than
/// lighting the same side for the whole phrase.
///
/// If a beat doesn't arrive for 4+ measures at the recently observed tempo,
/// the next hit is treated as a fresh start: back to the "All" pattern with
/// a newly rolled color, rather than resuming wherever the phrase counter
/// left off.
/// </summary>
public sealed class ParLightPatternSequencer
{
    private const int BeatsPerPhrase = 8;
    private const int BeatsPerMeasure = 4;
    private const int SilenceResetMeasures = 4;
    private const int SilenceResetBeats = BeatsPerMeasure * SilenceResetMeasures;
    private const double FlashSaturation = 0.95;
    private const double FlashLightness = 0.61;
    private const double InitialBeatIntervalSeconds = 0.5; // ~120 BPM assumption until real beats arrive
    private const double TempoSmoothingFactor = 0.25;

    // "All" gets 35% of the roll; the other three patterns split the
    // remaining 65% evenly.
    private const double AllPatternWeight = 0.35;

    private static readonly (ParLightPattern Pattern, double Weight)[] WeightedPatterns =
    [
        (ParLightPattern.All, AllPatternWeight),
        (ParLightPattern.AlternateLeftRight, (1 - AllPatternWeight) / 3),
        (ParLightPattern.AlternateTopBottom, (1 - AllPatternWeight) / 3),
        (ParLightPattern.AlternateDiagonals, (1 - AllPatternWeight) / 3),
    ];

    private readonly Random _random = new();
    private int _beatsInCurrentPhrase;
    private int _beatIndex;
    private double _averageBeatIntervalSeconds = InitialBeatIntervalSeconds;
    private DateTime _lastBeatTime = DateTime.MinValue;

    public ParLightPattern CurrentPattern { get; private set; } = ParLightPattern.All;

    /// <summary>
    /// The flash color for the current phrase. Picked fresh - random hue,
    /// fixed saturation/lightness - each time the pattern changes.
    /// </summary>
    public Color CurrentFlashColor { get; private set; }

    public ParLightPatternSequencer()
    {
        CurrentFlashColor = RandomFlashColor(_random);
    }

    /// <summary>
    /// Call once per detected beat (bass hit). A full 8-beat phrase (two
    /// 4/4 measures) plays out before switching, so the new pattern (and
    /// flash color) starts on beat 9 - the first beat of the next phrase -
    /// not on beat 8 itself.
    /// </summary>
    public void RegisterBeat()
    {
        var now = DateTime.UtcNow;

        if (_lastBeatTime != DateTime.MinValue)
        {
            var gapSeconds = (now - _lastBeatTime).TotalSeconds;

            if (gapSeconds > _averageBeatIntervalSeconds * SilenceResetBeats)
            {
                // Quiet for 4+ measures at the recent tempo - start the next
                // hit as a fresh phrase instead of resuming where we left off.
                _beatsInCurrentPhrase = 0;
                CurrentPattern = ParLightPattern.All;
                CurrentFlashColor = RandomFlashColor(_random);
            }
            else
            {
                _averageBeatIntervalSeconds += TempoSmoothingFactor * (gapSeconds - _averageBeatIntervalSeconds);
            }
        }

        _lastBeatTime = now;

        _beatIndex++;
        _beatsInCurrentPhrase++;

        if (_beatsInCurrentPhrase <= BeatsPerPhrase)
            return;

        _beatsInCurrentPhrase = 1;
        CurrentPattern = PickNextPattern();
        CurrentFlashColor = RandomFlashColor(_random);
    }

    private static Color RandomFlashColor(Random random) =>
        HslColor.FromHsl(random.NextDouble() * 360.0, FlashSaturation, FlashLightness);

    private ParLightPattern PickNextPattern()
    {
        ParLightPattern next;
        do
        {
            next = PickWeightedPattern();
        } while (next == CurrentPattern);

        return next;
    }

    private ParLightPattern PickWeightedPattern()
    {
        var roll = _random.NextDouble();
        var cumulative = 0.0;

        foreach (var (pattern, weight) in WeightedPatterns)
        {
            cumulative += weight;
            if (roll < cumulative)
                return pattern;
        }

        // Floating-point rounding safety net; practically unreachable.
        return WeightedPatterns[^1].Pattern;
    }

    /// <summary>
    /// Lights for the current pattern on the beat just registered. For the
    /// alternating patterns this flips between halves each call.
    /// </summary>
    public IReadOnlyList<ParLight> GetActiveLights(LightingRig rig)
    {
        var isEvenBeat = _beatIndex % 2 == 0;

        return CurrentPattern switch
        {
            ParLightPattern.AlternateLeftRight => isEvenBeat
                ? [rig.TopLeftPar, rig.BottomLeftPar]
                : [rig.TopRightPar, rig.BottomRightPar],
            ParLightPattern.AlternateTopBottom => isEvenBeat
                ? [rig.TopLeftPar, rig.TopRightPar]
                : [rig.BottomLeftPar, rig.BottomRightPar],
            ParLightPattern.AlternateDiagonals => isEvenBeat
                ? [rig.TopLeftPar, rig.BottomRightPar]
                : [rig.TopRightPar, rig.BottomLeftPar],
            _ => rig.ParLights,
        };
    }
}
