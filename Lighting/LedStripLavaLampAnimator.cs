using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// A slow, organic "lava lamp" animation for the LED strips: blobs of
/// varying size packed along each strip with a constant gap between them,
/// hue via the same 95% saturation HSL rule used elsewhere. Turning it
/// on/off fades the whole effect's intensity smoothly rather than
/// snapping, so it can be driven directly by whether a melodic passage is
/// currently detected.
///
/// The strips aren't animated independently - think of the ceiling as one
/// continuous field, with each strip sampling its own narrow vertical
/// slice of it (strip index is the horizontal coordinate). Because
/// neighboring strips sample nearby columns of the same noise, adjacent
/// strips naturally show similar colors instead of independent random
/// ones. The rig is also mirrored left/right: only the left half is ever
/// sampled, and each right-side strip just copies its mirrored left-side
/// counterpart.
///
/// Blobs are bounded by a sequence of "boundary" points, not stacked one
/// after another from a fixed origin. Boundary c's position is computed
/// directly from (strip, c, time) - c*averageSpacing plus a bounded,
/// independent noise-driven jitter - with no dependency on any other
/// boundary's value. Blob c then sits between boundary c and boundary
/// c+1, with the gap eating into both ends:
///
///   radius[c] = (boundary[c+1] - boundary[c] - gap) / 2
///
/// This is the opposite of naively stacking blobs by cumulative sum: there
/// position is *derived from* size (so any blob's size change reshuffles
/// every blob after it - with many blobs all breathing independently every
/// frame, those shifts compound into the whole chain visibly accelerating
/// the deeper into it you look). Here it's inverted - size is *derived
/// from* two independent boundary positions - so when boundary c's jitter
/// moves, only the two blobs that share it (c-1 and c) are affected at
/// all: one's right edge moves, the other's left edge moves the same way,
/// and between them they redistribute the space - exactly the "the
/// neighbor expands to fill the gap" behavior wanted, with nothing further
/// down the chain so much as shifting a pixel. Every boundary is
/// independent of every other, so this isn't just unlikely to propagate
/// further, it's structurally impossible for it to.
///
/// Boundaries aren't tied to the physical strip - they're generated along
/// a virtual strip <see cref="VirtualStripLengthMultiplier"/>x longer than
/// the physical one, and only whichever slice falls within each strip's
/// window gets rendered. That's what makes blob count per strip genuinely
/// random rather than fixed: a stretch that happens to have widely-spaced
/// boundaries might show only 2 blobs in the visible window, a stretch of
/// closely-spaced ones might show 8. The one thing that never varies is
/// the gap. Each strip's window is picked once and held fixed rather than
/// drifting over time - an earlier version drifted it slowly, but sized
/// for a much shorter range than the virtual strip actually ended up
/// being, so the whole strip visibly scrolled far faster than intended.
/// Since bulk motion wasn't wanted at all, the fix was to remove viewport
/// drift entirely rather than retune it: individual blobs still breathe in
/// place from boundary jitter evolving slowly over time, but nothing
/// bulk-scrolls.
///
/// Everything here - jitter and hue - is recomputed fresh from (position,
/// time) every frame rather than integrated forward and
/// carried across frames, so nothing can accumulate or diverge no matter
/// how long the effect runs. Each blob's edge fades to black via a
/// raised-cosine falloff (touches zero with zero slope, so there's no seam
/// even without the blur pass).
/// </summary>
public sealed class LedStripLavaLampAnimator
{
    // How far apart (in noise-space) adjacent strips sample the shared
    // field. Small relative to one noise cell (1.0) so neighbors stay
    // strongly correlated.
    private const double StripHorizontalSpacing = 0.35;

    // Boundaries are generated along a virtual strip this many times
    // longer than the physical one, and only a slowly-drifting window into
    // it is ever rendered - this is what lets blob count vary randomly
    // instead of always being the same fixed number squeezed into one
    // strip.
    private const double VirtualStripLengthMultiplier = 8.0;

    // The constant gap enforced between every pair of neighboring blobs'
    // edges - the one thing that stays exactly the same everywhere as
    // blobs grow, shrink, and the viewport drifts.
    private const double BlobGapPixels = 27.0;

    private const double MinBlobRadiusPixels = 12.0;
    private const double MaxBlobRadiusPixels = 60.0;

    // Derived so that boundary spacing of (AverageBoundarySpacing +/-
    // BoundaryJitterAmplitude) works out to exactly
    // [MinBlobRadiusPixels, MaxBlobRadiusPixels] once the gap is
    // subtracted and split into a radius.
    private const double AverageBoundarySpacing = MinBlobRadiusPixels + MaxBlobRadiusPixels + BlobGapPixels;
    private const double BoundaryJitterAmplitude = (MaxBlobRadiusPixels - MinBlobRadiusPixels) / 2.0;

    // Spatial frequency for the per-boundary jitter noise, in units of
    // "boundary index" - low enough that neighboring boundaries are still
    // somewhat correlated without every one just repeating the same value.
    private const double BoundarySpatialFrequency = 0.5;

