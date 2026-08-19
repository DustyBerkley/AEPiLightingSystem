using System;
using System.Windows.Media;

namespace AEPiLightingSystem.Models;

/// <summary>
/// A WLED-driven diffused LED strip made up of individually addressable pixels.
/// </summary>
public class LedStrip
{
    public const int DefaultPixelCount = 300;

    public int Id { get; }

    public string Label { get; }

    public int PixelCount => Pixels.Length;

    public Color[] Pixels { get; }

    /// <summary>
    /// Raised after one or more pixels have been updated, so views can redraw.
    /// </summary>
    public event EventHandler? PixelsChanged;

    public LedStrip(int id, string label, int pixelCount = DefaultPixelCount)
    {
        Id = id;
        Label = label;
        Pixels = new Color[pixelCount];
        Array.Fill(Pixels, Colors.Black);
    }

    public void SetPixel(int index, Color color)
    {
        Pixels[index] = color;
        RaisePixelsChanged();
    }

    public void Fill(Color color)
    {
        Array.Fill(Pixels, color);
        RaisePixelsChanged();
    }

    /// <summary>
    /// Call after directly mutating <see cref="Pixels"/> in bulk to notify views.
    /// </summary>
    public void RaisePixelsChanged() => PixelsChanged?.Invoke(this, EventArgs.Empty);
}
