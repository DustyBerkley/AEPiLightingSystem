using System;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Drives the laser's <see cref="LaserFixture.Groups"/> - exactly
/// <see cref="LaserFixture.GroupCount"/> of them (one per lens), each a
/// single horizontal line rather than the individually-addressable beams an
/// earlier design modeled (the real hardware can only draw one shape per
/// lens, so that never had a real equivalent). Only the first group is
/// actually simulated - wandering position, width, and tilt over time via
/// <see cref="SineNoise"/> - the second mirrors it (position negated, tilt
/// sign-flipped, same width/color), the same left-simulated/right-mirrored
/// trick used elsewhere in the rig.
///
/// Color is picked once per activation, not continuously wandered: a fresh
/// hue is rolled each time <see cref="SetActive"/> turns the effect on from
/// off, then held fixed for as long as it keeps playing - only going idle
/// (intensity fading to 0) and coming back rolls a new one.
///
/// Active state fades in/out smoothly, driven by the same
/// arpeggio/melodic-passage detection that drives the ceiling light bars.
///
/// A single isolated, sustained chord (as opposed to a busy arpeggio/vocal
/// line - see <see cref="Audio.ChordSustainDetector"/>) gets different
/// treatment: <see cref="TriggerChordHit"/> replaces the wandering line
/// with a narrow, static one at a freshly-picked random position/color,
/// held until the next chord hit or until chord mode ends via
/// <see cref="SetChordMode"/>.
///
/// Whatever's computed above is a per-frame *target*, not what's actually
/// shown - <see cref="OnRendering"/> eases the displayed position/width/
/// tilt/color toward that target rather than snapping, so switching
/// between the wandering line and a chord hit (or one chord hit and the
/// next) crossfades smoothly instead of cutting.
/// </summary>
public sealed class LaserFanAnimator
{
    private const double IntensityFadeSeconds = 1.5;

    // How wide the wandering line's width ranges, as a fraction of the
    // fixture's full usable span. A previous, larger jump (to 0.35-0.75)
    // made real beams collapse toward a dot at the high end, so this is a
    // smaller, more cautious step up from the last confirmed-good 0.15-0.45
    // rather than repeating that same overshoot.
    private const double MinWidthFraction = 0.22;
    private const double MaxWidthFraction = 0.58;

    // Chord mode reads as "a single beam" - narrower than the normal
    // wandering line, but widened proportionally alongside it.
    private const double ChordWidthFraction = 0.09;

    // How far off-center the line's wander can reach, in the same [-1, 1]
    // domain as CenterFraction.
    private const double CenterWanderRange = 0.6;

    // How fast width/tilt wander - fast enough to read as active movement
    // rather than a slow ambient breathe (the dynamic SpeedFactor below
    // scales this further with the music's energy).
    private const double EvolveSpeed = 0.65;

    // Horizontal position (CenterFraction, which drives the real DMX
    // Horizontal Movement channel) gets its own slower pace, separate from
    // width/tilt, per real-hardware feedback that it was moving too fast.
    private const double CenterEvolveSpeed = 0.3;

    private const double FlashSaturation = 0.95;
    private const double LightnessCap = 0.5;

    // A group's vertical tilt, in radians either side of dead ahead - kept
    // fairly narrow so the line stays close to the horizontal plane in
    // front of the fixture rather than swinging widely up/down.
    private const double MaxTiltRadians = 0.10;

    // How long the displayed position/width/tilt/color takes to ease
    // toward whatever's newly computed each frame - what makes switching
    // between the wandering line and a chord hit (or one chord hit and the
    // next) read as a crossfade instead of a cut.
    private const double DisplayFadeSeconds = 0.4;

    /// <summary>
    /// Multiplies the line's lightness - intended to follow the music's
    /// current volume (see EffectBrightnessTracker) so the effect breathes
    /// with the track rather than sitting at one fixed brightness. Defaults
    /// to 1.0 (no change) so the animator behaves exactly as before if
    /// nothing ever sets it.
    /// </summary>
    public double BrightnessFactor { get; set; } = 1.0;

    /// <summary>
    /// Multiplies how fast the animator's internal clock advances -
    /// intended to follow the music's current energy (see
    /// EffectSpeedTracker) so the line wanders faster during energetic
    /// passages and slower during calm ones. Defaults to 1.0 (no change) so
    /// the animator behaves exactly as before if nothing ever sets it.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    private readonly LaserFixture _laser;
    private readonly Random _random;

    // All three drive on-screen movement (position, width, and tilt), so
    // all use SineNoise rather than SmoothNoise - see SineNoise's summary
    // for why value noise looked jerky here once sampled fast along the
    // time axis.
    private readonly SineNoise _centerNoise;
    private readonly SineNoise _widthNoise;
    private readonly SineNoise _tiltNoise;

    // What's actually shown, eased toward the per-frame target computed in
    // OnRendering rather than snapping to it - see DisplayFadeSeconds.
    private double _displayCenter;
    private double _displayWidth;
    private double _displayTilt;
    private Color _displayColor = Colors.Black;

    private double _time;
    private double _targetIntensity;
    private double _currentIntensity;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    private bool _chordMode;
    private double _chordCenter;
    private double _chordHue;

    private double _waveHue;
    private bool _wasActive;

    public LaserFanAnimator(LaserFixture laser)
    {
        _laser = laser;

        _random = new Random();
        _centerNoise = new SineNoise(_random.Next());
        _widthNoise = new SineNoise(_random.Next());
        _tiltNoise = new SineNoise(_random.Next());
        _waveHue = _random.NextDouble() * 360.0;
    }

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing the laser off - SetActive/SetChordMode/
    /// TriggerChordHit are all ignored outright while set, regardless of
    /// who's calling them (manual fader effect or automation).
    /// </summary>
    public bool IsSuppressed { get; private set; }