    // How fast each boundary's jitter evolves - kept slow and gentle so
    // blobs breathe (grow/shrink, with their immediate neighbor
    // compensating) rather than visibly racing around.
    private const double BoundaryEvolveSpeed = 0.3;

    // Low enough that a full strip only crosses a fraction of one noise
    // cell, so hue stays close to uniform along a strip's length instead
    // of banding through several colors.
    // Low enough that even the naturally-differing blob positions between
    // strips (each strip lays out its own independent chain, so a blob's
    // exact center never lines up perfectly row-to-row) don't swing hue
    // much on their own - consistency across strips depends on this being
    // small, not just on HueStripInfluence below.
    private const double HueSpatialFrequency = 0.0003;

    // How much a strip's own horizontal position is allowed to influence
    // its hue - kept tiny (rather than 0) so strips stay almost, but not
    // quite, perfectly identical.
    private const double HueStripInfluence = 0.02;

    // Hue keeps evolving over time even though color is now shared across
    // strips, so the whole rig's color slowly drifts rather than settling
    // into a fixed pattern.
    private const double HueEvolveSpeed = 0.015;

    private const double LightnessCap = 0.5;
    private const double FlashSaturation = 0.95;

    private const double IntensityFadeSeconds = 1.5;

    // Final smoothing pass along each strip's pixel index - a small
    // Gaussian-ish blur applied on top of each blob's own raised-cosine
    // falloff for extra polish; not load-bearing for smoothness the way it
    // was for an earlier threshold-based version of this effect.
    private static readonly double[] BlurWeights = { 1, 6, 15, 20, 15, 6, 1 }; // radius 3, sums to 64
    private const int BlurRadius = 3;
    private const double BlurWeightTotal = 64.0;

    // Safety cap on how many boundaries get walked in one frame while
    // covering the viewport - comfortably more than needed even at the
    // smallest possible blob spacing. Purely defensive.
    private const int MaxBoundariesPerFrame = 512;

    private readonly IReadOnlyList<LedStrip> _strips;
    private readonly SmoothNoise _hueNoise;
    private readonly SmoothNoise _boundaryJitterNoise;

    /// <summary>
    /// Multiplies every lit pixel's lightness - intended to follow the
    /// music's current volume (see EffectBrightnessTracker) so the effect
    /// breathes with the track rather than sitting at one fixed brightness.
    /// Defaults to 1.0 (no change) so the animator behaves exactly as
    /// before if nothing ever sets it.
    /// </summary>
    public double BrightnessFactor { get; set; } = 1.0;

    /// <summary>
    /// Multiplies how fast the animator's internal clock advances -
    /// intended to follow the music's current energy (see
    /// EffectSpeedTracker) so blobs breathe/drift faster during energetic
    /// passages and slower during calm ones. Defaults to 1.0 (no change) so
    /// the animator behaves exactly as before if nothing ever sets it.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    private double _time;
    private double _targetIntensity;
    private double _currentIntensity;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    // The rig is mirrored left/right: only the left half's noise field is
    // ever sampled, and each right-side strip just copies its mirrored
    // left-side counterpart's pixels every frame.
    private readonly int _leftStripCount;

    // Raw (pre-blur) colors for each left-side strip, reused every frame so
    // the blur pass has unblurred neighbor values to read from without
    // extra per-frame allocation.
    private readonly Color[][] _rawColors;

    private readonly double _virtualStripLength;

    // Each strip's window into the virtual chain is picked once and held
    // fixed - there's no drift, so the strip as a whole never scrolls.
    // Individual blobs still breathe (grow/shrink, with their immediate
    // neighbor compensating) since boundary jitter keeps evolving, but
    // nothing bulk-moves.
    private readonly double[] _viewportStarts;

    public LedStripLavaLampAnimator(IReadOnlyList<LedStrip> strips)
    {
        _strips = strips;
        _leftStripCount = strips.Count / 2;

        var random = new Random();
        _hueNoise = new SmoothNoise(random.Next());
        _boundaryJitterNoise = new SmoothNoise(random.Next());

        _rawColors = new Color[_leftStripCount][];
        _viewportStarts = new double[_leftStripCount];
        for (var s = 0; s < _leftStripCount; s++)
            _rawColors[s] = new Color[strips[s].PixelCount];

        _virtualStripLength = strips[0].PixelCount * VirtualStripLengthMultiplier;

        var maxViewportStart = Math.Max(0.0, _virtualStripLength - strips[0].PixelCount);
        for (var s = 0; s < _leftStripCount; s++)
            _viewportStarts[s] = random.NextDouble() * maxViewportStart;
    }

