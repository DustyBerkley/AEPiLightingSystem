using System;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Drives the 2nd manual laser fader effect: independently wanders
/// Rotation, Horizontal Movement, and Vertical Zoom within their own
/// confirmed real-hardware ranges (see <see cref="Dmx.LaserDmxBinder"/>),
/// each via its own <see cref="SineNoise"/> generator so none of the three
/// move in lockstep. Every other DMX channel keeps whatever the normal
/// wave/lava-lamp effect already sets - see LaserFixture.IsSecondEffectActive.
///
/// Color follows the same rule as the wave effect: a fresh hue is rolled
/// only on an actual off-to-on activation, then held fixed for as long as
/// this effect keeps playing.
/// </summary>
public sealed class LaserSecondEffectAnimator
{
    // How far the on-screen preview's TiltRadians swings for
    // SecondEffectVerticalZoomFraction = 0-1 - same magnitude LaserFanAnimator
    // uses for its own wobble, just driven by this effect's own channel
    // instead.
    private const double MaxTiltRadians = 0.10;

    private const double PreviewSaturation = 0.95;
    private const double PreviewLightness = 0.5;

    private readonly LaserFixture _laser;
    private readonly Random _random;

    private readonly SineNoise _rotationNoise;
    private readonly SineNoise _horizontalNoise;
    private readonly SineNoise _verticalZoomNoise;

    /// <summary>
    /// Multiplies how fast the three channels wander - intended to be
    /// driven by the fader's own beat interval (lower interval = faster),
    /// the same repurposing the 1st manual laser effect uses. Defaults to
    /// 1.0 so the animator behaves reasonably even if nothing ever sets it.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    private const double EvolveSpeed = 0.18;

    private double _time;
    private bool _isRunning;
    private bool _wasActive;

    public LaserSecondEffectAnimator(LaserFixture laser)
    {
        _laser = laser;

        _random = new Random();
        _rotationNoise = new SineNoise(_random.Next());
        _horizontalNoise = new SineNoise(_random.Next());
        _verticalZoomNoise = new SineNoise(_random.Next());
    }

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing the laser off - SetActive is ignored outright
    /// while set, regardless of who's calling it.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    /// <summary>Force this effect off (true) or let it respond to SetActive calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (suppressed)
            SetActiveCore(false);
    }

    /// <summary>Call whenever the fader driving this effect crosses to/from 0%.</summary>
    public void SetActive(bool active)
    {
        if (IsSuppressed)
            return;

        SetActiveCore(active);
    }

    private void SetActiveCore(bool active)
    {
        // The caller (MainWindow's per-tick UpdateLaserSecondFaderEffect)
        // calls this with active=false on every single tick whenever the
        // driving fader just sits at 0%, not only on the actual
        // active-to-inactive transition - already being inactive means
        // there's nothing to turn off, so bail out before force-blacking
        // the preview color again, which would otherwise stomp on the wave
        // effect (or anything else) using the laser right now.
        if (!active && !_wasActive)
            return;

        if (active && !_wasActive)
            _laser.SecondEffectHueDegrees = _random.NextDouble() * 360.0;
        _wasActive = active;

        _laser.IsSecondEffectActive = active;

        if (active)
        {
            EnsureRunning();
        }
        else
        {
            // OnRendering unhooks itself once it sees this false, but that
            // might not happen for a while (or ever, if nothing else is
            // driving the laser) - force one final write now so the DMX
            // universe reflects "off" immediately rather than freezing on
            // whatever this effect last wrote. Same for the on-screen
            // preview's color, if the wave effect isn't the one supplying
            // it - CenterFraction/TiltRadians are left as-is (harmless once
            // Total Switch is off, and the wave effect may still want them
            // showing its own values if it's the one still active).
            _isRunning = false;

            if (!_laser.IsWaveEffectActive)
            {
                foreach (var group in _laser.Groups)
                    group.Color = Colors.Black;
            }

            _laser.RaiseGroupsChanged();
        }
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private DateTime _lastFrameTime;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            return;
        }

        var now = DateTime.UtcNow;
        var dt = _lastFrameTime == default ? 0.0 : (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        _time += dt * SpeedFactor;

        _laser.SecondEffectRotationFraction = _rotationNoise.Sample(0.0, _time * EvolveSpeed);
        _laser.SecondEffectHorizontalFraction = _horizontalNoise.Sample(100.0, _time * EvolveSpeed);
        _laser.SecondEffectVerticalZoomFraction = _verticalZoomNoise.Sample(200.0, _time * EvolveSpeed);

        // Also drive the on-screen preview (LaserPanelView draws entirely
        // from LaserFixture.Groups) so this effect visibly differs from the
        // wave effect rather than leaving the preview frozen wherever the
        // wave effect last left it - CenterFraction/TiltRadians are the 2 of
        // this effect's 3 channels that have a natural visual analog
        // (Horizontal Movement/Vertical Zoom respectively); color only
        // takes over here when the wave effect isn't actually contributing
        // one, matching LaserDmxBinder's own real-DMX color precedence.
        foreach (var group in _laser.Groups)
        {
            group.CenterFraction = _laser.SecondEffectHorizontalFraction * 2.0 - 1.0;
            group.TiltRadians = (_laser.SecondEffectVerticalZoomFraction * 2.0 - 1.0) * MaxTiltRadians;

            if (!_laser.IsWaveEffectActive)
            {
                group.HueDegrees = _laser.SecondEffectHueDegrees;
                group.Color = HslColor.FromHsl(_laser.SecondEffectHueDegrees, PreviewSaturation, PreviewLightness);
            }
        }

        _laser.RaiseGroupsChanged();
    }
}
