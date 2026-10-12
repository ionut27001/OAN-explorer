using System.Diagnostics;
using System.Runtime.InteropServices;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The menu itself puts UI on screen and runs code we did not write, so what is tested here is the
/// part that keeps one of those extensions from taking the window with it: the deadline, the
/// refusal that comes back instead, and the list of kinds already known to hang. The one thread
/// menus are built on is MenuThreadTests', and shared with it.
///
/// The slow part is handed in as a delegate, so the extension that never answers is a fake that
/// never returns. Every kind used here is made up, so no test can poison another's.
/// </summary>
[Collection(nameof(ShellContextMenu))]
public class ContextMenuTests
{
    internal static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    /// <summary>A build that never comes back, and the switch that lets its thread go at the end.</summary>
    internal static (Func<(Native.IContextMenu? Menu, nint Popup)> Build, ManualResetEventSlim Release, Func<int> Asked) Hangs()
    {
        // Not disposed: the thread we abandon is still sitting in Wait, and disposing the event
        // under it would throw there instead of letting it go.
        var release = new ManualResetEventSlim();
        var asked = 0;

        return (() =>
        {
            Interlocked.Increment(ref asked);
            release.Wait();
            return default;
        }, release, () => Volatile.Read(ref asked));
    }

    /// <summary>An extension that owner-draws its items: after <paramref name="delay"/>, it measures one by writing into
    /// the struct it was handed, as TortoiseGit does for its icons.</summary>
    private sealed class Measures(TimeSpan delay) : Native.IContextMenu, Native.IContextMenu3
    {
        public ManualResetEventSlim Wrote { get; } = new();

        public int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result)
        {
            Thread.Sleep(delay);
            var item = Marshal.PtrToStructure<Native.MEASUREITEMSTRUCT>(lParam);
            item.itemWidth = item.itemID * 10;
            item.itemHeight = 24;
            Marshal.StructureToPtr(item, lParam, fDeleteOld: false);

            Wrote.Set();
            result = 1;
            return 0;
        }

