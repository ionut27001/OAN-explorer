using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oan.Core.Shell;

/// <summary>Outcome of a shell menu. <see cref="Refusal"/>: Explorer's menu never appeared, open the app's own.
/// <see cref="Failure"/>: the menu appeared and the chosen verb refused.</summary>
public readonly record struct ShellMenuResult(bool Ran, string? Refusal, string? Failure = null, int OwnCommand = 0);

/// <summary>An entry OAN puts at the top of the shell's menu. Ids stay below <c>MenuBuilder.FirstCommand</c>.
/// One with <see cref="Items"/> opens a submenu; one with no label is a separator.</summary>
public readonly record struct OwnMenuItem(int Id, string Label, bool Checked = false, bool Enabled = true,
    IReadOnlyList<OwnMenuItem>? Items = null)
{
    public static OwnMenuItem Separator => new(0, "");
}

/// <summary>One item the menu is shown for. IsDirectory comes from the listing: <c>Directory.Exists</c>
/// on a dropped share blocks the UI thread for SMB timeouts.</summary>
public readonly record struct ShellItem(string Path, bool IsDirectory);

/// <summary>Explorer's context menu with the user's shell extensions, on a deadline: they load in-process and can hang.
/// Called from the window's thread only.</summary>
public static class ShellContextMenu
{

    /// <summary>How long an extension gets; the window does not answer meanwhile. Paid once per kind of file.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>Deadline for owner-draw callbacks while the menu is open. Missing it loses the picture, never the thread.</summary>
    internal static readonly TimeSpan DrawDeadline = TimeSpan.FromMilliseconds(250);

    /// <summary>How long letting go of the held menus may hold up closing the window.</summary>
    private static readonly TimeSpan CloseDeadline = TimeSpan.FromMilliseconds(250);

    /// <summary>A hang is remembered against a kind only for a selection this small: parsing costs ~1.5 ms a path, so a
    /// thousand files run out of time on their number alone.</summary>
    internal const int FewItems = 16;

    /// <summary>Kinds of file whose menu hung once in this run; they go straight to the app's own menu.</summary>
    private static readonly ConcurrentDictionary<string, bool> Stuck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fewest items, more than <see cref="FewItems"/>, whose menu ran out of time in this run: a selection this
    /// large goes straight to the app's own menu, whatever is in it.</summary>
    private static int _tooMany = int.MaxValue;

    /// <summary>Why OAN's own menu is opening instead of Explorer's.</summary>
    internal enum Why { Hung, Empty, Lost, NoWindow, TooMany }

    /// <summary>Shows the menu for <paramref name="items"/> at a screen point and runs the chosen command.</summary>
    public static ShellMenuResult Show(nint owner, IReadOnlyList<ShellItem> items, int screenX, int screenY,
        IReadOnlyList<OwnMenuItem>? own = null)
    {
        var (key, build) = ForItems(items, own);
        return Show(owner, items, screenX, screenY, build, background: false, key);
    }

    /// <summary>The menu Explorer shows on empty space in a folder.</summary>
    public static ShellMenuResult ShowForFolder(nint owner, string folder, int screenX, int screenY,
        IReadOnlyList<OwnMenuItem>? own = null)
    {
        var (key, build) = ForFolder(owner, folder, own);
        return Show(owner, AsFolder(folder), screenX, screenY, build, background: true, key);
    }

    /// <summary>Starts building the menu at right-button-down, so the build overlaps the press; the menu opens at button up,
    /// and <see cref="Show"/> takes this build when it is for the same items. Skipped while the thread is busy.</summary>
    public static void Prepare(nint owner, IReadOnlyList<ShellItem> items, IReadOnlyList<OwnMenuItem>? own = null)
    {
        var (key, build) = ForItems(items, own);
        Prepare(owner, items, build, background: false, key);
    }

