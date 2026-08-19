using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AEPiLightingSystem.Models;

namespace AEPiLightingSystem.Controls;

/// <summary>
/// Renders an <see cref="LedStrip"/> as a vertical bar with one row of the
/// underlying bitmap per physical pixel, drawn top (pixel 0) to bottom.
/// </summary>
public partial class LedStripView : UserControl
{
    private const int BitmapWidth = 12;

    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;

    public static readonly DependencyProperty StripProperty = DependencyProperty.Register(
        nameof(Strip),
        typeof(LedStrip),
        typeof(LedStripView),
        new PropertyMetadata(null, OnStripChanged));

    public LedStrip? Strip
    {
        get => (LedStrip?)GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    public LedStripView()
    {
        InitializeComponent();
    }

    private static void OnStripChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LedStripView)d;

        if (e.OldValue is LedStrip oldStrip)
            oldStrip.PixelsChanged -= view.OnPixelsChanged;

        if (e.NewValue is LedStrip newStrip)
        {
            newStrip.PixelsChanged += view.OnPixelsChanged;
            view.InitializeBitmap(newStrip.PixelCount);
            view.Draw(newStrip);
        }
    }

    private void InitializeBitmap(int pixelCount)
    {
        _bitmap = new WriteableBitmap(BitmapWidth, pixelCount, 96, 96, PixelFormats.Bgra32, null);
        _pixelBuffer = new byte[BitmapWidth * pixelCount * 4];
        PixelImage.Source = _bitmap;
    }

    private void OnPixelsChanged(object? sender, EventArgs e)
    {
        if (Strip is null)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Draw(Strip));
            return;
        }

        Draw(Strip);
    }

    private void Draw(LedStrip strip)
    {
        if (_bitmap is null || _pixelBuffer is null)
            return;

        var pixels = strip.Pixels;
        var stride = BitmapWidth * 4;

        for (var row = 0; row < pixels.Length; row++)
        {
            var color = pixels[row];
            var rowOffset = row * stride;

            for (var col = 0; col < BitmapWidth; col++)
            {
                var offset = rowOffset + col * 4;
                _pixelBuffer[offset] = color.B;
                _pixelBuffer[offset + 1] = color.G;
                _pixelBuffer[offset + 2] = color.R;
                _pixelBuffer[offset + 3] = color.A;
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, BitmapWidth, pixels.Length), _pixelBuffer, stride, 0);
    }
}
