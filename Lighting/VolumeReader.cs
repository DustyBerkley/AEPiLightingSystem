using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Reads each deck's channel fader volume off the narrow mirror capture
/// (see ScreenMirrorCapture/MainWindow's _narrowMirrorCapture) by counting
/// blue pixels in each half - the left half is deck 1's fader, the right
/// half is deck 2's. A fully-filled fader reads as <see cref="FullScaleBluePixels"/>
/// blue pixels (confirmed against deck 1 at max volume); 0 blue pixels means
/// the fader is all the way down. Both ends were confirmed against the real
/// capture before this scale was picked.
/// </summary>
public sealed class VolumeReader
{
    private const int FullScaleBluePixels = 96;

    public double Deck1Volume { get; private set; }
    public double Deck2Volume { get; private set; }

    public void Read(WpfBitmapSource capture)
    {
        var converted = new FormatConvertedBitmap(capture, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        var leftHalfWidth = width / 2;
        var leftBlue = CountBlue(pixels, stride, 0, leftHalfWidth, height);
        var rightBlue = CountBlue(pixels, stride, leftHalfWidth, width, height);

        Deck1Volume = Math.Clamp(leftBlue / (double)FullScaleBluePixels * 100.0, 0.0, 100.0);
        Deck2Volume = Math.Clamp(rightBlue / (double)FullScaleBluePixels * 100.0, 0.0, 100.0);
    }

    private static int CountBlue(byte[] pixels, int stride, int xStart, int xEnd, int height)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                var offset = y * stride + x * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                if (b > 100 && b > r + 30 && b > g + 30)
                    count++;
            }
        }
        return count;
    }
}