    /// <summary>The same for the menu on empty space in a folder.</summary>
    public static void PrepareForFolder(nint owner, string folder, IReadOnlyList<OwnMenuItem>? own = null)
    {
        var (key, build) = ForFolder(owner, folder, own);
        Prepare(owner, AsFolder(folder), build, background: true, key);
    }

    /// <summary>Once per run, after the first listing is on screen and before any menu: builds the folder's menu and the first
    /// row's and holds them like real ones, so the first right-click finds the shell's DLLs loaded and the surrogates running.
    /// Never waited for.</summary>
    public static void WarmUp(nint owner, string folder, ShellItem first)
    {
        if (owner == 0) return;

        MenuThread.WarmUp(() => MenuBuilder.BuildForFolder(owner, folder, Native.CMF_EXPLORE, null),
            () => MenuBuilder.Build([first.Path], Native.CMF_EXPLORE));
    }

    /// <summary>Lets go of the held menus as the window closes, so the surrogates exit with OAN instead of waiting for COM
    /// to notice it has gone.</summary>
    public static void ReleaseHeld() => MenuThread.ReleaseHeld(CloseDeadline);

    /// <summary>The same, with the part that loads third-party code handed in, so it can be faked. <paramref name="key"/> says
    /// which menu this is, for a build prepared at button-down; <paramref name="background"/> which slot holds it afterwards.</summary>
    internal static ShellMenuResult Show(nint owner, IReadOnlyList<ShellItem> items, int screenX, int screenY,
        Func<(Native.IContextMenu? Menu, nint Popup)> build, bool background = false, string? key = null)
    {
        if (items.Count == 0) return default;

        var kinds = Kinds(items);
        if (owner == 0) return new(false, Refusal(kinds, Why.NoWindow));
        if (kinds.Any(Stuck.ContainsKey)) return new(false, Refusal(kinds));
        if (items.Count >= _tooMany) return new(false, Refusal(kinds, Why.TooMany));

        var (apartment, built, menu, popup) = MenuThread.Build(build, background, key);
        var sta = apartment.Thread;

        try
        {
            if (!built || menu is null || popup == 0)
            {
                if (!sta.Lost) return new(false, Refusal(kinds, Why.Empty));

                // Only a few items of one kind say which handler hung; many may just be too many to parse in time.
                if (items.Count > FewItems) _tooMany = Math.Min(_tooMany, items.Count);
                else if (kinds.Count == 1) Stuck[kinds[0]] = true;

                return new(false, Refusal(kinds, items.Count > FewItems ? Why.TooMany : Why.Hung));
            }

            var command = Track(owner, sta, menu, popup, screenX, screenY);
            // Nothing was picked: Escape, or a click beside the menu. Nothing to say about that.
            if (command <= 0) return default;

            // One of ours; no shell extension was involved.
            if (command < MenuBuilder.FirstCommand) return new(false, null, null, command);

            // The apartment died while the menu was up: say so rather than do nothing.
            if (sta.Lost) return new(false, Refusal(kinds, Why.Lost));

            return Chosen(sta, menu, owner, (uint)command - MenuBuilder.FirstCommand, screenX, screenY);
        }
        finally
        {
            // The HMENU goes and the IContextMenu is held; a lost thread keeps both.
            sta.Run(() => apartment.Keep(background, menu, popup), Deadline);
        }
    }

    internal static void Prepare(nint owner, IReadOnlyList<ShellItem> items,
        Func<(Native.IContextMenu? Menu, nint Popup)> build, bool background, string key)
    {
        if (owner != 0 && items.Count > 0 && items.Count < _tooMany && !Kinds(items).Any(Stuck.ContainsKey))
            MenuThread.Prepare(build, background, key);
    }

    private static (string Key, Func<(Native.IContextMenu? Menu, nint Popup)> Build) ForItems(
        IReadOnlyList<ShellItem> items, IReadOnlyList<OwnMenuItem>? own)
    {
        var flags = Flags();
        var paths = items.Select(item => item.Path).ToArray();
        return (Key(flags, own, paths), () => MenuBuilder.Build(paths, flags, own));
    }

