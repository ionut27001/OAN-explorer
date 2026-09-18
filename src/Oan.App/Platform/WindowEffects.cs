using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Oan.App.Platform;

internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    /// <summary>Mica needs Windows 11 22H2; earlier builds ignore the attribute and stay opaque.</summary>
    public static bool TryMica(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22621) return false;

        var hwnd = Handle(window);
        if (hwnd == 0) return false;

        var backdrop = DwmsbtMainWindow;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }

    public static void UseDarkTitleBar(Window window, bool dark)
    {
        var hwnd = Handle(window);
        if (hwnd == 0) return;

        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    public static nint Handle(Window window) => new WindowInteropHelper(window).Handle;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
