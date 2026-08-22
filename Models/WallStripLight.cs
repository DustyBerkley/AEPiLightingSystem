using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AEPiLightingSystem.Models;

/// <summary>
/// A Rockville Motionstrip 80W - a motorized moving-head fixture with 8
/// lights in a single line that can rotate up/down. Each of the 8 lights is
/// a plain <see cref="ParLight"/> (same model the wall/floor pars use;
/// only the GUI renders it differently), so the existing
/// <see cref="Lighting.ParLightFlashAnimator"/> can drive it exactly like a
/// par light group. <see cref="FacingAngleDegrees"/> is the target
/// direction the head should be aimed; <see cref="DisplayFacingAngleDegrees"/>
/// is where the GUI's arrow is actually drawn, eased toward that target by
/// <see cref="Lighting.WallStripLightRotationAnimator"/> rather than
/// snapping, so it reads as a physical head rotating.
/// </summary>
public class WallStripLight : INotifyPropertyChanged
{
    public const int LightCount = 8;

    /// <summary>
    /// "Forward" is angled 17 degrees down from perfectly horizontal, not
    /// dead level - <see cref="DownAngleDegrees"/> = facing further down
    /// still, from this same reference.
    /// </summary>
    public const double ForwardAngleDegrees = 17.0;

    /// <summary>60 degrees from horizontal, not a full 90 - a straight-down aim overshot the intended coverage area.</summary>
    public const double DownAngleDegrees = 60.0;

    public ReadOnlyCollection<ParLight> Lights { get; }

    private double _facingAngleDegrees = ForwardAngleDegrees;
    private double _displayFacingAngleDegrees = ForwardAngleDegrees;

    /// <summary>The direction the head should be aimed, set from whichever bass-flash pattern is active.</summary>
    public double FacingAngleDegrees
    {
        get => _facingAngleDegrees;
        set
        {
            if (_facingAngleDegrees == value)
                return;

            _facingAngleDegrees = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Where the arrow is actually drawn - eases toward <see cref="FacingAngleDegrees"/> rather than snapping to it.</summary>
    public double DisplayFacingAngleDegrees
    {
        get => _displayFacingAngleDegrees;
        set
        {
            if (_displayFacingAngleDegrees == value)
                return;

            _displayFacingAngleDegrees = value;
            OnPropertyChanged();
        }
    }

    public WallStripLight()
    {
        var lights = new ParLight[LightCount];
        for (var i = 0; i < LightCount; i++)
            lights[i] = new ParLight(i + 1, $"Wall Striplight {i + 1}");

        Lights = new ReadOnlyCollection<ParLight>(lights);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