    private static (string Key, Func<(Native.IContextMenu? Menu, nint Popup)> Build) ForFolder(
        nint owner, string folder, IReadOnlyList<OwnMenuItem>? own)
    {
        var flags = Flags();
        return (Key(flags, own, [folder]), () => MenuBuilder.BuildForFolder(owner, folder, flags, own));
    }

    /// <summary>Named as a folder, not by extension: what can hang here is the folder's view handler.</summary>
    private static ShellItem[] AsFolder(string folder) => [new ShellItem(folder, IsDirectory: true)];

    /// <summary>CMF_CANRENAME stays off: it promises we carry out the rename, which needs a shell view to call back into.
    /// Shift is read here, not on the STA thread: key state belongs to the input queue of the asking thread.</summary>
    private static uint Flags() =>
        (Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0 ? Native.CMF_EXPLORE | Native.CMF_EXTENDEDVERBS : Native.CMF_EXPLORE;

    internal static string Key(uint flags, IReadOnlyList<OwnMenuItem>? own, IEnumerable<string> paths) =>
        string.Join('\n', [flags.ToString(), .. Lines(own), .. paths]);

    /// <summary>A submenu by what is in it: the record prints its list as a type name, and a moved tick would still match.</summary>
    private static IEnumerable<string> Lines(IReadOnlyList<OwnMenuItem>? own) =>
        (own ?? []).SelectMany(item => Lines(item.Items).Prepend((item with { Items = null }).ToString()));

    private static ShellMenuResult Chosen(StaThread sta, Native.IContextMenu menu, nint owner, uint offset,
        int screenX, int screenY)
    {
        var answer = 0;
        var replied = sta.Run(() => answer = Invoke(menu, owner, offset, screenX, screenY), Deadline);

        // A verb that opens its own window (Properties, a confirmation) does not return until it closes,
        // which looks like a hang from here. Refresh anyway and do not remember it as stuck.
        if (!replied || answer >= 0) return new(true, null);

        // A cancel by the user is not worth a message; any other refusal is.
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
                Native.TPM_LEFTALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NOANIMATION,
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

        var answer = menu.InvokeCommand(invoke);

        // Copy and Cut leave the clipboard to be rendered by this thread, which outlives the menu: rendered now, no paste waits
        // on it and the copy survives OAN. A no-op when the clipboard is not this thread's.
        Native.OleFlushClipboard();
        return answer;
    }

    private static string Explain(int hresult) =>
        Marshal.GetExceptionForHR(hresult)?.Message ?? $"0x{hresult:X8}";

    /// <summary>The drawing callbacks; null when no extension took the message.</summary>
    internal static nint? Draw(Native.IContextMenu menu, uint msg, nint wParam, nint lParam)
    {
        if (menu is Native.IContextMenu3 three && three.HandleMenuMsg2(msg, wParam, lParam, out var result) >= 0)
            return result;

        if (menu is Native.IContextMenu2 two && two.HandleMenuMsg(msg, wParam, lParam) >= 0)
            return 1;

        return null;
    }

    /// <summary>WM_MEASUREITEM and WM_DRAWITEM, on a copy of the struct: USER32 owns lParam only until the window answers,
    /// and an extension past the deadline still writes into what it was given. Its HDC is stale by then too, but that is
    /// a handle GDI checks, not memory.</summary>
    internal static nint? DrawItem(StaThread sta, Native.IContextMenu menu, uint msg, nint wParam, nint lParam)
    {
        if (sta.Lost) return null;

        var size = msg == Native.WM_DRAWITEM ? Marshal.SizeOf<Native.DRAWITEMSTRUCT>() : Marshal.SizeOf<Native.MEASUREITEMSTRUCT>();
        var copy = Marshal.AllocHGlobal(size);
        Copy(lParam, copy, size);

        // Held by this call and by the job; the last to let go frees it. A job never posted, because collecting an earlier
        // one lost the thread, never lets go: those bytes stay with the thread, like everything it built.
        var holders = 2;
        nint? handled = null;
        var answered = sta.Run(() =>
        {
            try
            {
                handled = Draw(menu, msg, wParam, copy);
            }
            finally
            {
                LetGo();
            }
        }, DrawDeadline, abandon: false);

        if (answered) Copy(copy, lParam, size);
        LetGo();
        return answered ? handled : null;

        void LetGo()
        {
            if (Interlocked.Decrement(ref holders) == 0) Marshal.FreeHGlobal(copy);
        }
    }

    private static unsafe void Copy(nint from, nint to, int size) => Buffer.MemoryCopy((void*)from, (void*)to, size, size);

    /// <summary>Must run on the STA thread that created these.</summary>
    internal static void Release(Native.IContextMenu? menu, nint popup)
    {
        if (popup != 0) Native.DestroyMenu(popup);
        if (menu is not null) Marshal.ReleaseComObject(menu);
    }

    /// <summary>What a hang is remembered against: the extension, with one bucket each for folders and extensionless files.
    /// IsDirectory decides, not the name: folders called "v1.2" or "archive.iso" would otherwise poison real file kinds.</summary>
    internal static List<string> Kinds(IReadOnlyList<ShellItem> items) =>
        items
            .Select(item => item.IsDirectory ? "folders"
                : Path.GetExtension(item.Path) is { Length: > 1 } extension ? extension.ToLowerInvariant()
                : "files with no extension")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The sentence shown to the user; only a real hang names the program that owns the type.</summary>
    internal static string Refusal(List<string> kinds, Why why = Why.Hung)
    {
        if (why == Why.NoWindow) return "There is no window to hang Explorer's menu on — showing OAN's own menu.";

        var what = string.Join(", ", kinds);
        var who = why == Why.Hung && kinds.Count == 1 ? Owner(kinds[0]) : null;

        return why switch
        {
            Why.Empty => $"Windows has no menu of its own for {what} — showing OAN's own menu.",
            Why.Lost => $"The shell menu for {what} stopped answering before it could run that — showing OAN's own menu.",
            Why.TooMany => "The shell menu for this many items takes too long — showing OAN's own menu.",
            _ => who is null
                ? $"The shell menu for {what} is not answering — showing OAN's own menu."
                : $"{who} is not answering for {what} — showing OAN's own menu.",
        };
    }

    /// <summary>Who the registry says handles this kind of file. Named keys only, never the wildcard key.</summary>
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

        // The type's friendly name; when blank (DAEMON Tools leaves it so), the ProgID.
        using var described = type is null ? null : Registry.ClassesRoot.OpenSubKey(type);
        return described?.GetValue(null) is string { Length: > 0 } name ? name : type;
    }

    /// <summary>Forwards menu messages to the shell while the menu is up; without it extension submenus
    /// (Send To, Open With, cloud providers) come back empty.</summary>
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
            // Called by USER32 across a native frame: an escaping exception would take the process down.
            try
            {
                // abandon: false — a late submenu must not cost the thread the menu object lives on. WM_INITMENUPOPUP
                // fills a submenu rather than paints it, and gets the deadline a build does.
                nint? handled = null;
                if (msg == Native.WM_INITMENUPOPUP &&
                    _sta.Run(() => handled = Draw(_menu, msg, wParam, lParam), Deadline, abandon: false) &&
                    handled is not null)
                    return 0;

                if ((msg is Native.WM_DRAWITEM or Native.WM_MEASUREITEM) &&
                    DrawItem(_sta, _menu, msg, wParam, lParam) is { } result)
                    return result;
            }
            catch (Exception)
            {
                // Fall through: the menu loses whatever the extension was drawing, nothing more.
            }

            return Native.CallWindowProc(_previous, window, msg, wParam, lParam);
        }
    }
}
