using System.Runtime.InteropServices;

namespace Oan.Core.Shell;

/// <summary>
/// The COM half of the context menu: turning paths into an <c>IContextMenu</c> and an HMENU.
///
/// Separated from ShellContextMenu because the two answer different questions. This one knows
/// how the shell is asked; that one knows what to do when it does not answer — the deadline, the
/// refusal wording, and which kinds of file have hung before. Both were in one file until it
/// grew past the length the shape check allows, and the seam was already there.
///
/// Everything here runs on the STA thread its caller set up. None of it is safe to call from the
/// window's own thread: that is the whole point of the arrangement.
/// </summary>
internal static class MenuBuilder
{
    /// <summary>The shell's ids start here; everything below belongs to OAN's own entries.</summary>
    internal const uint FirstCommand = 100;
    internal const uint LastCommand = 0x7FFF;

    internal static (Native.IContextMenu? Menu, nint Popup) BuildForFolder(
        nint owner, string folder, uint flags, IReadOnlyList<OwnMenuItem>? own)
    {
        if (Native.SHParseDisplayName(folder, 0, out var pidl, 0, out _) != 0 || pidl == 0) return default;

        try
        {
            if (Native.SHBindToObject(0, pidl, 0, Native.IID_IShellFolder, out var raw) != 0 || raw == 0)
                return default;

            Native.IShellFolder shellFolder;
            try { shellFolder = (Native.IShellFolder)Marshal.GetObjectForIUnknown(raw); }
            finally { Marshal.Release(raw); }

            if (shellFolder.CreateViewObject(owner, Native.IID_IContextMenu, out var view) != 0 || view == 0)
                return default;

            Native.IContextMenu menu;
            try { menu = (Native.IContextMenu)Marshal.GetObjectForIUnknown(view); }
            finally { Marshal.Release(view); }

            var popup = Native.CreatePopupMenu();
            if (popup == 0 || menu.QueryContextMenu(popup, 0, FirstCommand, LastCommand, flags) < 0)
            {
                ShellContextMenu.Release(menu, popup);
                return default;
            }

            InsertOwn(popup, own);
            return (menu, popup);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static void InsertOwn(nint popup, IReadOnlyList<OwnMenuItem>? own)
    {
        if (own is not { Count: > 0 }) return;

        Native.InsertMenuW(popup, 0, Native.MF_BYPOSITION | Native.MF_SEPARATOR, 0, null);
        for (var i = own.Count - 1; i >= 0; i--)
            Native.InsertMenuW(popup, 0, Native.MF_BYPOSITION, (nuint)own[i].Id, own[i].Label);
    }

    /// <summary>
    /// Everything that loads somebody else's code: binding the shell object for the selection, and
    /// asking it — and every extension registered for these files — to fill a menu. This is the
    /// call that hangs on this machine, so it only ever runs on the STA thread.
    /// </summary>
    internal static (Native.IContextMenu? Menu, nint Popup) Build(IReadOnlyList<string> paths, uint flags, IReadOnlyList<OwnMenuItem>? own = null)
    {
        var pidls = new List<nint>(paths.Count);
        try
        {
            // One path the shell cannot parse — a deleted item still in the list, a dropped
            // share — must not cost the user the menu for everything else selected.
            foreach (var path in paths)
                if (Native.SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 && pidl != 0)
                    pidls.Add(pidl);

            if (pidls.Count == 0) return default;

            var menu = Bind(pidls.ToArray());
            if (menu is null) return default;

            var popup = Native.CreatePopupMenu();
            if (popup != 0 && menu.QueryContextMenu(popup, 0, FirstCommand, LastCommand, flags) >= 0)
            {
                // Ours go in after the shell has filled the menu, at the top, so they are the
                // first thing under the cursor and the shell's numbering is untouched.
                InsertOwn(popup, own);
                return (menu, popup);
            }

            ShellContextMenu.Release(menu, popup);
            return default;
        }
        finally
        {
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static Native.IContextMenu? Bind(nint[] pidls)
    {
        if (Native.SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var items) != 0)
            return null;

        try
        {
            if (items.BindToHandler(0, Native.BHID_SFUIObject, Native.IID_IContextMenu, out var menu) != 0 || menu == 0)
                return null;

            try
            {
                return (Native.IContextMenu)Marshal.GetObjectForIUnknown(menu);
            }
            finally
            {
                Marshal.Release(menu);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(items);
        }
    }
}
