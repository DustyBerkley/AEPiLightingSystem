using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Grabs a region of the middle monitor - where Rekordbox runs fullscreen in
/// the current 3-monitor setup (2x 1080p external + laptop, laptop on the
/// far right) - so it can be mirrored live into the app's own GUI.
///
/// Every instance is centered on the same fixed point regardless of its own
/// <see cref="Width"/>/<see cref="Height"/> - that point was tuned
/// interactively against the original 700x150 mirror, so a narrower/shorter
/// capture built from a different instance stays concentric with it rather
/// than needing its own offset tuning from scratch.
/// </summary>
public sealed class ScreenMirrorCapture
{
    public const int DefaultWidth = 700;
    public const int DefaultHeight = 150;

    // Nudges the shared center point off dead-center on the middle monitor,
    // tuned by eye against where Rekordbox's actual content sits.
    private const int OffsetRightPixels = 500;
    private const int OffsetUpPixels = 330;

    private const uint SrcCopy = 0x00CC0020;

    public int Width { get; }
    public int Height { get; }

    // Exposed so other captures can be positioned relative to this one
    // (e.g. "right below this box's bottom-right corner") without
    // duplicating monitor-lookup/offset math of their own.
    public int OriginX { get; }
    public int OriginY { get; }

    // centerOffsetXPixels/centerOffsetYPixels nudge an individual instance
    // away from the shared tuned center (positive = right/down) without
    // touching that shared point, so other instances stay put.
    public ScreenMirrorCapture(int width = DefaultWidth, int height = DefaultHeight, int centerOffsetXPixels = 0, int centerOffsetYPixels = 0)
    {
        Width = width;
        Height = height;

        var bounds = GetMiddleMonitorBounds();
        var centerX = bounds.X + bounds.Width / 2 + OffsetRightPixels + centerOffsetXPixels;
        var centerY = bounds.Y + bounds.Height / 2 - OffsetUpPixels + centerOffsetYPixels;

        OriginX = centerX - width / 2;
        OriginY = centerY - height / 2;
    }

    private ScreenMirrorCapture(int width, int height, (int X, int Y) origin)
    {
        Width = width;
        Height = height;
        OriginX = origin.X;
        OriginY = origin.Y;
    }

    /// <summary>
    /// A capture positioned independently of the shared tuned center above:
    /// horizontally centered on the middle monitor (plus
    /// <paramref name="offsetRightPixels"/>, positive = right), with its top
    /// edge <paramref name="topPixelsFromMonitorTop"/> down from the
    /// monitor's own top edge.
    /// </summary>
    public static ScreenMirrorCapture CenteredHorizontallyFromTop(int width, int height, int topPixelsFromMonitorTop, int offsetRightPixels = 0)
    {
        var bounds = GetMiddleMonitorBounds();
        var originX = bounds.X + (bounds.Width - width) / 2 + offsetRightPixels;
        var originY = bounds.Y + topPixelsFromMonitorTop;
        return new ScreenMirrorCapture(width, height, (originX, originY));
    }

    /// <summary>A capture placed at an explicit origin, e.g. relative to another capture's own OriginX/OriginY/Width/Height.</summary>
    public static ScreenMirrorCapture AtOrigin(int width, int height, int originX, int originY) =>
        new(width, height, (originX, originY));

    /// <summary>
    /// A capture spanning <paramref name="widthFraction"/> of the middle
    /// monitor's width (1.0 = full width), left-anchored to the monitor's
    /// own left edge (plus <paramref name="offsetRightPixels"/>, positive =
    /// right), with its top edge <paramref name="topPixelsFromMonitorTop"/>
    /// down from the monitor's own top edge.
    /// </summary>
    public static ScreenMirrorCapture FullWidthFromTop(int height, int topPixelsFromMonitorTop, double widthFraction = 1.0, int offsetRightPixels = 0)
    {
        var bounds = GetMiddleMonitorBounds();
        var width = (int)(bounds.Width * widthFraction);
        return new ScreenMirrorCapture(width, height, (bounds.X + offsetRightPixels, bounds.Y + topPixelsFromMonitorTop));
    }

    private static System.Drawing.Rectangle GetMiddleMonitorBounds()
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();
        var middleIndex = screens.Length >= 3 ? screens.Length / 2 : 0;
        return screens[middleIndex].Bounds;
    }

    // BitBlt/GetDC(NULL) operate in raw device pixels - the same space as
    // Screen.Bounds - unlike WPF's own Window.Left/Top, which are
    // DPI-virtualized DIPs (see PositionFullScreenOnLeftmostMonitor's own
    // comment in MainWindow.xaml.cs). No offset correction needed here.
    public BitmapSource? Capture()
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, Width, Height);
        var oldBitmap = SelectObject(memoryDc, bitmap);

        try
        {
            var ok = BitBlt(memoryDc, 0, 0, Width, Height, screenDc, OriginX, OriginY, SrcCopy);
            if (!ok)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
