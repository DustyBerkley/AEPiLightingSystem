using System;
using System.Collections.Generic;
using System.Windows.Media;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// A slow, organic "lava lamp" animation for the ceiling light bars' RGB
/// segments: blobs of varying size with a constant gap between them, hue via
/// the same 95% saturation HSL rule used elsewhere. Turning it on/off fades
/// the whole effect's intensity smoothly rather than snapping, so it can be
/// driven directly by whether a melodic passage is currently detected.
///
/// The ceiling is treated as one shared 2D field rather than each bar
/// running its own animation. Front-to-back, blobs are laid out along a
/// single chain in a shared depth coordinate (see
/// <see cref="CeilingLightBar.DepthOffset"/>), so a bar shows whichever
/// blobs sit at its depth. Side-to-side, every blob sways across the room
/// on its own phase and speed, and each bar shows it weighted by how close
/// the blob currently is to that bar's side - so a blob visibly drifts out
/// of the left bars and into the right ones and back.
///
/// Rendered at <see cref="VirtualPixelsPerBar"/> virtual pixels per bar -
/// the resolution of the LED strips this effect was originally tuned for,
/// so every pixel-sized constant below keeps its original meaning - then
/// averaged down to the bar's 16 real segments.
///
/// Blobs are bounded by a sequence of "boundary" points, each computed
/// directly from (c, time) - c*averageSpacing plus a bounded, noise-driven
/// jitter - with no dependency on any other boundary, so when one boundary
/// moves only the two blobs sharing it change: one's edge grows as the
/// other's shrinks, and nothing further down the chain shifts at all.
///
/// Color is shared across the whole ceiling, varying only slightly with
/// depth, so it reads as one color drifting over time.
///
/// Everything is recomputed fresh from (position, time) every frame rather
/// than integrated forward, so nothing can accumulate or diverge however
/// long it runs. Each blob's edge fades to black via a raised-cosine falloff.
/// </summary>
public sealed class LightBarLavaLampAnimator
{
    private const int VirtualPixelsPerBar = 300;

    // The chain of blobs is generated this many bar-lengths long, and the
    // ceiling renders a fixed random window into it.
    private const double VirtualChainLengthBars = 8.0;

    // The constant gap enforced between every pair of neighboring blobs'
    // edges, in virtual pixels.
    private const double BlobGapPixels = 27.0;

    private const double MinBlobRadiusPixels = 12.0;
    private const double MaxBlobRadiusPixels = 60.0;

    // Derived so that boundary spacing of (AverageBoundarySpacing +/-
    // BoundaryJitterAmplitude) works out to exactly
    // [MinBlobRadiusPixels, MaxBlobRadiusPixels] once the gap is subtracted
    // and split into a radius.
    private const double AverageBoundarySpacing = MinBlobRadiusPixels + MaxBlobRadiusPixels + BlobGapPixels;
    private const double BoundaryJitterAmplitude = (MaxBlobRadiusPixels - MinBlobRadiusPixels) / 2.0;

    // In units of "boundary index" - low enough that neighboring boundaries
    // are still somewhat correlated.
    private const double BoundarySpatialFrequency = 0.5;

    // How fast blobs grow/shrink against their neighbors.
    private const double BoundaryEvolveSpeed = 0.75;

    // One full left-right-left sway of a blob (at SpeedFactor 1, before its
    // own per-blob speed variation).
    private const double SwaySecondsPerCycle = 5.0;

    // Each blob sways at between (1 - this) and (1 + this) times the base
    // rate, so neighboring blobs drift in and out of step with each other
    // instead of the whole ceiling swinging as one.
    private const double SwaySpeedVariation = 0.25;

    // Slight shade shift front-to-back across the whole ceiling (depth in
    // virtual pixels) - small enough that every bar reads as nearly the same
    // color at once.
    private const double HueDepthFrequency = 0.0001;

    // The shared color drifts fairly quickly since it's the main thing
    // changing across the ceiling.
    private const double HueEvolveSpeed = 0.05;

    private const double LightnessCap = 0.5;
    private const double FlashSaturation = 0.95;

