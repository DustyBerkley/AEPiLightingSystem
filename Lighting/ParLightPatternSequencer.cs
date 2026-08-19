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
/// </summary>
public sealed class ParLightPatternSequencer
{
    private const int BeatsPerPhrase = 8;
    private const double FlashSaturation = 0.95;
    private const double FlashLightness = 0.61;

    private static readonly ParLightPattern[] AllPatterns =
    [
        ParLightPattern.All,
        ParLightPattern.AlternateLeftRight,
        ParLightPattern.AlternateTopBottom,
        ParLightPattern.AlternateDiagonals,
    ];

    private readonly Random _random = new();
    private int _beatsInCurrentPhrase;
    private int _beatIndex;

    public ParLightPattern CurrentPattern { get; private set; } = ParLightPattern.All;

    /// <summary>
    /// The flash color for the current phrase. Picked fresh - random hue,
    /// fixed saturation/lightness - each time the pattern changes.
    /// </summary>
    public Color CurrentFlashColor { get; private set; } = Colors.White;

    /// <summary>
    /// Call once per detected beat (bass hit). A full 8-beat phrase (two
    /// 4/4 measures) plays out before switching, so the new pattern (and
    /// flash color) starts on beat 9 - the first beat of the next phrase -
    /// not on beat 8 itself.
    /// </summary>
    public void RegisterBeat()
    {
        _beatIndex++;
        _beatsInCurrentPhrase++;

        if (_beatsInCurrentPhrase <= BeatsPerPhrase)
            return;

        _beatsInCurrentPhrase = 1;
        CurrentPattern = PickNextPattern();
        CurrentFlashColor = HslColor.FromHsl(_random.NextDouble() * 360.0, FlashSaturation, FlashLightness);
    }

    private ParLightPattern PickNextPattern()
    {
        ParLightPattern next;
        do
        {
            next = AllPatterns[_random.Next(AllPatterns.Length)];
        } while (next == CurrentPattern);

        return next;
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
