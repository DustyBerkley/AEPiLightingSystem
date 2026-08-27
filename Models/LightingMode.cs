namespace AEPiLightingSystem.Models;

/// <summary>
/// How much of the rig is currently driven by the audio-reactive automation
/// vs. the manual MIDI-fader effects.
/// </summary>
public enum LightingMode
{
    /// <summary>Automated animations only - manual fader effects are ignored even if a fader is turned up.</summary>
    FullAuto,

    /// <summary>Automated animations and manual fader effects both run at once.</summary>
    SemiAuto,

    /// <summary>Automated animations are switched off entirely - only manual fader effects run.</summary>
    Manual,
}
