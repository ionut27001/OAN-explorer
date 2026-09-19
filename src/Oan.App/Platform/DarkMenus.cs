using System.Runtime.InteropServices;

namespace Oan.App.Platform;

/// <summary>
/// Makes the shell's own context menus dark.
///
/// The title bar has a documented flag; menus do not. A Win32 menu is drawn by the system in the
/// theme the PROCESS has asked for, and the only way to ask is <c>SetPreferredAppMode</c> —
/// exported from uxtheme.dll by ordinal 135 and by no name at all. Explorer and every dark file
/// manager on Windows calls it; there is no documented alternative, and without it a dark window
/// opens a white menu.
///
/// Undocumented means it can be withdrawn. Everything here is best-effort: if the export is gone
/// or the call fails, the menus stay light and nothing else changes.
/// </summary>
internal static class DarkMenus
{
    private enum PreferredAppMode { Default, AllowDark, ForceDark, ForceLight, Max }

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(PreferredAppMode mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>Why it could not be applied, or null. Never throws: a menu colour is not worth a crash.</summary>
    public static string? Apply(bool dark)
    {
        try
        {
            // ForceDark rather than AllowDark: AllowDark defers to each control's own opt-in,
            // which a menu the shell creates has never done, so the menu would stay light while
            // everything else went dark — the exact half-applied look this is here to avoid.
            SetPreferredAppMode(dark ? PreferredAppMode.ForceDark : PreferredAppMode.ForceLight);
            FlushMenuThemes();
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "this build of Windows does not export the menu theme switch";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
