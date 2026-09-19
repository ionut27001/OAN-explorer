using System.Diagnostics;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The menu itself puts UI on screen and runs code we did not write, so what is tested here is the
/// part that keeps one of those extensions from taking the window with it: the deadline, the
/// refusal that comes back instead, and the list of kinds already known to hang.
///
/// The slow part is handed in as a delegate, so the extension that never answers is a fake that
/// never returns. Every kind used here is made up, so no test can poison another's.
/// </summary>
public class ContextMenuTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    /// <summary>A build that never comes back, and the switch that lets its thread go at the end.</summary>
    private static (Func<(Native.IContextMenu? Menu, nint Popup)> Build, ManualResetEventSlim Release, Func<int> Asked) Hangs()
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


    /// <summary>The paths under test are made up, so the directory flag never matters here.</summary>
    private static ShellItem Item(string path) => new(path, IsDirectory: false);

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
