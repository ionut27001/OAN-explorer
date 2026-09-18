using System.Runtime.InteropServices;

namespace Oan.Core.Shell;

/// <summary>Explorer's own context menu, with every shell extension the user already has.</summary>
public static class ShellContextMenu
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    /// <summary>
    /// Shows the menu for <paramref name="paths"/> at a screen point and runs the chosen command.
    /// Returns true only when a command ran, so the caller can refresh. False covers the three
    /// ways nothing happened: the user dismissed the menu, the shell would not build it, or the
    /// command itself failed.
    /// </summary>
    public static bool Show(nint owner, IReadOnlyList<string> paths, int screenX, int screenY)
    {
        if (owner == 0 || paths.Count == 0) return false;

        var pidls = new List<nint>(paths.Count);
        try
        {
            // One path the shell cannot parse — a deleted item still in the list, a dropped
            // share — must not cost the user the menu for everything else selected.
            foreach (var path in paths)
                if (Native.SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 && pidl != 0)
                    pidls.Add(pidl);

            if (pidls.Count == 0) return false;

            var menu = Bind(pidls.ToArray());
            if (menu is null) return false;

            try
            {
                return Track(owner, menu, screenX, screenY);
            }
            finally
            {
                Marshal.ReleaseComObject(menu);
            }
        }
        catch (Exception)
        {
            // Third-party shell extensions build this menu inside our process. One that faults
            // costs the user a menu; it must not cost them the window.
            return false;
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

    private static bool Track(nint owner, Native.IContextMenu menu, int screenX, int screenY)
    {
        var popup = Native.CreatePopupMenu();
        if (popup == 0) return false;

        try
        {
            // CMF_CANRENAME is a promise to carry out the rename ourselves, which needs a shell
            // view to call back into. Without one the verb would be enabled and do nothing, so
            // the flag stays off and the shell disables it. The app renames on F2.
            var flags = Native.CMF_EXPLORE;
            if ((Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0) flags |= Native.CMF_EXTENDEDVERBS;

            if (menu.QueryContextMenu(popup, 0, FirstCommand, LastCommand, flags) < 0) return false;

            int command;
            using (new MenuMessages(owner, menu))
            {
                command = Native.TrackPopupMenuEx(
                    popup,
                    Native.TPM_LEFTALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                    screenX, screenY, owner, 0);
            }

            if (command <= 0) return false;

            // A denied delete, a cancelled elevation and an extension returning E_FAIL all land
            // here. Reporting them as success would refresh the list and show the unchanged
            // state as though the command had worked.
            return Invoke(menu, owner, (uint)command - FirstCommand, screenX, screenY) >= 0;
        }
        finally
        {
            Native.DestroyMenu(popup);
        }
    }

    private static int Invoke(Native.IContextMenu menu, nint owner, uint offset, int screenX, int screenY)
    {
        // The command is identified by its offset packed into the verb pointer (MAKEINTRESOURCE),
        // not by a verb string; both the ANSI and Unicode fields must carry it.
        var verb = (nint)offset;

        var invoke = new Native.CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
            fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE,
            hwnd = owner,
            lpVerb = verb,
            lpVerbW = verb,
            nShow = Native.SW_SHOWNORMAL,
            ptInvoke = new Native.POINT { X = screenX, Y = screenY },
        };

        return menu.InvokeCommand(invoke);
    }

    /// <summary>
    /// Hands the menu messages to the shell while the menu is up. Without this the submenus
    /// owned by extensions — Send To, Open With, cloud providers — come back empty.
    /// </summary>
    private sealed class MenuMessages : IDisposable
    {
        private readonly nint _window;
        private readonly nint _previous;
        private readonly Native.WindowProc _proc;
        private readonly Native.IContextMenu2? _menu2;
        private readonly Native.IContextMenu3? _menu3;

        public MenuMessages(nint window, Native.IContextMenu menu)
        {
            _window = window;
            _menu2 = menu as Native.IContextMenu2;
            _menu3 = menu as Native.IContextMenu3;
            _proc = Forward;
            _previous = Native.SetWindowLongPtr(window, Native.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_proc));
        }

        public void Dispose()
        {
            // A failed swap left the window untouched; writing 0 back would destroy its proc.
            if (_previous != 0) Native.SetWindowLongPtr(_window, Native.GWLP_WNDPROC, _previous);
            GC.KeepAlive(_proc);
        }

        private nint Forward(nint window, uint msg, nint wParam, nint lParam)
        {
            // USER32 calls this from inside the menu's modal loop, across a native frame. An
            // exception that escaped would not unwind to a managed handler — it would take the
            // process down while the menu is open.
            try
            {
                if (msg is Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM)
                {
                    if (_menu3 is not null && _menu3.HandleMenuMsg2(msg, wParam, lParam, out var result) >= 0)
                        return msg == Native.WM_INITMENUPOPUP ? 0 : result;

                    if (_menu2 is not null && _menu2.HandleMenuMsg(msg, wParam, lParam) >= 0)
                        return msg == Native.WM_INITMENUPOPUP ? 0 : 1;
                }
            }
            catch (Exception)
            {
                // Fall through: the menu loses whatever the extension was drawing, nothing more.
            }

            return Native.CallWindowProc(_previous, window, msg, wParam, lParam);
        }
    }
}
