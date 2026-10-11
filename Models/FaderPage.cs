namespace AEPiLightingSystem.Models;

/// <summary>Which bank of 8 fader effects the physical MIDI board is currently controlling.</summary>
public enum FaderPage
{
    Bass,
    Laser,
    LightBar,
    Combo,

    /// <summary>
    /// Global fixture-wide controls (see MainWindow.UpdateGlobalBrightnessFaderEffect)
    /// - not a real physical page selectable from the on-screen GUI, only
    /// from the Launchpad's row-1 pad 4.
    /// </summary>
    Global,
}
