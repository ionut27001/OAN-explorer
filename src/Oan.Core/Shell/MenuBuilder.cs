using System.Runtime.InteropServices;

namespace Oan.Core.Shell;

/// <summary>The COM half of the context menu: turning paths into an <c>IContextMenu</c> and an HMENU.
/// Everything here must run on the STA thread its caller set up, never on the window's own thread.</summary>
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

            // Released here, not by the GC: the finalizer would have to wait for this thread, forever if an extension hangs it.
            int created;
            nint view;
            try { created = shellFolder.CreateViewObject(owner, Native.IID_IContextMenu, out view); }
            finally { Marshal.ReleaseComObject(shellFolder); }

            if (created != 0 || view == 0) return default;

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

    internal static void InsertOwn(nint popup, IReadOnlyList<OwnMenuItem>? own)
    {
        if (own is not { Count: > 0 }) return;

        Insert(popup, 0, OwnMenuItem.Separator);
        for (var i = own.Count - 1; i >= 0; i--) Insert(popup, 0, own[i]);
    }

    private static void Insert(nint menu, uint position, OwnMenuItem item)
    {
        if (item.Label.Length == 0)
        {
            Native.InsertMenuW(menu, position, Native.MF_BYPOSITION | Native.MF_SEPARATOR, 0, null);
            return;
        }

        var flags = Native.MF_BYPOSITION | (item.Checked ? Native.MF_CHECKED : 0) | (item.Enabled ? 0 : Native.MF_GRAYED);
        var id = (nuint)item.Id;

        // Kept nowhere: DestroyMenu on the menu it is in destroys it too.
        if (item.Items is { } items)
        {
            var submenu = Native.CreatePopupMenu();
            if (submenu == 0) return;

            for (var i = 0; i < items.Count; i++) Insert(submenu, (uint)i, items[i]);
            (flags, id) = (flags | Native.MF_POPUP, (nuint)submenu);
        }

        Native.InsertMenuW(menu, position, flags, id, item.Label);
    }

    /// <summary>Binds the shell object for the selection and asks every registered extension to fill a menu.
    /// This is the call that can hang, so it only ever runs on the STA thread.</summary>
    internal static (Native.IContextMenu? Menu, nint Popup) Build(IReadOnlyList<string> paths, uint flags, IReadOnlyList<OwnMenuItem>? own = null)
    {
        var pidls = new List<nint>(paths.Count);
        try
        {
            // One path the shell cannot parse must not cost the menu for everything else selected.
            foreach (var path in paths)
                if (Native.SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 && pidl != 0)
                    pidls.Add(pidl);

            if (pidls.Count == 0) return default;

            var menu = Bind(pidls.ToArray());
            if (menu is null) return default;

            var popup = Native.CreatePopupMenu();
            if (popup != 0 && menu.QueryContextMenu(popup, 0, FirstCommand, LastCommand, flags) >= 0)
            {
                // Ours go in after the shell has filled the menu, so the shell's numbering is untouched.
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
