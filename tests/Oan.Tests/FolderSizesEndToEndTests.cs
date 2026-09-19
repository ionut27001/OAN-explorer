using System.IO;
using Oan.App.Platform;
using Oan.Core.Operations;
using Oan.App.ViewModels;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The whole path the switch takes, through the real window model: navigate, switch on, and the
/// rows fill. The unit tests cover the pieces; this covers them being wired to each other.
/// </summary>
[Collection(DiskBound.Name)]
public class FolderSizesEndToEndTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public async Task Turning_the_switch_on_fills_the_folder_rows_of_the_listing()
    {
        using var temp = new TempFolder();
        temp.AddFolder("one");
        temp.AddFolder("two");
        File.WriteAllText(Path.Combine(temp.Root, "one", "a.bin"), new string('x', 700));
        File.WriteAllText(Path.Combine(temp.Root, "two", "b.bin"), new string('x', 40));

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);

        vm.FolderSizes.On = true;

        Assert.True(Settles(() => Row(vm, "one").SortSize == 700 && Row(vm, "two").SortSize == 40),
            $"one showed '{Row(vm, "one").SizeText}', two showed '{Row(vm, "two").SizeText}'");
    }

    [Fact]
    public async Task Sorting_by_size_orders_the_folders_by_what_was_measured()
    {
        using var temp = new TempFolder();
        Folder(temp, "big", 900);
        Folder(temp, "small", 10);

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);
        Assert.True(Settles(() => Row(vm, "big").SortSize == 900 && Row(vm, "small").SortSize == 10),
            $"never measured: big '{Row(vm, "big").SizeText}', small '{Row(vm, "small").SizeText}'");

        vm.SortCommand.Execute(SortKey.Size);

        Assert.Equal(["small", "big"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_size_that_lands_after_the_sort_still_takes_its_place_in_the_order()
    {
        using var temp = new TempFolder();
        Folder(temp, "big", 900);
        Folder(temp, "small", 10);

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);

        // Sorted by size before anything has been measured, which is what happens every time you
        // walk into a folder already sorted that way.
        vm.SortCommand.Execute(SortKey.Size);

        Assert.True(Settles(() => vm.Rows.Select(r => r.Name).SequenceEqual(["small", "big"])),
            $"stayed {string.Join(", ", vm.Rows.Select(r => $"{r.Name} {r.SizeText}"))}");
    }

    [Fact]
    public async Task Coming_back_to_a_folder_does_not_walk_it_all_over_again()
    {
        using var temp = new TempFolder();
        Folder(temp, "one", 700);
        Folder(temp, "two", 40);

        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);
        // Both, not just the first: a measurement still in flight when we navigate away is
        // abandoned on purpose, so it never reaches the cache and cannot be waiting on the way
        // back. Waiting for one of the two made this pass or fail on how busy the machine was.
        Assert.True(Settles(() => Row(vm, "one").SortSize == 700 && Row(vm, "two").SortSize == 40),
            "never measured the first time");

        await vm.NavigateAsync(Path.Combine(temp.Root, "one"));
        await vm.NavigateAsync(temp.Root);

        // Straight away, with no waiting: the answer was already known, so nothing should be
        // walking and no row should be sitting on an ellipsis.
        Assert.Equal(700, Row(vm, "one").SortSize);
        Assert.Equal(40, Row(vm, "two").SortSize);
    }

    private static void Folder(TempFolder temp, string name, int bytes)
    {
        var path = temp.AddFolder(name);
        File.WriteAllText(Path.Combine(path, "a.bin"), new string('x', bytes));
    }

    private static EntryViewModel Row(MainViewModel vm, string name) => vm.Rows.Single(r => r.Name == name);

    private static bool Settles(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) Thread.Sleep(20);
        return done();
    }
}
