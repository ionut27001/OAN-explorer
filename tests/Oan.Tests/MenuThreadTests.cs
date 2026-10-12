using System.Diagnostics;
using System.Runtime.CompilerServices;
using Oan.Core.Shell;
using Xunit;
using static Oan.Tests.ContextMenuTests;

namespace Oan.Tests;

/// <summary>
/// The one thread shell menus are built on, and what it keeps between right-clicks: the last menu of each slot, the build
/// started at button-down, the warm-up. ContextMenuTests builds its menus on the same thread, so the two never run at once.
/// </summary>
[Collection(nameof(ShellContextMenu))]
public class MenuThreadTests
{
    /// <summary>Builds that answer at once, each with a fake menu and a real, empty HMENU, noting the thread they ran on.
    /// The window handle the tests pass is not a window, so the menu closes the moment it would open.</summary>
    private sealed class Builds
    {
        public List<FakeMenu> Made { get; } = [];
        public List<int> Threads { get; } = [];

        public (Native.IContextMenu? Menu, nint Popup) Build()
        {
            var (fake, menu) = FakeMenu.Make();
            Made.Add(fake);
            Threads.Add(Environment.CurrentManagedThreadId);
            return (menu, Native.CreatePopupMenu());
        }
    }

    [Fact]
    public void The_last_menu_is_held_until_the_next_of_its_slot_is_built_then_released_on_its_own_thread()
    {
        var builds = new Builds();

        ShellContextMenu.Show(1, [Item(@"C:\a.oanheld")], 0, 0, builds.Build);
        ShellContextMenu.Show(1, [Item(@"C:\b.oanheld")], 0, 0, builds.Build, background: true);
        var first = builds.Made[0];

        // Still held while its successor is built, so a surrogate both reference never drops to zero in between.
        int? duringNext = -1;
        ShellContextMenu.Show(1, [Item(@"C:\c.oanheld")], 0, 0, () =>
        {
            duringNext = first.ReleasedOn;
            return builds.Build();
        });

        Assert.Null(duringNext);
        Assert.Equal(builds.Threads[0], first.ReleasedOn);
        Assert.All(builds.Threads, thread => Assert.Equal(builds.Threads[0], thread));

        // The folder's menu has a slot of its own, and the newest menu stays held.
        Assert.Null(builds.Made[1].ReleasedOn);
        Assert.Null(builds.Made[2].ReleasedOn);
    }

    [Fact]
    public void A_lost_thread_is_replaced_and_what_it_built_is_never_released_from_another()
    {
        var builds = new Builds();
        var release = new ManualResetEventSlim();

        ShellContextMenu.Show(1, [Item(@"C:\a.oanlostheld")], 0, 0, builds.Build);
        LoseTheThread(release);

        var next = ShellContextMenu.Show(1, [Item(@"C:\b.oanlostnext")], 0, 0, builds.Build);
        var again = ShellContextMenu.Show(1, [Item(@"C:\c.oanlostheld")], 0, 0, builds.Build);

        // Left unreachable, the lost thread's menus would be released by the finalizer, from its own thread.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Null(builds.Made[0].ReleasedOn);
        Assert.NotEqual(builds.Threads[0], builds.Threads[1]);
        Assert.Equal(default, next);
        Assert.Equal(default, again);
        Assert.Equal(3, builds.Made.Count);
        release.Set();
    }

