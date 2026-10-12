using System.IO;
using System.Runtime.InteropServices;

namespace Oan.App.Platform;

/// <summary>
/// The system property sheet. It loads third-party property-page handlers, so it gets a dedicated STA thread
/// and no owner window: a page that hangs costs that dialog only, and cannot disable ours.
/// </summary>
public static class ShellProperties
{
    private const uint FilePath = 0x2;

    /// <summary>Null when the sheet was handed off, otherwise why it was not.</summary>
    public static string? Show(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return $"\"{path}\" is no longer there.";

        var thread = new Thread(() => SHObjectProperties(0, FilePath, path, null))
        {
            IsBackground = true,
            Name = "OAN properties",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(nint parent, uint type, string obj, string? page);
}
