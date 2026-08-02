using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Chargle.Views;

/// <summary>
/// Converts the sizes we think in to the sizes Windows wants.
///
/// <c>AppWindow.Resize</c> and <c>MoveAndResize</c> take physical pixels, while every number in
/// XAML is in effective pixels. On a display at 100% those are the same and the difference never
/// shows up. On a 250% display, asking for a 780 pixel window gets you one that is 312 effective
/// pixels wide, so the interface renders at full size inside a window a third of the size it
/// needs, and everything is clipped. Passing every window dimension through here is what keeps
/// the app the same shape on every screen.
/// </summary>
internal static partial class DisplayScale
{
    public static double For(Window window)
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    public static SizeInt32 Size(Window window, int width, int height)
    {
        double scale = For(window);
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    public static int Round(double value, double scale) => (int)Math.Round(value * scale);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
