using System.Diagnostics;
using System.IO;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Work the app used to do twice, or start and abandon. Measured before these were fixed: the
/// tree behind the map was re-read on every filter keystroke and every column click, none of the
/// restarts finishing; and a folder being written to steadily was never reported at all, because
/// each event pushed the settle deadline back again.
///
/// These are here rather than in the benchmarks because they are rules, not numbers — the cost
/// only matters on a real tree, but the rule is provable on a handful of files.
/// </summary>
[Collection(DiskBound.Name)]
public class RepeatedWorkTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public async Task Filtering_and_sorting_leave_the_tree_behind_the_map_alone()
    {
        using var temp = Tree();
        var vm = Mapped();
        await vm.NavigateAsync(temp.Root);

        Assert.True(Settles(() => vm.FolderSizes.Tree is not null), "the tree was never read");
        var first = vm.FolderSizes.Tree;

        vm.Filter = "o";
        Assert.Same(first, vm.FolderSizes.Tree);

        vm.SortCommand.Execute(SortKey.Size);
        Assert.Same(first, vm.FolderSizes.Tree);
    }

    [Fact]
    public async Task Reading_the_folder_again_does_read_the_tree_again()
    {
        using var temp = Tree();
        var vm = Mapped();
        await vm.NavigateAsync(temp.Root);

        Assert.True(Settles(() => vm.FolderSizes.Tree is not null), "the tree was never read");
        var first = vm.FolderSizes.Tree;

        await vm.NavigateAsync(temp.Root);

        Assert.True(Settles(() => vm.FolderSizes.Tree is not null && !ReferenceEquals(first, vm.FolderSizes.Tree)),
            "opening the folder again kept the tree read before it");
    }

    [Fact]
    public void A_folder_being_written_to_steadily_is_reported_before_the_writing_stops()
    {
        using var temp = new TempFolder();
        temp.AddFolder("busy");
        var inside = Path.Combine(temp.Root, "busy");

        var reports = 0;
        using var changes = new FolderChanges(_ => Interlocked.Increment(ref reports),
                                              TimeSpan.FromMilliseconds(100));
        changes.Watch(temp.Root);

        // Faster than the settle, for far longer than it: every one of these used to push the
        // deadline back, so nothing was ever reported until the writing stopped.
        var clock = Stopwatch.StartNew();
        for (var i = 0; clock.Elapsed < TimeSpan.FromMilliseconds(1500); i++)
        {
            File.WriteAllText(Path.Combine(inside, $"{i}.tmp"), "x");
            Thread.Sleep(30);
        }

        Assert.True(Volatile.Read(ref reports) > 0, "nothing was reported while the folder was being written to");
    }

    private static MainViewModel Mapped() =>
        new(new NoOps(), new Settings { FolderSizes = true, Treemap = true });

    private static TempFolder Tree()
    {
        var temp = new TempFolder();
        temp.AddFolder("one");
        temp.AddFolder("two");
        File.WriteAllText(Path.Combine(temp.Root, "one", "a.bin"), new string('x', 700));
        File.WriteAllText(Path.Combine(temp.Root, "two", "b.bin"), new string('x', 40));
        return temp;
    }

    private static bool Settles(Func<bool> done, int within = 8000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < within)
        {
            if (done()) return true;
            Thread.Sleep(25);
        }
        return done();
    }
}
