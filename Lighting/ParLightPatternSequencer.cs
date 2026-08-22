using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Rotates which par lights participate in each bass-hit flash - and what
/// color they flash - every 16 beats - four standard 4/4 measures - so a
/// steady, repeating bass pattern doesn't look identical flash after flash.
/// Because the phrase length is fixed at 16 beats, changes always land on
/// standard musical boundaries (16, 32, 48, 64, ...).
///
/// The half-rig patterns (top/bottom, diagonals) alternate their two halves
/// every beat, producing a back-and-forth chase rather than lighting the
/// same side for the whole phrase.
///
/// If a beat doesn't arrive for 4+ measures at the recently observed tempo,
/// the next hit is treated as a fresh start: back to the "All" pattern with
/// a newly rolled color, rather than resuming wherever the phrase counter
/// left off.
/// </summary>
public sealed class ParLightPatternSequencer
{
    private const int BeatsPerPhrase = 16;
    private const int BeatsPerMeasure = 4;
    private const int SilenceResetMeasures = 4;
    private const int SilenceResetBeats = BeatsPerMeasure * SilenceResetMeasures;
    private const double FlashSaturation = 0.95;
    private const double FlashLightness = 0.61;
    private const double InitialBeatIntervalSeconds = 0.5; // ~120 BPM assumption until real beats arrive
    private const double TempoSmoothingFactor = 0.25;

    // "All" gets 35% of the roll; the other two patterns split the
    // remaining 65% evenly.
    private const double AllPatternWeight = 0.35;

    private static readonly (ParLightPattern Pattern, double Weight)[] WeightedPatterns =
    [
        (ParLightPattern.All, AllPatternWeight),
        (ParLightPattern.AlternateTopBottom, (1 - AllPatternWeight) / 2),
        (ParLightPattern.AlternateDiagonals, (1 - AllPatternWeight) / 2),
    ];

    private readonly Random _random = new();
    private int _beatsInCurrentPhrase;
    private int _beatIndex;
    private double _averageBeatIntervalSeconds = InitialBeatIntervalSeconds;
    private DateTime _lastBeatTime = DateTime.MinValue;

    public ParLightPattern CurrentPattern { get; private set; } = ParLightPattern.All;

    /// <summary>
    /// The pattern that will take effect on the next beat that starts a new
    /// phrase, decided one beat ahead of when it's actually applied (see
    /// <see cref="RegisterBeat"/>) - non-null only right after the last beat
    /// of the current phrase, so callers with something slow to react to
    /// (the wall striplight's rotation) can start reacting the moment that
    /// beat is done instead of waiting for the new phrase's first beat to
    /// actually arrive.
    /// </summary>
    public ParLightPattern? UpcomingPattern { get; private set; }

    /// <summary>
    /// Whether the most recent <see cref="GetActiveLights"/> call picked the
    /// top pair - only meaningful when <see cref="CurrentPattern"/> is
    /// <see cref="ParLightPattern.AlternateTopBottom"/>, for callers (the
    /// wall striplight) that only care about that specific half.
    /// </summary>
    public bool LastActiveWasTop { get; private set; }

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
    /// Call once per detected beat (bass hit). A full 16-beat phrase (four
    /// 4/4 measures) plays out before switching, so the new pattern (and
    /// flash color) starts on beat 17 - the first beat of the next phrase -
    /// not on beat 16 itself (though <see cref="UpcomingPattern"/> is
    /// already known one beat earlier, on beat 16).
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
                // There's no beat to have anticipated this on, so no lookahead
                // applies here - it's a direct, immediate reset.
                _beatsInCurrentPhrase = 0;
                CurrentPattern = ParLightPattern.All;
                CurrentFlashColor = RandomFlashColor(_random);
                UpcomingPattern = null;
            }
            else
            {
                _averageBeatIntervalSeconds += TempoSmoothingFactor * (gapSeconds - _averageBeatIntervalSeconds);
            }
        }

        _lastBeatTime = now;

        _beatIndex++;
        _beatsInCurrentPhrase++;

        // One beat before the phrase actually rolls over, decide (but don't
        // yet apply) the next pattern - this is what lets UpcomingPattern
        // give callers a full beat's lead time on a change instead of only
        // finding out once the new phrase's first beat itself arrives.
        if (_beatsInCurrentPhrase == BeatsPerPhrase)
            UpcomingPattern = PickNextPattern();

        if (_beatsInCurrentPhrase <= BeatsPerPhrase)
            return;

        _beatsInCurrentPhrase = 1;
        CurrentPattern = UpcomingPattern ?? PickNextPattern();
        UpcomingPattern = null;
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
        LastActiveWasTop = CurrentPattern == ParLightPattern.AlternateTopBottom && isEvenBeat;

        return CurrentPattern switch
        {
            ParLightPattern.AlternateTopBottom => isEvenBeat
                ? [rig.TopLeftPar, rig.TopRightPar]
                : [rig.BottomLeftPar, rig.BottomRightPar],
            ParLightPattern.AlternateDiagonals => isEvenBeat
                ? [rig.TopLeftPar, rig.BottomRightPar]
                : [rig.TopRightPar, rig.BottomLeftPar],
            _ => rig.WallParLights,
        };
    }
}
