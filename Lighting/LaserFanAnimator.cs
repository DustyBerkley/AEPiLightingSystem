using System;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Paints the laser's on-screen fan with the same lava-lamp character as
/// <see cref="LedStripLavaLampAnimator"/>, but with a different underlying
/// model: that animator (and this one, before) partitions its domain into
/// boundary-defined segments, which structurally guarantees a minimum gap
/// between neighboring blobs and rules out overlap entirely - two blobs can
/// never get closer than the gap because they share the boundary that
/// separates them. That's wrong for this fan: gaps should be able to fully
/// collapse, and groups should occasionally overlap into one bigger lit
/// patch. So instead there are a small fixed number of independent
/// "groups" (see <see cref="GroupCount"/>), each with its own center
/// position and radius that wander freely and independently (each around
/// its own roughly-evenly-spaced home slot) via <see cref="SineNoise"/> -
/// nothing ties one group's position or size to its neighbors', so two
/// groups can freely drift apart (gap opens), touch (gap collapses to
/// zero), or pass through each other (overlap) depending on where their
/// independent wandering happens to put them. Where two groups' regions
/// overlap, the brighter of the two wins at each beam (see
/// <see cref="OnRendering"/>) rather than adding, so an overlap reads as
/// one continuous bright patch instead of a blown-out seam.
///
/// Each group also gets its own vertical tilt, shared by every beam in that
/// group (so a group reads as one height), wandering on the same
/// independent-per-group basis as its position/radius. Tilting only ever
/// changes a beam's angle, never its length - light doesn't fall short at
/// some distance, so every beam always reaches exactly as far as it always
/// does; see <see cref="Controls.LaserPanelView"/> for how that tilt is
/// applied as a rotation around the projector point rather than a change in
/// reach.
///
/// Active state fades in/out smoothly, driven by the same
/// arpeggio/melodic-passage detection that drives the LED strips. Only the
/// left half of the fan is actually simulated each frame; the right half
/// mirrors it, the same trick <see cref="LedStripLavaLampAnimator"/> uses
/// for the rig's strips. DMX output isn't wired up yet; this only drives
/// the visualization.
///
/// A single isolated, sustained chord (as opposed to a busy arpeggio/vocal
/// line - see <see cref="Audio.ChordSustainDetector"/>) gets a different
/// treatment entirely: <see cref="TriggerChordHit"/> replaces the whole
/// wandering-group field with exactly one beam at full brightness and a
/// freshly-picked random position/color, held static (no wander, no falloff)
/// until the next chord hit or until chord mode ends via
/// <see cref="SetChordMode"/>.
///
/// Whatever's computed above (wave field or chord beam) is a per-frame
/// *target*, not what's actually shown - <see cref="OnRendering"/> eases
/// each beam's displayed color/tilt toward that target rather than snapping
/// straight to it, so switching between the wandering fan and a chord hit
/// (or between one chord hit and the next) crossfades smoothly instead of
/// cutting.
/// </summary>
public sealed class LaserFanAnimator
{
    private const double IntensityFadeSeconds = 1.5;

    // How many independent lit groups each half of the fan shows at once -
    // mirrored to the other half, so the whole fan shows twice this many.
    private const int GroupCount = 2;

    // Blob radius range, in beam-index units (the fan is only
    // LaserFixture.BeamCount beams wide, vs. hundreds of pixels for an LED
    // strip, so these are scaled down from LedStripLavaLampAnimator's
    // pixel-based constants). Scaled to LaserFixture.BeamCount so the fan
    // keeps showing groups of a consistent relative size regardless of beam
    // count. Sized so a typical (average-radius) pair of neighboring groups
    // leaves a real gap between them - overlap still happens, but only when
    // radius and wander both land toward the high end at the same time.
    private const double MinBlobRadiusBeams = 0.8 * BeamCountScale;
    private const double MaxBlobRadiusBeams = 2.0 * BeamCountScale;

    // The radius constants above were tuned against a 21-beam fan (20
    // beam-index units of domain); this rescales them so a different
    // LaserFixture.BeamCount still yields visually consistent group sizes.
    private const double BeamCountScale = (LaserFixture.BeamCount - 1) / 20.0;

    // Spatial frequency for the per-group noise samples, in units of
    // "group index" - decorrelates neighboring groups from each other.
    private const double GroupSpatialFrequency = 0.7;

    // How fast each group's position/radius/tilt wanders - fast enough to
    // read as active movement rather than a slow ambient breathe, but eased
    // back a bit from its original 0.9 for a less frantic default pace (the
    // dynamic SpeedFactor below scales this further with the music's energy).
    private const double GroupEvolveSpeed = 0.65;

    // Spatial frequency across beam index for hue - kept low enough that
    // the whole fan reads as close to one color at a time (real laser
    // modules typically show one color per moment, not several at once)
    // rather than banding into multiple hues across its width.
    private const double HueSpatialFrequency = 0.1;

