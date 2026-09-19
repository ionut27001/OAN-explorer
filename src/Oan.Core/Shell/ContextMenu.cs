using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oan.Core.Shell;

/// <summary>
/// What came of handing a right-click to the shell. <see cref="Ran"/> means a command was started
/// and the listing should be refreshed. <see cref="Refusal"/> is the sentence to show when
/// Explorer's menu never appeared, and the signal to open the app's own menu instead.
/// <see cref="Failure"/> is the other way round: the menu was there, the user picked from it, and
/// the verb itself said no — there is something to say, but nothing left to offer.
/// </summary>
public readonly record struct ShellMenuResult(bool Ran, string? Refusal, string? Failure = null, int OwnCommand = 0);

/// <summary>
/// An entry OAN puts at the top of the shell's menu, above everything the system offers.
///
/// Mount belongs here rather than in a menu of our own: the user right-clicks once and expects
/// one list. Ids are ours and stay below <c>MenuBuilder.FirstCommand</c>, so what comes back says plainly
/// whether the shell should run it or we should.
/// </summary>
public readonly record struct OwnMenuItem(int Id, string Label);

/// <summary>
/// One item the menu is being shown for. Whether it is a folder comes from the listing the rows
/// were built from and not from the disk: <c>Directory.Exists</c> on a dropped network share
/// answers in SMB timeouts, and the question would be asked on the window's own thread — the one
/// thread this class exists to keep free.
/// </summary>
public readonly record struct ShellItem(string Path, bool IsDirectory);

/// <summary>
/// Explorer's own context menu, with every shell extension the user already has — on a deadline,
/// because those extensions are loaded into our process and one of them can hang for hours.
/// </summary>
public static class ShellContextMenu
{

    /// <summary>
    /// How long an extension gets. Not a performance budget — a menu slower than this is broken,
    /// not busy — and the window is not answering while we wait, which is why it is not longer.
    /// It is paid at most once per kind of file, because a timeout is remembered.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The same, for an extension drawing its own item while the menu is already open. The user is
    /// hovering: anything this slow loses its picture rather than hold the menu still. It loses
    /// only the picture — the thread is not given up on for missing this one, or a menu that was
    /// merely slow to paint would be left on screen unable to run anything clicked on it.
    /// </summary>
    private static readonly TimeSpan DrawDeadline = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Kinds of file whose menu has hung once in this run; the next right-click on one goes
    /// straight to the app's own menu instead of waiting again. Per run on purpose: an extension
    /// is usually stuck because of the state the machine is in, and starting OAN again is the
    /// natural moment to find out whether it still is.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> Stuck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why OAN's own menu is opening instead of Explorer's. One sentence for all four
    /// would accuse a program that answered in a millisecond of hanging.</summary>
    internal enum Why { Hung, Empty, Lost, NoWindow }