    private const double IntensityFadeSeconds = 1.5;

    private static readonly double[] BlurWeights = { 1, 6, 15, 20, 15, 6, 1 }; // radius 3, sums to 64
    private const int BlurRadius = 3;
    private const double BlurWeightTotal = 64.0;

    // Purely defensive cap on boundaries walked per bar per frame.
    private const int MaxBoundariesPerFrame = 512;

    private readonly IReadOnlyList<CeilingLightBar> _bars;
    private readonly SmoothNoise _hueNoise;
    private readonly SmoothNoise _boundaryJitterNoise;

    // Raw (pre-blur) virtual pixels, reused every frame.
    private readonly Color[] _raw = new Color[VirtualPixelsPerBar];

    // Where the ceiling's front edge sits along the virtual chain.
    private readonly double _chainStart;

    /// <summary>
    /// Multiplies every lit pixel's lightness - intended to follow the
    /// music's current volume (see EffectBrightnessTracker).
    /// </summary>
    public double BrightnessFactor { get; set; } = 1.0;

    /// <summary>
    /// Multiplies how fast the animator's internal clock advances - intended
    /// to follow the music's current energy (see EffectSpeedTracker).
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    private double _time;
    private double _targetIntensity;
    private double _currentIntensity;
    private DateTime _lastFrameTime;
    private bool _isRunning;

    public LightBarLavaLampAnimator(IReadOnlyList<CeilingLightBar> bars)
    {
        _bars = bars;

        var random = new Random();
        _hueNoise = new SmoothNoise(random.Next());
        _boundaryJitterNoise = new SmoothNoise(random.Next());

        var ceilingDepthBars = 1.0;
        foreach (var bar in bars)
            ceilingDepthBars = Math.Max(ceilingDepthBars, bar.DepthOffset + 1.0);

        var maxChainStart = Math.Max(0.0, (VirtualChainLengthBars - ceilingDepthBars) * VirtualPixelsPerBar);
        _chainStart = random.NextDouble() * maxChainStart;
    }

    /// <summary>
    /// True while a light-group override (see MainWindow's Launchpad row-2
    /// pads 1-4) is forcing this group off - SetActive is ignored outright
    /// while set, regardless of who's calling it.
    /// </summary>
    public bool IsSuppressed { get; private set; }

    /// <summary>
    /// Fades the whole effect in (true) or out (false). Safe to call
    /// repeatedly with the same value - it just holds the current target.
    /// </summary>
    public void SetActive(bool active)
    {
        if (IsSuppressed)
            return;

        _targetIntensity = active ? 1.0 : 0.0;
        EnsureRunning();
    }

    /// <summary>Force this group off (true) or let it respond to SetActive calls again (false).</summary>
    public void SetSuppressed(bool suppressed)
    {
        IsSuppressed = suppressed;
        if (!suppressed)
            return;

        _targetIntensity = 0.0;
        _currentIntensity = 0.0;

        foreach (var bar in _bars)
            bar.FillPixels(Colors.Black);

        if (_isRunning)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private void EnsureRunning()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    // Boundary c's position along the chain, independent of every other
    // boundary's value.
    private double Boundary(int c, double time)
    {
        var jitterRaw01 = _boundaryJitterNoise.Sample(
            500.0,
            c * BoundarySpatialFrequency + time * BoundaryEvolveSpeed);
        var jitter = (jitterRaw01 * 2.0 - 1.0) * BoundaryJitterAmplitude;
        return c * AverageBoundarySpacing + jitter;
    }

    // Blob c's current cross-room position: 0 = fully over the left bars,
    // 1 = fully over the right. Phase and speed come from fixed per-blob
    // hashes so each blob keeps its own consistent motion over time.
    private double SwayPosition(int c, double time)
    {
        var phase01 = Fraction(c * 0.6180339887);
        var speed = 1.0 + (Fraction(c * 0.4142135624) * 2.0 - 1.0) * SwaySpeedVariation;
        var angle = 2.0 * Math.PI * (time * speed / SwaySecondsPerCycle + phase01);
        return 0.5 + 0.5 * Math.Sin(angle);
    }

    private static double Fraction(double value) => value - Math.Floor(value);

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

        foreach (var bar in _bars)
        {
            RenderVirtualPixels(bar);
            DownsampleInto(bar);
            bar.RaiseChanged();
        }

        if (_currentIntensity <= 0.0005 && _targetIntensity <= 0.0005)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }

