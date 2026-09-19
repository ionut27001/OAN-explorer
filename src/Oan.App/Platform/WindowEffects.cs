using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Oan.App.Platform;

internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>
    /// Asks DWM for the system backdrop. It needs Windows 11 22H2; earlier builds ignore the
    /// attribute, so a false here means the caller must paint the window itself rather than
    /// leave a transparent one with nothing behind it.
    /// </summary>
    public static bool TryBackdrop(Window window, Backdrop kind)
    {
        if (Environment.OSVersion.Version.Build < 22621) return false;

        var hwnd = Handle(window);
        if (hwnd == 0) return false;

        // DWM_SYSTEMBACKDROP_TYPE: 1 none, 2 Mica, 3 Acrylic, 4 Mica Alt.
        var type = kind switch { Backdrop.Mica => 2, Backdrop.Acrylic => 3, Backdrop.MicaAlt => 4, _ => 1 };
        if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) != 0) return false;

        // The attribute alone gets you a black window. DWM draws the backdrop in the window's
        // frame, and the frame is the title bar until it is extended over the client area; and
        // WPF clears its own surface first, which is what the backdrop would be hiding behind.
        // Both of these are measured: without either one the chrome composites to #000000.
        var glass = kind == Backdrop.Solid ? 0 : -1;
        var margins = new Margins { Left = glass, Right = glass, Top = glass, Bottom = glass };
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;

        return true;
    }

    public static void UseDarkTitleBar(Window window, bool dark)
    {
        var hwnd = Handle(window);
        if (hwnd == 0) return;

        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    public static nint Handle(Window window) => new WindowInteropHelper(window).Handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