    /// <summary>
    /// Fades the whole effect in (true) or out (false). Safe to call
    /// repeatedly with the same value - it just holds the current target.
    /// </summary>
    public void SetActive(bool active)
    {
        _targetIntensity = active ? 1.0 : 0.0;
        EnsureRunning();
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    // Boundary c's position, independent of every other boundary's value -
    // this is what guarantees a change here can only ever affect the two
    // blobs adjacent to it.
    private double Boundary(double stripX, int c, double time)
    {
        var jitterRaw01 = _boundaryJitterNoise.Sample(
            stripX * BoundarySpatialFrequency + 500.0,
            c * BoundarySpatialFrequency + time * BoundaryEvolveSpeed);
        var jitter = (jitterRaw01 * 2.0 - 1.0) * BoundaryJitterAmplitude;
        return c * AverageBoundarySpacing + jitter;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        _time += dt * SpeedFactor;

        var intensityStep = dt / IntensityFadeSeconds;
        if (_currentIntensity < _targetIntensity)
            _currentIntensity = Math.Min(_targetIntensity, _currentIntensity + intensityStep);
        else if (_currentIntensity > _targetIntensity)
            _currentIntensity = Math.Max(_targetIntensity, _currentIntensity - intensityStep);

        for (var s = 0; s < _leftStripCount; s++)
        {
            var strip = _strips[s];
            var pixels = strip.Pixels;
            var raw = _rawColors[s];
            var stripX = s * StripHorizontalSpacing;

            var viewportStart = _viewportStarts[s];
            var viewportEnd = viewportStart + pixels.Length;

            Array.Clear(raw, 0, raw.Length);

            // Jump straight to roughly the right boundary index instead of
            // walking from index 0 every frame - boundary positions don't
            // depend on each other, so there's no need to compute the ones
            // before the viewport at all. A small margin covers the jitter
            // possibly pulling a boundary earlier than its average slot.
            var startC = Math.Max(0, (int)(viewportStart / AverageBoundarySpacing) - 2);

            var leftBoundary = Boundary(stripX, startC, _time);

            for (var c = startC; c < startC + MaxBoundariesPerFrame && leftBoundary < viewportEnd; c++)
            {
                var rightBoundary = Boundary(stripX, c + 1, _time);
                var radius = (rightBoundary - leftBoundary - BlobGapPixels) / 2.0;
                var center = (leftBoundary + rightBoundary) / 2.0;

                if (radius > 0 && center + radius >= viewportStart && center - radius <= viewportEnd)
                {
                    var hueRaw01 = _hueNoise.Sample(
                        stripX * HueStripInfluence + 500.0,
                        center * HueSpatialFrequency + _time * HueEvolveSpeed);
                    var hue = hueRaw01 * 360.0;

                    var localCenter = center - viewportStart;
                    var loIndex = Math.Max(0, (int)Math.Floor(localCenter - radius));
                    var hiIndex = Math.Min(pixels.Length - 1, (int)Math.Ceiling(localCenter + radius));

                    for (var i = loIndex; i <= hiIndex; i++)
                    {
                        var distance = i - localCenter;
                        if (Math.Abs(distance) >= radius)
                            continue;

                        // Raised-cosine falloff: 1 at the blob's center, 0
                        // at its edge, and - unlike a linear or clamped
                        // falloff - its slope is also 0 right at the edge,
                        // so it touches black with no visible seam.
                        var t = distance / radius;
                        var falloff = 0.5 * (1.0 + Math.Cos(Math.PI * t));
                        var lightness = falloff * LightnessCap * BrightnessFactor;

                        raw[i] = HslColor.FromHsl(hue, FlashSaturation, lightness);
                    }
                }

                leftBoundary = rightBoundary;
            }

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = Lerp(Colors.Black, BlurAt(raw, i), _currentIntensity);

            strip.RaisePixelsChanged();

            // Mirror this left-side strip straight across to its right-side
            // counterpart instead of computing an independent noise sample
            // for it.
            var mirrorStrip = _strips[_strips.Count - 1 - s];
            Array.Copy(pixels, mirrorStrip.Pixels, pixels.Length);
            mirrorStrip.RaisePixelsChanged();
        }

        if (_currentIntensity <= 0.0005 && _targetIntensity <= 0.0005)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private static Color BlurAt(Color[] raw, int index)
    {
        double r = 0, g = 0, b = 0;
        var lastIndex = raw.Length - 1;

        for (var k = -BlurRadius; k <= BlurRadius; k++)
        {
            var sampleIndex = Math.Clamp(index + k, 0, lastIndex);
            var weight = BlurWeights[k + BlurRadius];
            var c = raw[sampleIndex];

            r += c.R * weight;
            g += c.G * weight;
            b += c.B * weight;
        }

        return Color.FromRgb((byte)(r / BlurWeightTotal), (byte)(g / BlurWeightTotal), (byte)(b / BlurWeightTotal));
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        byte LerpChannel(byte a, byte b) => (byte)(a + (b - a) * t);

        return Color.FromRgb(
            LerpChannel(from.R, to.R),
            LerpChannel(from.G, to.G),
            LerpChannel(from.B, to.B));
    }
}