    // How fast hue evolves over time - quicker than
    // LedStripLavaLampAnimator's HueEvolveSpeed so the laser's color visibly
    // shifts rather than drifting gradually, but not so fast it flickers
    // through colors.
    private const double HueEvolveSpeed = 0.1;

    private const double FlashSaturation = 0.95;
    private const double LightnessCap = 0.5;

    // A group's vertical tilt, in radians either side of dead ahead - kept
    // fairly narrow so the beams stay close to the horizontal plane in
    // front of the fixture rather than swinging widely up/down.
    private const double MaxTiltRadians = 0.10;

    // How long each beam's displayed color/tilt takes to ease toward
    // whatever's newly computed each frame - what makes switching between
    // the wandering fan and a chord hit (or one chord hit and the next)
    // read as a crossfade instead of a cut.
    private const double DisplayFadeSeconds = 0.4;

    /// <summary>
    /// Multiplies every lit beam's lightness - intended to follow the
    /// music's current volume (see EffectBrightnessTracker) so the effect
    /// breathes with the track rather than sitting at one fixed brightness.
    /// Defaults to 1.0 (no change) so the animator behaves exactly as
    /// before if nothing ever sets it.
    /// </summary>
    public double BrightnessFactor { get; set; } = 1.0;

    /// <summary>
    /// Multiplies how fast the animator's internal clock advances -
    /// intended to follow the music's current energy (see
    /// EffectSpeedTracker) so groups drift/pan faster during energetic
    /// passages and slower during calm ones. Defaults to 1.0 (no change) so
    /// the animator behaves exactly as before if nothing ever sets it.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    private readonly LaserFixture _laser;
    private readonly SmoothNoise _hueNoise;
    private readonly Random _random;

    // All three drive on-screen movement (group position, radius, and
    // tilt), so all use SineNoise rather than SmoothNoise - see SineNoise's
    // summary for why value noise looked jerky here once sampled fast along
    // the time axis.
    private readonly SineNoise _centerNoise;
    private readonly SineNoise _radiusNoise;
    private readonly SineNoise _tiltNoise;
    private readonly double[] _brightness = new double[LaserFixture.BeamCount];
    private readonly double[] _tilt = new double[LaserFixture.BeamCount];

    // What's actually shown, eased toward the per-frame target computed
    // above rather than snapping to it - see DisplayFadeSeconds.
    private readonly Color[] _displayColor = new Color[LaserFixture.BeamCount];
    private readonly double[] _displayTilt = new double[LaserFixture.BeamCount];

    private double _time;
    private double _targetIntensity;
    private double _currentIntensity;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    private bool _chordMode;
    private int _chordBeamIndex;
    private double _chordHue;

    public LaserFanAnimator(LaserFixture laser)
    {
        _laser = laser;

        _random = new Random();
        _hueNoise = new SmoothNoise(_random.Next());
        _centerNoise = new SineNoise(_random.Next());
        _radiusNoise = new SineNoise(_random.Next());
        _tiltNoise = new SineNoise(_random.Next());
    }

    /// <summary>Call whenever the thing this follows (currently: arpeggio/melodic-passage detection) changes state.</summary>
    public void SetActive(bool active)
    {
        _targetIntensity = active ? 1.0 : 0.0;
        EnsureRunning();
    }

    /// <summary>
    /// Switches between the normal wandering-group fan and single-beam
    /// chord mode. Turning chord mode off resumes the normal fan on the
    /// next frame - it doesn't fade or transition specially, since the
    /// caller (see <see cref="Audio.ChordSustainDetector"/>) only flips this
    /// when the music itself has already moved from one held chord back to
    /// a busy arpeggio/vocal line.
    /// </summary>
    public void SetChordMode(bool active) => _chordMode = active;