    /// <summary>
    /// Shows the menu for <paramref name="items"/> at a screen point and runs the chosen command.
    /// </summary>
    public static ShellMenuResult Show(nint owner, IReadOnlyList<ShellItem> items, int screenX, int screenY,
        IReadOnlyList<OwnMenuItem>? own = null)
    {
        // CMF_CANRENAME is a promise to carry out the rename ourselves, which needs a shell view
        // to call back into. Without one the verb would be enabled and do nothing, so the flag
        // stays off and the shell disables it. The app renames on F2.
        // Shift is read here and not on the STA thread below: key state belongs to the input
        // queue of the thread that asks, and over there it would come back empty.
        var flags = Native.CMF_EXPLORE;
        if ((Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0) flags |= Native.CMF_EXTENDEDVERBS;

        var paths = items.Select(item => item.Path).ToArray();
        return Show(owner, items, screenX, screenY, () => MenuBuilder.Build(paths, flags, own), own);
    }

    /// <summary>
    /// The menu Explorer shows on empty space in a folder: New, Paste, Properties, and whatever
    /// extensions the user has. Same system as the menu for a selection, on purpose — two menus
    /// drawn by two different owners look like two different programs, and the app's own is here
    /// only for when the shell will not answer.
    /// </summary>
    public static ShellMenuResult ShowForFolder(nint owner, string folder, int screenX, int screenY,
        IReadOnlyList<OwnMenuItem>? own = null)
    {
        var flags = Native.CMF_EXPLORE;
        if ((Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0) flags |= Native.CMF_EXTENDEDVERBS;

        // Named as a folder rather than by extension: what can hang here is the folder's own view
        // handler, and blaming ".iso" for it would silence the wrong thing next time.
        var items = new[] { new ShellItem(folder, IsDirectory: true) };
        return Show(owner, items, screenX, screenY, () => MenuBuilder.BuildForFolder(owner, folder, flags, own), own);
    }

    /// <summary>The same, with the part that loads third-party code handed in, so it can be faked.</summary>
    internal static ShellMenuResult Show(nint owner, IReadOnlyList<ShellItem> items, int screenX, int screenY,
        Func<(Native.IContextMenu? Menu, nint Popup)> build, IReadOnlyList<OwnMenuItem>? own = null)
    {
        if (items.Count == 0) return default;

        var kinds = Kinds(items);
        // Without a window there is nothing to hang a menu from — and this is the one way out of
        // here that could otherwise leave the user with no menu at all and nothing said.
        if (owner == 0) return new(false, Refusal(kinds, Why.NoWindow));
        if (kinds.Any(Stuck.ContainsKey)) return new(false, Refusal(kinds));

        using var sta = new StaThread($"OAN shell menu {string.Join(' ', kinds)}");
        Native.IContextMenu? menu = null;
        nint popup = 0;

        try
        {
            if (!sta.Run(() => (menu, popup) = build(), Deadline) || menu is null || popup == 0)
            {
                // Only a selection of one kind says anything about which handler hung. Blaming
                // every kind in a mixed one would cost the user Explorer's menu on types that
                // were never the problem.
                if (sta.Lost && kinds.Count == 1) Stuck[kinds[0]] = true;
                return new(false, Refusal(kinds, sta.Lost ? Why.Hung : Why.Empty));
            }

            var command = Track(owner, sta, menu, popup, screenX, screenY);
            // Nothing was picked: Escape, or a click beside the menu. Nothing to say about that.
            if (command <= 0) return default;

            // One of ours. It never went near a shell extension, so a lost apartment does not
            // apply to it.
            if (command < MenuBuilder.FirstCommand) return new(false, null, null, command);

            // Something was picked, off a menu that was on screen and working, and the apartment
            // it lived in is gone. Answering that with silence is the click that does nothing.
            if (sta.Lost) return new(false, Refusal(kinds, Why.Lost));

            return Chosen(sta, menu, owner, (uint)command - MenuBuilder.FirstCommand, screenX, screenY);
        }
        finally
        {
            sta.Run(() => Release(menu, popup), Deadline);
        }
    }

    /// <summary>The verb the user picked, and what to make of the answer it gave.</summary>
    private static ShellMenuResult Chosen(StaThread sta, Native.IContextMenu menu, nint owner, uint offset,
        int screenX, int screenY)
    {
        var answer = 0;
        var replied = sta.Run(() => answer = Invoke(menu, owner, offset, screenX, screenY), Deadline);

        // A verb that puts up a window of its own — Properties, a delete confirmation, an
        // archiver — does not return until the user has dealt with it, which from here is
        // indistinguishable from one that has hung. So the wait ends, the listing is
        // refreshed anyway, and this is not remembered: the menu was never the problem.
        if (!replied || answer >= 0) return new(true, null);

        // It answered, and the answer was no. When that is the user's own doing — a dialog they
        // closed, an elevation they declined — saying so would be nagging. Anything else is a
        // reason they have not been given, and a command that evaporates reads as a broken app.
        return answer == Native.HRESULT_CANCELLED
            ? default
            : new(false, null, $"That command did not run — {Explain(answer)}");
    }

    /// <summary>The menu's own modal loop, here on the UI thread, and what the user picked.</summary>
    private static int Track(nint owner, StaThread sta, Native.IContextMenu menu, nint popup, int screenX, int screenY)
    {
        using (new MenuMessages(owner, sta, menu))
        {
            return Native.TrackPopupMenuEx(
                popup,
                Native.TPM_LEFTALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                screenX, screenY, owner, 0);
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

    /// <summary>A refusal in words: "Access is denied" is something the user can act on and
    /// 0x80070005 is not. The number is kept for the ones the system has no sentence for.</summary>
    private static string Explain(int hresult) =>
        Marshal.GetExceptionForHR(hresult)?.Message ?? $"0x{hresult:X8}";

    /// <summary>
    /// The drawing callbacks, on the thread that owns the object. Null when no extension took the
    /// message; otherwise the number USER32 gets back.
    /// </summary>
    private static nint? Draw(Native.IContextMenu menu, uint msg, nint wParam, nint lParam)
    {
        if (menu is Native.IContextMenu3 three && three.HandleMenuMsg2(msg, wParam, lParam, out var result) >= 0)
            return result;

        if (menu is Native.IContextMenu2 two && two.HandleMenuMsg(msg, wParam, lParam) >= 0)
            return 1;

        return null;
    }

    /// <summary>
    /// Runs on the STA thread that created these, because that is the apartment they live in —
    /// and not at all when that thread is one we gave up on.
    /// </summary>
    internal static void Release(Native.IContextMenu? menu, nint popup)
    {
        if (popup != 0) Native.DestroyMenu(popup);
        if (menu is not null) Marshal.ReleaseComObject(menu);
    }

    /// <summary>
    /// What a hang is remembered against: the extension, because that is what a handler registers
    /// itself for. Folders and extensionless files each share one bucket — a handler that hangs on
    /// one of those hangs on all of them. Doubles as the words for the message.
    ///
    /// A folder is a folder whatever its name looks like. Read off the string instead, "v1.2" and
    /// ".git" land under ".2" and ".git" — kinds no handler is registered for, so the table would
    /// never protect the case it was built for, while a real .iso file would lose Explorer's menu
    /// because a folder called archive.iso once hung.
    /// </summary>
    internal static List<string> Kinds(IReadOnlyList<ShellItem> items) =>
        items
            .Select(item => item.IsDirectory ? "folders"
                : Path.GetExtension(item.Path) is { Length: > 1 } extension ? extension.ToLowerInvariant()
                : "files with no extension")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What to tell the user. Naming the program that owns the type is the whole difference
    /// between a message they can act on and "something is stuck" — but only when something is
    /// stuck: a shell that answered at once with no menu to give has not hung, and a sentence
    /// saying it has sends the user off to uninstall a program that did nothing wrong.
    /// </summary>
    internal static string Refusal(List<string> kinds, Why why = Why.Hung)
    {
        if (why == Why.NoWindow) return "There is no window to hang Explorer's menu on — showing OAN's own menu.";

        var what = string.Join(", ", kinds);
        var who = why == Why.Hung && kinds.Count == 1 ? Owner(kinds[0]) : null;

        return why switch
        {
            Why.Empty => $"Windows has no menu of its own for {what} — showing OAN's own menu.",
            Why.Lost => $"The shell menu for {what} stopped answering before it could run that — showing OAN's own menu.",
            _ => who is null
                ? $"The shell menu for {what} is not answering — showing OAN's own menu."
                : $"{who} is not answering for {what} — showing OAN's own menu.",
        };
    }

    /// <summary>
    /// Who the registry says handles this kind of file. Menu handlers are named under the
    /// extension and under the type it points at; failing both, the type's own name still beats
    /// nothing. Named keys only, never a wildcard — that one walks the whole hive.
    /// </summary>
    internal static string? Owner(string kind)
    {
        if (!kind.StartsWith('.')) return null;

        using var extension = Registry.ClassesRoot.OpenSubKey(kind);
        var type = extension?.GetValue(null) is string { Length: > 0 } progId ? progId : null;

        using var handlers = Registry.ClassesRoot.OpenSubKey($@"{kind}\ShellEx\ContextMenuHandlers")
                             ?? (type is null ? null : Registry.ClassesRoot.OpenSubKey($@"{type}\ShellEx\ContextMenuHandlers"));

        // A handler registered under its CLSID instead of a name says nothing to a user.
        var named = handlers?.GetSubKeyNames().Where(name => !Guid.TryParse(name, out _)).ToArray();
        if (named is { Length: > 0 }) return string.Join(", ", named);

        // The friendly name of the type, and when that is blank — which is how DAEMON Tools
        // leaves it — the type's own id, which still names the program.
        using var described = type is null ? null : Registry.ClassesRoot.OpenSubKey(type);
        return described?.GetValue(null) is string { Length: > 0 } name ? name : type;
    }

    /// <summary>
    /// Hands the menu messages to the shell while the menu is up. Without this the submenus owned
    /// by extensions — Send To, Open With, cloud providers — come back empty.
    /// </summary>
    private sealed class MenuMessages : IDisposable
    {
        private readonly nint _window;
        private readonly nint _previous;
        private readonly Native.WindowProc _proc;
        private readonly StaThread _sta;
        private readonly Native.IContextMenu _menu;

        public MenuMessages(nint window, StaThread sta, Native.IContextMenu menu)
        {
            _window = window;
            _sta = sta;
            _menu = menu;
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
                nint? handled = null;

                // abandon: false — a picture that came too late costs that picture and no more.
                // The menu's own object lives on this thread; giving it up over a slow drawing
                // would leave a menu on screen that cannot run anything the user then clicks.
                if ((msg is Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM) &&
                    _sta.Run(() => handled = Draw(_menu, msg, wParam, lParam), DrawDeadline, abandon: false) &&
                    handled is { } result)
                    return msg == Native.WM_INITMENUPOPUP ? 0 : result;
            }
            catch (Exception)
            {
                // Fall through: the menu loses whatever the extension was drawing, nothing more.
            }

            return Native.CallWindowProc(_previous, window, msg, wParam, lParam);
        }
    }
}
