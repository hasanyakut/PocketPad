using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PocketPad.Desktop.Views;

/// <summary>
/// Asks DWM for a dark title bar (Windows 10 1809+) and, on Windows 11, paints the caption in the
/// app's background colour. Purely cosmetic: failures on older systems are ignored.
/// </summary>
internal static partial class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 1809–1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Windows 10 2004+ / 11
    private const int DWMWA_BORDER_COLOR = 34;                // Windows 11
    private const int DWMWA_CAPTION_COLOR = 35;               // Windows 11
    private const int DWMWA_TEXT_COLOR = 36;                  // Windows 11

    /// <summary>Call from the window constructor; applies once the native handle exists.</summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window);

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

        // COLORREF is 0x00BBGGRR.
        var caption = ToColorRef(0x14, 0x16, 0x1B);
        var border = ToColorRef(0x2C, 0x30, 0x39);
        var text = ToColorRef(0xEC, 0xEE, 0xF2);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