    /// <summary>
    /// Call on every new chord attack while in chord mode: picks a fresh
    /// random beam position and color, held static until the next hit.
    /// </summary>
    public void TriggerChordHit()
    {
        _chordMode = true;
        var domainEnd = (LaserFixture.BeamCount - 1) / 2;
        _chordBeamIndex = _random.Next(domainEnd + 1);
        _chordHue = _random.NextDouble() * 360.0;
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

        var beamCount = LaserFixture.BeamCount;

        // Only the left half is ever simulated; the right half just copies
        // its mirrored left-side counterpart below, the same trick
        // LedStripLavaLampAnimator uses to keep both sides of the rig
        // correlated instead of running two independent, unrelated fields.
        var domainEnd = (beamCount - 1) / 2;

        Array.Clear(_brightness, 0, _brightness.Length);

        if (_chordMode)
        {
            // One beam, fully lit, held static - no falloff, no wander.
            if (_chordBeamIndex <= domainEnd)
            {
                _brightness[_chordBeamIndex] = 1.0;
                _tilt[_chordBeamIndex] = 0.0;
            }
        }
        else
        {
            // Each group has its own home slot, evenly spaced across the
            // domain, but wanders freely around it - nothing here couples
            // one group's center to its neighbors', which is what lets gaps
            // collapse or groups overlap instead of always staying a fixed
            // distance apart.
            var homeSpacing = domainEnd / (double)GroupCount;
            var wanderRange = homeSpacing * 0.55;

            for (var g = 0; g < GroupCount; g++)
            {
                var homeCenter = homeSpacing * (g + 0.5);

                var centerNoise01 = _centerNoise.Sample(g * GroupSpatialFrequency, _time * GroupEvolveSpeed);
                var center = homeCenter + (centerNoise01 * 2.0 - 1.0) * wanderRange;

                var radiusNoise01 = _radiusNoise.Sample(g * GroupSpatialFrequency + 500.0, _time * GroupEvolveSpeed);
                var radius = MinBlobRadiusBeams + (MaxBlobRadiusBeams - MinBlobRadiusBeams) * radiusNoise01;

                var tiltNoise01 = _tiltNoise.Sample(g * GroupSpatialFrequency, _time * GroupEvolveSpeed);
                var tilt = (tiltNoise01 * 2.0 - 1.0) * MaxTiltRadians;

                var loIndex = Math.Max(0, (int)Math.Floor(center - radius));
                var hiIndex = Math.Min(domainEnd, (int)Math.Ceiling(center + radius));

                for (var i = loIndex; i <= hiIndex; i++)
                {
                    var distance = i - center;
                    if (Math.Abs(distance) >= radius)
                        continue;

                    // Raised-cosine falloff: 1 at the group's center, 0 at
                    // its edge, with zero slope at the edge too, so breaks
                    // touch black with no visible seam. Where two groups
                    // overlap, the brighter one wins instead of adding, so
                    // it reads as one continuous patch rather than a
                    // blown-out seam.
                    var t = distance / radius;
                    var falloff = 0.5 * (1.0 + Math.Cos(Math.PI * t));

                    if (falloff > _brightness[i])
                    {
                        _brightness[i] = falloff;
                        _tilt[i] = tilt;
                    }
                }
            }
        }

        // Every beam's displayed color/tilt eases toward this frame's
        // target rather than snapping to it - the same fade rate regardless
        // of what changed (blob movement, a chord hit, a mode switch), so
        // all of those read as one consistent smooth transition.
        var fadeAlpha = 1.0 - Math.Exp(-dt / DisplayFadeSeconds);

        var displayIsBlack = true;

        void EaseBeam(int index, Color targetColor, double targetTilt)
        {
            var current = _displayColor[index];
            _displayColor[index] = Color.FromRgb(
                (byte)(current.R + (targetColor.R - current.R) * fadeAlpha),
                (byte)(current.G + (targetColor.G - current.G) * fadeAlpha),
                (byte)(current.B + (targetColor.B - current.B) * fadeAlpha));
            _displayTilt[index] += fadeAlpha * (targetTilt - _displayTilt[index]);

            _laser.BeamColors[index] = _displayColor[index];
            _laser.BeamTiltRadians[index] = _displayTilt[index];

            var c = _displayColor[index];
            if (c.R != 0 || c.G != 0 || c.B != 0)
                displayIsBlack = false;
        }

        for (var i = 0; i <= domainEnd; i++)
        {
            var hue = _chordMode
                ? _chordHue
                : _hueNoise.Sample(i * HueSpatialFrequency + 500.0, _time * HueEvolveSpeed) * 360.0;
            var lightness = _brightness[i] * LightnessCap * _currentIntensity * BrightnessFactor;
            var color = lightness <= 0.001 ? Colors.Black : HslColor.FromHsl(hue, FlashSaturation, lightness);

            EaseBeam(i, color, _tilt[i]);

            var mirrorIndex = beamCount - 1 - i;
            if (mirrorIndex != i)
            {
                // Tilt is negated, not copied straight across: the right
                // side's beam vector points the opposite horizontal
                // direction from the left's, so rotating it by the same
                // signed angle would send it the opposite way vertically
                // (left panning up, right panning down). Flipping the sign
                // here is what makes both sides pan up/down together
                // instead of in mirrored opposition.
                EaseBeam(mirrorIndex, color, -_tilt[i]);
            }
        }

        _laser.RaiseBeamsChanged();

        // Stopping as soon as _currentIntensity bottoms out isn't enough on
        // its own - the *displayed* color lags behind via the crossfade
        // above and can still be a faint non-black residue at that exact
        // moment, which would otherwise freeze in place as a leftover line
        // the instant rendering stops. Waiting for the display to have
        // actually reached black is what avoids that.
        if (_currentIntensity <= 0.0005 && _targetIntensity <= 0.0005 && displayIsBlack)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }
}
