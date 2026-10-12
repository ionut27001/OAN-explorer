using System.Runtime.InteropServices;

namespace Oan.App.Platform;

/// <summary>
/// Makes the shell's own context menus dark. A Win32 menu is drawn in the theme the PROCESS asked for, and the
/// only way to ask is SetPreferredAppMode, exported from uxtheme.dll by ordinal 135 and no name.
/// Undocumented, so best-effort: if it fails the menus stay light.
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
            // ForceDark, not AllowDark: AllowDark defers to each control's own opt-in, which a shell menu never makes.
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
