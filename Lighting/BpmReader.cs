using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Reads the 2 deck BPM numbers (the big numbers inside the circles) out of
/// the Rekordbox screen mirror via Windows' built-in OCR engine, and stores
/// the confirmed values.
///
/// Each raw OCR read only overwrites the last confirmed value once the same
/// number has come back <see cref="RequiredConsecutiveMatches"/> times in a
/// row - a single misread frame (motion blur, a mid-transition digit, a
/// stray glyph) never touches the stored value, only a sustained, repeated
/// read does. That debounce is what makes this "extremely reliable" rather
/// than just "OCR on a screenshot".
/// </summary>
public sealed class BpmReader
{
    // Crop rectangles for each deck's BPM circle within the 700x150
    // Rekordbox mirror capture (see ScreenMirrorCapture). Rough starting
    // guesses - internal so MainWindow can draw them as an overlay on the
    // live mirror for visual tuning, rather than guessing blind.
    // Shrunk from the original 170x100 guess (then halved, then flattened),
    // keeping the same center (165, 75) fixed throughout.
    internal static readonly Int32Rect Deck1Region = new(133, 52, 65, 26);
    // Quartered from the original 170x100 guess, keeping the same center
    // (535, 75) fixed.
    internal static readonly Int32Rect Deck2Region = new(469, 49, 65, 26);

    private const int UpscaleFactor = 4;
    private const int RequiredConsecutiveMatches = 2;

    // A misread wildly outside plausible DJ tempos is discarded outright
    // rather than being allowed to debounce its way into the stored value.
    private const double MinPlausibleBpm = 40.0;
    private const double MaxPlausibleBpm = 220.0;

    private readonly OcrEngine? _ocrEngine =
        OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));

    private double? _pendingDeck1Value;
    private int _deck1MatchCount;
    private double? _pendingDeck2Value;
    private int _deck2MatchCount;

    public double? Deck1Bpm { get; private set; }
    public double? Deck2Bpm { get; private set; }

    // The exact preprocessed (cropped/upscaled/binarized) image handed to
    // the OCR engine for each deck - wired to small preview Image controls
    // in the GUI so the crop rectangles above can be visually verified and
    // tuned against the real capture.
    public WpfBitmapSource? Deck1DebugPreview { get; private set; }
    public WpfBitmapSource? Deck2DebugPreview { get; private set; }

    public bool IsAvailable => _ocrEngine is not null;

    public async Task ReadAsync(WpfBitmapSource capture)
    {
        if (_ocrEngine is null)
            return;

        var (deck1Value, deck1Preview) = await ReadOneAsync(capture, Deck1Region);
        var (deck2Value, deck2Preview) = await ReadOneAsync(capture, Deck2Region);

        Deck1DebugPreview = deck1Preview;
        Deck2DebugPreview = deck2Preview;

        Deck1Bpm = Debounce(deck1Value, ref _pendingDeck1Value, ref _deck1MatchCount, Deck1Bpm);
        Deck2Bpm = Debounce(deck2Value, ref _pendingDeck2Value, ref _deck2MatchCount, Deck2Bpm);
    }

    private static double? Debounce(double? candidate, ref double? pending, ref int matchCount, double? current)
    {
        if (candidate is null)
        {
            pending = null;
            matchCount = 0;
            return current;
        }

        if (pending.HasValue && Math.Abs(pending.Value - candidate.Value) < 0.05)
            matchCount++;
        else
        {
            pending = candidate;
            matchCount = 1;
        }

        return matchCount >= RequiredConsecutiveMatches ? candidate : current;
    }

    private async Task<(double? Value, WpfBitmapSource Preview)> ReadOneAsync(WpfBitmapSource capture, Int32Rect region)
    {
        var preprocessed = Preprocess(capture, region);

        double? value = null;
        try
        {
            var softwareBitmap = await ToGray8SoftwareBitmapAsync(preprocessed);
            var result = await _ocrEngine!.RecognizeAsync(softwareBitmap);
            var text = string.Concat(result.Lines.Select(l => l.Text));
            var match = Regex.Match(text, @"\d+(\.\d+)?");
            if (match.Success && double.TryParse(match.Value, out var parsed)
                && parsed is >= MinPlausibleBpm and <= MaxPlausibleBpm)
            {
                value = parsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BPM OCR read failed: {ex.Message}");
        }

        return (value, preprocessed);
    }

    // Crops to just the number, upscales (small text OCRs far more
    // reliably once it's not tiny), then binarizes to solid black-on-white -
    // detecting whether the crop is light-text-on-dark or dark-text-on-light
    // first, since Rekordbox's skin/theme isn't something this app controls.
    private static WpfBitmapSource Preprocess(WpfBitmapSource capture, Int32Rect region)
    {
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(capture, region);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
            cropped, new ScaleTransform(UpscaleFactor, UpscaleFactor));
        var grayscale = new System.Windows.Media.Imaging.FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

        var width = grayscale.PixelWidth;
        var height = grayscale.PixelHeight;
        var stride = width;
        var pixels = new byte[height * stride];
        grayscale.CopyPixels(pixels, stride, 0);

        var average = pixels.Select(p => (double)p).DefaultIfEmpty(255).Average();
        var lightTextOnDark = average < 128;

        const byte threshold = 128;
        for (var i = 0; i < pixels.Length; i++)
        {
            var isForeground = lightTextOnDark ? pixels[i] > threshold : pixels[i] < threshold;
            pixels[i] = isForeground ? (byte)0 : (byte)255;
        }

        var result = WpfBitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static async Task<SoftwareBitmap> ToGray8SoftwareBitmapAsync(WpfBitmapSource wpfBitmap)
    {
        using var memoryStream = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(wpfBitmap));
        encoder.Save(memoryStream);
        memoryStream.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var outputStream = randomAccessStream.GetOutputStreamAt(0))
        {
            using var dataWriter = new DataWriter(outputStream);
            dataWriter.WriteBytes(memoryStream.ToArray());
            await dataWriter.StoreAsync();
            await outputStream.FlushAsync();
        }

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        return SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
    }
}