        public int HandleMenuMsg(uint msg, nint wParam, nint lParam) => HandleMenuMsg2(msg, wParam, lParam, out _);
        public int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags) => 0;
        public int InvokeCommand(in Native.CMINVOKECOMMANDINFOEX invoke) => 0;
        public int GetCommandString(nint idCmd, uint type, nint reserved, nint name, uint max) => 0;
    }

    /// <summary>What USER32 hands the window with WM_MEASUREITEM, and owns again once the window has answered.</summary>
    private static nint Measuring(uint itemId)
    {
        var owned = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MEASUREITEMSTRUCT>());
        Marshal.StructureToPtr(new Native.MEASUREITEMSTRUCT { itemID = itemId, itemWidth = 1, itemHeight = 1 }, owned, false);
        return owned;
    }

    /// <summary>The paths under test are made up, so the directory flag never matters here.</summary>
    internal static ShellItem Item(string path) => new(path, IsDirectory: false);

    [Fact]
    public void A_menu_that_never_answers_comes_back_as_a_refusal_instead_of_a_frozen_window()
    {
        var (build, release, _) = Hangs();
        var clock = Stopwatch.StartNew();

        var result = ShellContextMenu.Show(1, [Item(@"C:\films\big.oanhang")], 40, 60, build);

        clock.Stop();
        release.Set();

        Assert.False(result.Ran);
        Assert.NotNull(result.Refusal);
        Assert.Contains(".oanhang", result.Refusal);
        Assert.True(clock.Elapsed < Generous, $"the caller waited {clock.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void A_kind_that_hung_once_is_not_asked_a_second_time()
    {
        var (build, release, asked) = Hangs();

        ShellContextMenu.Show(1, [Item(@"C:\films\one.oanskip")], 0, 0, build);

        var clock = Stopwatch.StartNew();
        var again = ShellContextMenu.Show(1, [Item(@"D:\other\two.OANSKIP")], 0, 0, build);
        clock.Stop();
        release.Set();

        // A different file, a different folder, different case: what is remembered is the kind.
        Assert.Equal(1, asked());
        Assert.NotNull(again.Refusal);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), $"waited {clock.ElapsedMilliseconds}ms again");
    }

    [Fact]
    public void A_mixed_selection_that_hangs_does_not_blacklist_the_kinds_in_it()
    {
        var (build, release, asked) = Hangs();

        ShellContextMenu.Show(1, [Item(@"C:\a.oanmixed"), Item(@"C:\b.oanplain")], 0, 0, build);
        release.Set();

        // Nothing in a mixed selection says which handler hung, and guessing would cost the user
        // Explorer's menu on a type that was never the problem.
        ShellContextMenu.Show(1, [Item(@"C:\c.oanplain")], 0, 0, () => default);

        Assert.Equal(1, asked());
        Assert.False(ShellContextMenu.Show(1, [Item(@"C:\d.oanplain")], 0, 0, () => default).Ran);
    }

    [Fact]
    public void A_shell_that_answers_at_once_is_asked_again_next_time()
    {
        var asked = 0;
        var build = new Func<(Native.IContextMenu? Menu, nint Popup)>(() =>
        {
            asked++;
            return default;
        });

        var clock = Stopwatch.StartNew();
        var first = ShellContextMenu.Show(1, [Item(@"C:\a.oanfast")], 0, 0, build);
        var second = ShellContextMenu.Show(1, [Item(@"C:\b.oanfast")], 0, 0, build);
        clock.Stop();

        // Having no menu to give is not hanging, so it is not remembered as one.
        Assert.Equal(2, asked);
        Assert.NotNull(first.Refusal);
        Assert.NotNull(second.Refusal);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"two menus took {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void A_big_selection_that_runs_out_of_time_is_remembered_by_its_size_not_its_kind()
    {
        var (build, release, asked) = Hangs();
        var many = Enumerable.Range(0, ShellContextMenu.FewItems + 1).Select(i => Item($@"C:\many\{i}.oanmany")).ToArray();

        var refused = ShellContextMenu.Show(1, many, 0, 0, build);
        var more = ShellContextMenu.Show(1, [.. many, Item(@"C:\many\other.oanmanyother")], 0, 0, build);
        release.Set();

        // A thousand paths take longer to parse than the deadline allows, whatever their handler does: so the kind is
        // asked again, a selection as large is not, and no program is blamed.
        var one = 0;
        ShellContextMenu.Show(1, [Item(@"C:\one.oanmany")], 0, 0, () => { one++; return default; });

        Assert.Equal(1, asked());
        Assert.Equal(1, one);
        Assert.Equal(ShellContextMenu.Refusal([".oanmany"], ShellContextMenu.Why.TooMany), refused.Refusal);
        Assert.Equal(refused, more);
    }

    [Fact]
    public void The_work_runs_on_a_named_background_thread_in_a_single_threaded_apartment()
    {
        using var sta = new StaThread("OAN shell menu .oanapartment");
        var apartment = ApartmentState.Unknown;
        var background = false;
        string? name = null;

        Assert.True(sta.Run(() =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            background = Thread.CurrentThread.IsBackground;
            name = Thread.CurrentThread.Name;
        }, Generous));

        // STA because that is what a COM shell extension requires, background so a thread we had
        // to abandon cannot hold the process open, named so it can be recognised in a debugger.
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(background);
        Assert.Equal("OAN shell menu .oanapartment", name);
    }

    [Fact]
    public void Nothing_is_ever_posted_to_a_thread_that_passed_its_deadline()
    {
        var release = new ManualResetEventSlim();
        using var sta = new StaThread("OAN shell menu .oanlost");

        Assert.False(sta.Run(release.Wait, TimeSpan.FromMilliseconds(100)));
        Assert.True(sta.Lost);

        var ran = false;
        var clock = Stopwatch.StartNew();
        var second = sta.Run(() => ran = true, TimeSpan.FromSeconds(10));
        clock.Stop();
        release.Set();

        // The second call neither runs nor waits: it would only queue behind the stuck one.
        Assert.False(second);
        Assert.False(ran);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100), $"waited {clock.ElapsedMilliseconds}ms on a lost thread");
    }

    [Fact]
    public void An_extension_that_faults_costs_the_menu_and_nothing_else()
    {
        using var sta = new StaThread("OAN shell menu .oanfaults");

        // An exception leaving a thread ends the process, so the one place it can be caught is
        // inside the thread. The thread survives it and the next call still works.
        Assert.True(sta.Run(() => throw new InvalidOperationException("an extension that faults"), Generous));
        Assert.False(sta.Lost);

        var ran = false;
        Assert.True(sta.Run(() => ran = true, Generous));
        Assert.True(ran);
    }

    [Fact]
    public void A_posted_job_is_not_waited_for_and_nothing_is_posted_behind_it()
    {
        var release = new ManualResetEventSlim();
        using var sta = new StaThread("OAN shell menu .oanposted");

        var clock = Stopwatch.StartNew();
        Assert.True(sta.Post(release.Wait));

        // A second job would take the first one's completion for its own.
        Assert.False(sta.Post(() => { }));
        clock.Stop();

        release.Set();
        Assert.True(sta.Wait(Generous));
        Assert.False(sta.Lost);

        var ran = false;
        Assert.True(sta.Run(() => ran = true, Generous));
        Assert.True(ran);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100), $"posting waited {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void An_item_measured_too_late_never_writes_into_the_struct_the_menu_has_taken_back()
    {
        using var sta = new StaThread("OAN shell menu .oanlate");
        var late = new Measures(ShellContextMenu.DrawDeadline * 3);
        var owned = Measuring(7);

        try
        {
            Assert.Null(ShellContextMenu.DrawItem(sta, late, Native.WM_MEASUREITEM, 0, owned));

            // The window has answered; the extension writes only now.
            Assert.True(late.Wrote.Wait(Generous), "the late measure never ran");
            Assert.True(sta.Wait(Generous));
            Assert.False(sta.Lost);

            var after = Marshal.PtrToStructure<Native.MEASUREITEMSTRUCT>(owned);
            Assert.Equal((7u, 1u, 1u), (after.itemID, after.itemWidth, after.itemHeight));
        }
        finally
        {
            Marshal.FreeHGlobal(owned);
        }
    }

    [Fact]
    public void An_item_measured_in_time_reaches_the_menu()
    {
        using var sta = new StaThread("OAN shell menu .oanmeasured");
        var owned = Measuring(7);

        try
        {
            // Started first, so only the measure itself is inside the draw deadline.
            Assert.True(sta.Run(() => { }, Generous));
            Assert.Equal(1, ShellContextMenu.DrawItem(sta, new Measures(TimeSpan.Zero), Native.WM_MEASUREITEM, 0, owned));

            var after = Marshal.PtrToStructure<Native.MEASUREITEMSTRUCT>(owned);
            Assert.Equal((70u, 24u), (after.itemWidth, after.itemHeight));
        }
        finally
        {
            Marshal.FreeHGlobal(owned);
        }
    }

    /// <summary>
    /// The one test that asks the real shell, because no fake can answer the question the whole
    /// design rests on: that Explorer's menu — with whatever extensions this machine has — can be
    /// built somewhere other than the window's own thread.
    /// </summary>
    [Fact]
    public void The_real_shell_fills_a_menu_on_the_worker_thread()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("note.txt");

        using var sta = new StaThread("OAN shell menu .txt");
        (Native.IContextMenu? Menu, nint Popup) built = default;

        Assert.True(sta.Run(() => built = MenuBuilder.Build([file], Native.CMF_EXPLORE), Generous),
            "the shell did not answer in 5s on a machine with nothing wrong with it");
        Assert.NotNull(built.Menu);
        Assert.NotEqual(0, built.Popup);

        sta.Run(() => ShellContextMenu.Release(built.Menu, built.Popup), Generous);
    }

    /// <summary>
    /// What holding the last menu rests on: an IContextMenu kept between right-clicks keeps the surrogates behind its verbs
    /// running, and must not keep the file or folder it was built for open. Asked of the real shell, with this machine's extensions.
    /// </summary>
    [Fact]
    public void A_held_menu_does_not_keep_its_file_or_folder_open()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("held.txt", "held");
        var folder = temp.AddFolder("held folder");
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "inside");

        using var sta = new StaThread("OAN shell menu held");
        var held = new List<Native.IContextMenu>();

        Assert.True(sta.Run(() =>
        {
            foreach (var (menu, popup) in new[]
                     {
                         MenuBuilder.Build([file], Native.CMF_EXPLORE),
                         MenuBuilder.Build([folder], Native.CMF_EXPLORE),
                         MenuBuilder.BuildForFolder(0, folder, Native.CMF_EXPLORE, null),
                     })
            {
                // As the app holds them: the HMENU goes when the menu closes, the IContextMenu stays.
                ShellContextMenu.Release(null, popup);
                if (menu is not null) held.Add(menu);
            }
        }, Generous), "the shell did not answer in 5s");
        Assert.Equal(3, held.Count);

        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            File.Move(file, file + ".renamed");
            File.Delete(file + ".renamed");

            Directory.Move(folder, folder + " renamed");
            Directory.Delete(folder + " renamed", recursive: true);

            Assert.False(File.Exists(file + ".renamed"));
            Assert.False(Directory.Exists(folder + " renamed"));
        }
        finally
        {
            sta.Run(() => held.ForEach(menu => ShellContextMenu.Release(menu, 0)), Generous);
        }
    }

    [Fact]
    public void Own_entries_can_open_a_submenu_of_ticked_greyed_and_separated_lines()
    {
        var popup = Native.CreatePopupMenu();
        nint submenu;

        try
        {
            MenuBuilder.InsertOwn(popup,
                [new(0, "View", Items: [new(20, "Details", Checked: true), OwnMenuItem.Separator, new(30, "Name", Enabled: false)])]);

            // View, then the separator between OAN's entries and the shell's.
            Assert.Equal(2, Native.GetMenuItemCount(popup));
            submenu = Native.GetSubMenu(popup, 0);
            Assert.NotEqual(0, submenu);

            Assert.Equal(3, Native.GetMenuItemCount(submenu));
            Assert.Equal((20, Native.MF_CHECKED), (GetMenuItemID(submenu, 0), GetMenuState(submenu, 0, Native.MF_BYPOSITION) & Native.MF_CHECKED));
            Assert.NotEqual(0u, GetMenuState(submenu, 1, Native.MF_BYPOSITION) & Native.MF_SEPARATOR);
            Assert.Equal((30, Native.MF_GRAYED), (GetMenuItemID(submenu, 2), GetMenuState(submenu, 2, Native.MF_BYPOSITION) & Native.MF_GRAYED));
        }
        finally
        {
            Native.DestroyMenu(popup);
        }

        // Nothing holds the submenu; it goes with the menu it is in.
        Assert.False(IsMenu(submenu));
    }

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(nint menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemID(nint menu, int position);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsMenu(nint menu);

    [Fact]
    public void A_hang_is_remembered_against_the_kind_of_file_not_the_path()
    {
        Assert.Equal(new[] { ".iso" }, ShellContextMenu.Kinds([Item(@"E:\films\a.ISO"), Item(@"E:\backup\b.iso")]));
        Assert.Equal(new[] { ".iso", ".txt" }, ShellContextMenu.Kinds([Item(@"E:\a.iso"), Item(@"E:\b.txt")]));
        Assert.Equal(new[] { "files with no extension" }, ShellContextMenu.Kinds([Item(@"C:\src\LICENSE")]));
        Assert.Equal(new[] { "folders" }, ShellContextMenu.Kinds([new ShellItem(Path.GetTempPath(), IsDirectory: true)]));
    }

    [Fact]
    public void The_refusal_names_the_program_that_owns_the_type_when_the_registry_knows_it()
    {
        var known = ShellContextMenu.Owner(".txt") ?? ShellContextMenu.Owner(".zip") ?? "";

        Assert.True(known.Length > 0, "Neither .txt nor .zip is registered here, so this test proves nothing.");
        Assert.Contains(known, ShellContextMenu.Refusal([".txt"]) + ShellContextMenu.Refusal([".zip"]));

        // Nothing invented, and nothing to say about a bucket that is not an extension at all.
        Assert.Null(ShellContextMenu.Owner(".oannobodyhasthis"));
        Assert.Null(ShellContextMenu.Owner("folders"));
        Assert.Contains("folders", ShellContextMenu.Refusal(["folders"]));
    }

    [Fact]
    public void A_type_whose_name_the_registry_leaves_blank_is_still_named_by_something()
    {
        // .iso here is DAEMON.Tools.Lite with an empty friendly name — read one step too
        // literally, that becomes a message with a hole where the program should be.
        foreach (var kind in new[] { ".iso", ".txt", ".zip", ".exe", ".pdf", ".oannobodyhasthis" })
            Assert.True(ShellContextMenu.Owner(kind) is null or { Length: > 0 },
                $"{kind} came back as an empty name, which reads as a missing word.");
    }
}