    /// <summary>Call whenever the thing this follows (currently: arpeggio/melodic-passage detection) changes state.</summary>
    public void SetActive(bool active)
    {
        if (IsSuppressed)
            return;

        // Only roll a new color on an actual off-to-on transition, not on
        // every call (SetActive(true) can be called repeatedly while
        // already active) - see class summary.
        if (active && !_wasActive)
            _waveHue = _random.NextDouble() * 360.0;
        _wasActive = active;

        // Set immediately, not once the fade below finishes - see
        // LaserFixture.IsWaveEffectActive.
        _laser.IsWaveEffectActive = active;

        _targetIntensity = active ? 1.0 : 0.0;
        EnsureRunning();
    }

    /// <summary>Force the laser off (true) or let it respond to SetActive/chord calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        _wasActive = false;
        _chordMode = false;
        _targetIntensity = 0.0;
        _currentIntensity = 0.0;
        _displayColor = Colors.Black;
        _laser.IsWaveEffectActive = false;

        foreach (var group in _laser.Groups)
            group.Color = Colors.Black;
        _laser.RaiseGroupsChanged();

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    /// <summary>
    /// Switches between the normal wandering line and single-"beam" chord
    /// mode. Turning chord mode off resumes the normal line on the next
    /// frame - it doesn't fade or transition specially, since the caller
    /// (see <see cref="Audio.ChordSustainDetector"/>) only flips this when
    /// the music itself has already moved from one held chord back to a
    /// busy arpeggio/vocal line.
    /// </summary>
    public void SetChordMode(bool active)
    {
        if (IsSuppressed)
            return;

        _chordMode = active;
    }

    /// <summary>
    /// Call on every new chord attack while in chord mode: picks a fresh
    /// random position and color, held static until the next hit.
    /// </summary>
    public void TriggerChordHit()
    {
        if (IsSuppressed)
            return;

        _chordMode = true;
        _chordCenter = (_random.NextDouble() * 2.0 - 1.0) * CenterWanderRange;
        _chordHue = _random.NextDouble() * 360.0;
        _laser.ChordHitIndex++;
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

        _time += dt * SpeedFactor;

        var step = dt / IntensityFadeSeconds;
        if (_currentIntensity < _targetIntensity)
            _currentIntensity = Math.Min(_targetIntensity, _currentIntensity + step);
        else if (_currentIntensity > _targetIntensity)
            _currentIntensity = Math.Max(_targetIntensity, _currentIntensity - step);

        double center, width, tilt, hue;

        if (_chordMode)
        {
            center = _chordCenter;
            width = ChordWidthFraction;
            tilt = 0.0;
            hue = _chordHue;
        }
        else
        {
            var centerNoise01 = _centerNoise.Sample(0.0, _time * CenterEvolveSpeed);
            center = (centerNoise01 * 2.0 - 1.0) * CenterWanderRange;

            var widthNoise01 = _widthNoise.Sample(500.0, _time * EvolveSpeed);
            width = MinWidthFraction + (MaxWidthFraction - MinWidthFraction) * widthNoise01;

            var tiltNoise01 = _tiltNoise.Sample(0.0, _time * EvolveSpeed);
            tilt = (tiltNoise01 * 2.0 - 1.0) * MaxTiltRadians;

            hue = _waveHue;
        }

        var lightness = LightnessCap * _currentIntensity * BrightnessFactor;
        var targetColor = lightness <= 0.001 ? Colors.Black : HslColor.FromHsl(hue, FlashSaturation, lightness);

        var fadeAlpha = 1.0 - Math.Exp(-dt / DisplayFadeSeconds);
        _displayCenter += fadeAlpha * (center - _displayCenter);
        _displayWidth += fadeAlpha * (width - _displayWidth);
        _displayTilt += fadeAlpha * (tilt - _displayTilt);
        _displayColor = Color.FromRgb(
            (byte)(_displayColor.R + (targetColor.R - _displayColor.R) * fadeAlpha),
            (byte)(_displayColor.G + (targetColor.G - _displayColor.G) * fadeAlpha),
            (byte)(_displayColor.B + (targetColor.B - _displayColor.B) * fadeAlpha));

        _laser.IsChordMode = _chordMode;

        var groups = _laser.Groups;
        groups[0].CenterFraction = _displayCenter;
        groups[0].WidthFraction = _displayWidth;
        groups[0].TiltRadians = _displayTilt;
        groups[0].Color = _displayColor;
        groups[0].HueDegrees = hue;

        // Mirrored: tilt is negated, not copied straight across, for the
        // same reason as the mirrored beams this replaced - the right
        // lens's line should pan up/down together with the left one, not
        // in opposition.
        groups[1].CenterFraction = -_displayCenter;
        groups[1].WidthFraction = _displayWidth;
        groups[1].TiltRadians = -_displayTilt;
        groups[1].Color = _displayColor;
        groups[1].HueDegrees = hue;

        _laser.RaiseGroupsChanged();

        // Stopping as soon as _currentIntensity bottoms out isn't enough on
        // its own - the *displayed* color lags behind via the crossfade
        // above and can still be a faint non-black residue at that exact
        // moment, which would otherwise freeze in place as a leftover line
        // the instant rendering stops. Waiting for the display to have
        // actually reached black is what avoids that.
        var displayIsBlack = _displayColor.R == 0 && _displayColor.G == 0 && _displayColor.B == 0;
        if (_currentIntensity <= 0.0005 && _targetIntensity <= 0.0005 && displayIsBlack)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }
}