    private void RenderVirtualPixels(CeilingLightBar bar)
    {
        var barDepth = bar.DepthOffset * VirtualPixelsPerBar;
        var windowStart = _chainStart + barDepth;
        var windowEnd = windowStart + VirtualPixelsPerBar;
        var barSide = bar.IsLeftSide ? 0.0 : 1.0;

        Array.Clear(_raw, 0, _raw.Length);

        // Jump straight to roughly the right boundary index - boundaries
        // don't depend on each other, so the ones before the window never
        // need computing. The margin covers jitter pulling one earlier.
        var startC = Math.Max(0, (int)(windowStart / AverageBoundarySpacing) - 2);
        var leftBoundary = Boundary(startC, _time);

        for (var c = startC; c < startC + MaxBoundariesPerFrame && leftBoundary < windowEnd; c++)
        {
            var rightBoundary = Boundary(c + 1, _time);
            var radius = (rightBoundary - leftBoundary - BlobGapPixels) / 2.0;
            var center = (leftBoundary + rightBoundary) / 2.0;

            if (radius > 0 && center + radius >= windowStart && center - radius <= windowEnd)
            {
                // Raised-cosine across the room: full on this bar's side
                // when the blob is over it, gone when it's fully over the
                // other side.
                var sideDistance = Math.Abs(SwayPosition(c, _time) - barSide);
                var sideWeight = 0.5 * (1.0 + Math.Cos(Math.PI * Math.Min(sideDistance, 1.0)));

                if (sideWeight > 0.0)
                {
                    var hueRaw01 = _hueNoise.Sample(
                        500.0,
                        (center - _chainStart) * HueDepthFrequency + _time * HueEvolveSpeed);
                    var hue = hueRaw01 * 360.0;

                    var localCenter = center - windowStart;
                    var loIndex = Math.Max(0, (int)Math.Floor(localCenter - radius));
                    var hiIndex = Math.Min(VirtualPixelsPerBar - 1, (int)Math.Ceiling(localCenter + radius));

                    for (var i = loIndex; i <= hiIndex; i++)
                    {
                        var distance = i - localCenter;
                        if (Math.Abs(distance) >= radius)
                            continue;

                        // Raised-cosine along the bar: 1 at the center, 0 at
                        // the edge with zero slope there, so it touches
                        // black seamlessly.
                        var falloff = 0.5 * (1.0 + Math.Cos(Math.PI * distance / radius));
                        var lightness = falloff * sideWeight * LightnessCap * BrightnessFactor;

                        _raw[i] = HslColor.FromHsl(hue, FlashSaturation, lightness);
                    }
                }
            }

            leftBoundary = rightBoundary;
        }
    }

    // Averages each segment's share of the (blurred) virtual pixels into
    // that one real RGB segment.
    private void DownsampleInto(CeilingLightBar bar)
    {
        var segments = bar.Pixels;
        var perSegment = (double)VirtualPixelsPerBar / segments.Length;

        for (var s = 0; s < segments.Length; s++)
        {
            var start = (int)Math.Round(s * perSegment);
            var end = (int)Math.Round((s + 1) * perSegment);

            double r = 0, g = 0, b = 0;
            for (var i = start; i < end; i++)
            {
                var c = BlurAt(i);
                r += c.R;
                g += c.G;
                b += c.B;
            }

            var count = end - start;
            var averaged = Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            segments[s] = Lerp(Colors.Black, averaged, _currentIntensity);
        }
    }

    private Color BlurAt(int index)
    {
        double r = 0, g = 0, b = 0;
        var lastIndex = _raw.Length - 1;

        for (var k = -BlurRadius; k <= BlurRadius; k++)
        {
            var c = _raw[Math.Clamp(index + k, 0, lastIndex)];
            var weight = BlurWeights[k + BlurRadius];

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
