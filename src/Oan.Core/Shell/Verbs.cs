using System.Runtime.InteropServices;

namespace Oan.Core.Shell;

/// <summary>
/// The three shell actions the app invokes directly, without going through a menu. Each returns
/// true when the action ran <em>or</em> the user cancelled — false means a real failure, so a
/// caller can report it without accusing the user of an error when they pressed Escape.
/// </summary>
public static class ShellVerbs
{
    /// <summary>Runs the default action. Windows shows its own "open with" prompt if nothing is associated.</summary>
    public static bool Open(string path, nint owner)
    {
        var info = new Native.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_INVOKEIDLIST | Native.SEE_MASK_NOASYNC,
            hwnd = owner,
            lpVerb = "open",
            lpFile = path,
            lpDirectory = Path.GetDirectoryName(path),
            nShow = Native.SW_SHOWNORMAL,
        };

        // Declining the elevation prompt comes back as a failure with ERROR_CANCELLED.
        return Native.ShellExecuteEx(ref info) || Marshal.GetLastWin32Error() == Native.ERROR_CANCELLED;
    }

    public static bool OpenWith(string path, nint owner)
    {
        var info = new Native.OPENASINFO
        {
            FileName = path,
            FileClass = null,
            InFlags = Native.OAIF_EXEC | Native.OAIF_ALLOW_REGISTRATION,
        };

        // Dismissing the dialog is the ordinary outcome, not something to report.
        var result = Native.SHOpenWithDialog(owner, info);
        return result == 0 || result == Native.HRESULT_CANCELLED;
    }

    public static bool Properties(string path, nint owner) =>
        Native.SHObjectProperties(owner, Native.SHOP_FILEPATH, path, null);
}
