namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Which par lights participate in a bass-hit flash. The non-"All" patterns
/// alternate between their two halves on every beat (e.g. left, then right,
/// then left...) rather than lighting the same half for the whole phrase.
/// </summary>
public enum ParLightPattern
{
    All,
    AlternateLeftRight,
    AlternateTopBottom,
    AlternateDiagonals,
}
