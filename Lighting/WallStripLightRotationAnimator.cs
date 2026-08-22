using System;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Eases the wall striplight's displayed facing angle
/// (<see cref="WallStripLight.DisplayFacingAngleDegrees"/>) toward its
/// target (<see cref="WallStripLight.FacingAngleDegrees"/>) at a constant
/// angular speed rather than snapping, so the GUI's arrow reads as a
/// physical moving head rotating into position instead of teleporting.
///
/// <see cref="RotationSpeedDegreesPerSecond"/> is a placeholder - the real
/// Rockville Motionstrip's pan/tilt speed is itself DMX-adjustable (its
/// documentation confirms a speed channel but publishes no fixed
/// degrees/second figure), so there's no single correct value to match
/// here. This is sized for a 90 degree move to take roughly
/// 0.5-1 second, a reasonable rate for this class of DMX moving-head bar at
/// a moderate speed setting - retune once the physical unit's actual
/// configured speed is known (e.g. timed with a stopwatch).
/// </summary>
public sealed class WallStripLightRotationAnimator
{
    private const double RotationSpeedDegreesPerSecond = 130.0;
    private const double SnapThresholdDegrees = 0.05;

    private readonly WallStripLight _light;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    public WallStripLightRotationAnimator(WallStripLight light)
    {
        _light = light;
        _light.DisplayFacingAngleDegrees = light.FacingAngleDegrees;
        _light.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WallStripLight.FacingAngleDegrees))
                EnsureRunning();
        };
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        var current = _light.DisplayFacingAngleDegrees;
        var target = _light.FacingAngleDegrees;
        var delta = target - current;
        var maxStep = RotationSpeedDegreesPerSecond * dt;

        if (Math.Abs(delta) <= maxStep)
        {
            _light.DisplayFacingAngleDegrees = target;
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
            return;
        }

        _light.DisplayFacingAngleDegrees = current + Math.Sign(delta) * maxStep;
    }
}
