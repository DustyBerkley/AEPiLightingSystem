using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AEPiLightingSystem.Lighting;

/// <summary>
/// Grabs the center 700x150px region of the middle monitor - where
/// Rekordbox runs fullscreen in the current 3-monitor setup (2x 1080p
/// external + laptop, laptop on the far right) - so it can be mirrored live
/// into the app's own GUI.
/// </summary>
public sealed class ScreenMirrorCapture
{
    public const int CaptureWidth = 700;
    public const int CaptureHeight = 150;

    // Nudges the capture rect off dead-center on the middle monitor, tuned
    // by eye against where Rekordbox's actual content sits.
    private const int OffsetRightPixels = 500;
    private const int OffsetUpPixels = 330;

    private const uint SrcCopy = 0x00CC0020;

    private readonly int _originX;
    private readonly int _originY;

    public ScreenMirrorCapture()
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();
        var middleIndex = screens.Length >= 3 ? screens.Length / 2 : 0;
        var bounds = screens[middleIndex].Bounds;

        _originX = bounds.X + (bounds.Width - CaptureWidth) / 2 + OffsetRightPixels;
        _originY = bounds.Y + (bounds.Height - CaptureHeight) / 2 - OffsetUpPixels;
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
        var bitmap = CreateCompatibleBitmap(screenDc, CaptureWidth, CaptureHeight);
        var oldBitmap = SelectObject(memoryDc, bitmap);

        try
        {
            var ok = BitBlt(memoryDc, 0, 0, CaptureWidth, CaptureHeight, screenDc, _originX, _originY, SrcCopy);
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