    /// <summary>Loses the thread as a verb that opens a dialog of its own does: in a job that holds none of the menus the
    /// thread keeps. A method of its own, so that no local of the test keeps them reachable either.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoseTheThread(ManualResetEventSlim release)
    {
        var (apartment, _, _, _) = MenuThread.Build(() => default, background: false, key: null);
        Assert.False(apartment.Thread.Run(release.Wait, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void A_build_left_running_that_hangs_marks_nothing_stuck_and_holds_a_right_click_up_by_one_deadline_at_most()
    {
        var (hangs, release, asked) = Hangs();
        var builds = new Builds();

        // As the warm-up is, or a build prepared at button-down that no menu came for.
        ShellContextMenu.Prepare(1, [Item(@"C:\a.oanleft")], hangs, background: false, "never opened");
        Assert.True(SpinWait.SpinUntil(() => asked() == 1, Generous), "the build never started");

        var clock = Stopwatch.StartNew();
        var shown = ShellContextMenu.Show(1, [Item(@"C:\b.oanleft")], 0, 0, builds.Build);
        clock.Stop();

        var again = Stopwatch.StartNew();
        ShellContextMenu.Show(1, [Item(@"C:\c.oanleft")], 0, 0, builds.Build);
        again.Stop();
        release.Set();

        Assert.Equal(default, shown);
        Assert.Equal(2, builds.Made.Count);
        Assert.True(clock.Elapsed < ShellContextMenu.Deadline + TimeSpan.FromMilliseconds(500),
            $"the right-click behind a hung build waited {clock.ElapsedMilliseconds}ms");
        Assert.True(again.Elapsed < TimeSpan.FromMilliseconds(250), $"the next one waited {again.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void No_warm_up_starts_once_a_menu_has_been_built()
    {
        var (hangs, release, asked) = Hangs();
        var builds = new Builds();

        // A menu is open; everything it does while up runs on the thread it was built on.
        var (apartment, _, menu, popup) = MenuThread.Build(builds.Build, background: false, key: null);

        // The dispatcher runs the warm-up inside the menu's modal loop. A submenu opened after that must not wait behind it.
        MenuThread.WarmUp(hangs, hangs);
        var submenu = apartment.Thread.Run(() => { }, ShellContextMenu.DrawDeadline, abandon: false);

        apartment.Thread.Run(() => apartment.Keep(background: false, menu, popup), Generous);
        release.Set();

        Assert.True(submenu);
        Assert.Equal(0, asked());
    }

    [Fact]
    public void A_build_prepared_at_button_down_is_used_only_for_the_same_menu()
    {
        var prepared = new Builds();
        var opened = new Builds();
        ShellItem[] items = [Item(@"C:\a.oanprepared")];

        ShellContextMenu.Prepare(1, items, prepared.Build, background: false, "the same");
        ShellContextMenu.Show(1, items, 0, 0, opened.Build, background: false, "the same");
        Assert.Empty(opened.Made);

        ShellContextMenu.Prepare(1, items, prepared.Build, background: false, "the same");
        ShellContextMenu.Show(1, items, 0, 0, opened.Build, background: false, "another");

        ShellContextMenu.Prepare(1, items, prepared.Build, background: true, "the same");
        ShellContextMenu.Show(1, items, 0, 0, opened.Build, background: false, "the same");

        Assert.Equal(3, prepared.Made.Count);
        Assert.Equal(2, opened.Made.Count);

        // One nobody opened is held like any other, and the one it replaced in its slot goes.
        Assert.Equal(prepared.Threads[0], prepared.Made[0].ReleasedOn);
        Assert.Null(prepared.Made[2].ReleasedOn);
    }

    [Fact]
    public void A_prepared_build_is_waited_for_while_it_runs_and_used_however_long_the_button_was_held()
    {
        var prepared = new Builds();
        var opened = new Builds();
        ShellItem[] items = [Item(@"C:\a.oanpress")];

        // The menu opens while its build is still going: what is left of the deadline is waited for.
        ShellContextMenu.Prepare(1, items, () => { Thread.Sleep(300); return prepared.Build(); }, background: false, "the same");
        var running = ShellContextMenu.Show(1, items, 0, 0, opened.Build, background: false, "the same");

        // A press held past the deadline: the build finished long ago.
        ShellContextMenu.Prepare(1, items, prepared.Build, background: false, "the same");
        Thread.Sleep(ShellContextMenu.Deadline + TimeSpan.FromMilliseconds(100));
        var held = ShellContextMenu.Show(1, items, 0, 0, opened.Build, background: false, "the same");

        Assert.Equal(default, running);
        Assert.Equal(default, held);
        Assert.Equal(2, prepared.Made.Count);
        Assert.Empty(opened.Made);
    }

    [Fact]
    public void A_prepared_build_is_told_apart_by_the_flags_the_own_items_and_every_path()
    {
        OwnMenuItem[] own = [new(1, "Mount")];
        string[] paths = [@"C:\a.txt", @"C:\b.txt"];
        var key = ShellContextMenu.Key(Native.CMF_EXPLORE, own, paths);

        Assert.Equal(key, ShellContextMenu.Key(Native.CMF_EXPLORE, [new(1, "Mount")], [@"C:\a.txt", @"C:\b.txt"]));

        // Shift held, a mount that changed OAN's own entries, one more file selected: each is another menu.
        Assert.NotEqual(key, ShellContextMenu.Key(Native.CMF_EXPLORE | Native.CMF_EXTENDEDVERBS, own, paths));
        Assert.NotEqual(key, ShellContextMenu.Key(Native.CMF_EXPLORE, [new(1, "Eject")], paths));
        Assert.NotEqual(key, ShellContextMenu.Key(Native.CMF_EXPLORE, own, [.. paths, @"C:\c.txt"]));

        // A tick that moved inside a submenu: the view changed since the build.
        Assert.NotEqual(
            ShellContextMenu.Key(Native.CMF_EXPLORE, [new(0, "View", Items: [new(20, "Details", Checked: true), new(21, "List")])], paths),
            ShellContextMenu.Key(Native.CMF_EXPLORE, [new(0, "View", Items: [new(20, "Details"), new(21, "List", Checked: true)])], paths));
    }
}
