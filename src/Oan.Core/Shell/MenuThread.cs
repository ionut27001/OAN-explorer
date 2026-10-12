using System.Diagnostics;

namespace Oan.Core.Shell;

/// <summary>The one STA thread shell menus are built on, and the last menu of each slot it built: the selection's and the
/// folder's. Held because the surrogates (dllhost) behind some verbs exit seconds after their last reference goes, and
/// starting them again is most of what a menu costs. Called from the window's thread only.</summary>
internal static class MenuThread
{
    private static Apartment? _apartment;
    private static Prepared? _prepared;

    /// <summary>Threads given up on, kept reachable: otherwise the finalizer releases what they hold, and waits on a stuck
    /// one to do it.</summary>
    private static readonly List<Apartment> Abandoned = [];

    /// <summary>The build prepared at button-down when it is for this menu, given what is left of the deadline it started
    /// with; otherwise a new one. Built false with the thread lost means this menu's own build did not finish in time.</summary>
    internal static (Apartment Apartment, bool Built, Native.IContextMenu? Menu, nint Popup) Build(
        Func<(Native.IContextMenu? Menu, nint Popup)> build, bool background, string? key)
    {
        var apartment = Current();
        var prepared = _prepared;
        _prepared = null;

        if (prepared is not null && prepared.Background == background && prepared.Key == key)
        {
            var left = ShellContextMenu.Deadline - Stopwatch.GetElapsedTime(prepared.Started);
            return (apartment, apartment.Thread.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero), prepared.Menu, prepared.Popup);
        }

        // What was posted before (the warm-up, a build nobody opened) gets the deadline. If it never ends, that costs the
        // thread, not this kind: the menu is built on a fresh one.
        if (!apartment.Thread.Wait(ShellContextMenu.Deadline))
        {
            apartment = Current();
            prepared = null;
        }

        (Native.IContextMenu? Menu, nint Popup) built = default;
        var ran = apartment.Thread.Run(() =>
        {
            apartment.Keep(prepared);
            built = build();
        }, ShellContextMenu.Deadline);

        return (apartment, ran, built.Menu, built.Popup);
    }

    /// <summary>Starts a build for <see cref="Build"/> to take, without waiting for it. Skipped while the thread is busy.</summary>
    internal static void Prepare(Func<(Native.IContextMenu? Menu, nint Popup)> build, bool background, string key)
    {
        var apartment = Current();
        var unused = _prepared;
        var prepared = new Prepared(background, key);

        if (apartment.Thread.Post(() =>
            {
                apartment.Keep(unused);
                (prepared.Menu, prepared.Popup) = build();
            }))
            _prepared = prepared;
    }

    /// <summary>Builds a folder's menu and a row's and holds them like real ones, unless a menu came first. Never waited for.</summary>
    internal static void WarmUp(Func<(Native.IContextMenu? Menu, nint Popup)> folder,
        Func<(Native.IContextMenu? Menu, nint Popup)> row)
    {
        // A menu that came first loaded as much, and may still be open: the dispatcher runs this inside its modal loop,
        // where the menu's submenus and its pick would queue behind the warm-up.
        if (_apartment is not null) return;

        var apartment = Current();
        apartment.Thread.Post(() =>
        {
            var (menu, popup) = folder();
            // 'New' does ~300 ms of first-time work as it opens; here, the user is not waiting for it.
            if (menu is not null && popup != 0) Populate(menu, popup);
            apartment.Keep(background: true, menu, popup);

            (menu, popup) = row();
            apartment.Keep(background: false, menu, popup);
        });
    }

    internal static void ReleaseHeld(TimeSpan deadline)
    {
        if (_apartment is not { Thread.Lost: false } apartment) return;

        var unused = _prepared;
        _prepared = null;
        apartment.Thread.Run(() =>
        {
            apartment.Keep(unused);
            apartment.ReleaseAll();
        }, deadline);
    }

    /// <summary>The thread, replaced when lost. Dispose tells a lost one to exit if its extension ever returns.</summary>
    private static Apartment Current()
    {
        if (_apartment is { Thread.Lost: false }) return _apartment;

        if (_apartment is { } lost)
        {
            lost.Thread.Dispose();
            Abandoned.Add(lost);
        }

        _prepared = null;
        return _apartment = new Apartment();
    }

    /// <summary>WM_INITMENUPOPUP to each submenu, as opening it would send.</summary>
    private static void Populate(Native.IContextMenu menu, nint popup)
    {
        for (var i = 0; i < Native.GetMenuItemCount(popup); i++)
            if (Native.GetSubMenu(popup, i) is var submenu and not 0)
                ShellContextMenu.Draw(menu, Native.WM_INITMENUPOPUP, submenu, i);
    }

    /// <summary>A thread and the menus it holds. Only its own jobs touch them, and a lost thread keeps what it built:
    /// releasing that from another thread is a use-after-free once the extension returns.</summary>
    internal sealed class Apartment
    {
        private readonly Native.IContextMenu?[] _held = new Native.IContextMenu?[2];

        public StaThread Thread { get; } = new("OAN shell menus");

        /// <summary>On this thread, once a menu has closed or was never shown: the HMENU goes, and the IContextMenu takes its
        /// slot. The one before goes only now, after the new one exists, so a surrogate both use never drops to zero.</summary>
        public void Keep(bool background, Native.IContextMenu? menu, nint popup)
        {
            if (popup != 0) Native.DestroyMenu(popup);
            if (menu is null) return;

            var slot = background ? 1 : 0;
            ShellContextMenu.Release(_held[slot], 0);
            _held[slot] = menu;
        }

        /// <summary>A build prepared and never opened is kept like any other.</summary>
        public void Keep(Prepared? unused)
        {
            if (unused is not null) Keep(unused.Background, unused.Menu, unused.Popup);
        }

        public void ReleaseAll()
        {
            foreach (var menu in _held) ShellContextMenu.Release(menu, 0);
            Array.Clear(_held);
        }
    }

    /// <summary>A build started at right-button-down. Menu and Popup are written on the menu thread, read once it has finished.</summary>
    internal sealed class Prepared(bool background, string key)
    {
        public bool Background { get; } = background;
        public string Key { get; } = key;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public Native.IContextMenu? Menu { get; set; }
        public nint Popup { get; set; }
    }
}
