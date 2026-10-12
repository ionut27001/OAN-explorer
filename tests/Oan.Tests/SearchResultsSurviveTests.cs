using System.IO;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Search results are rows like any other, and everything that reorders rows used to rebuild them
/// from the folder the search started in — so a click on a column header replaced eight hundred
/// results with the twelve files you searched, and the status bar went on calling them results.
/// </summary>
[Collection(DiskBound.Name)]
public class SearchResultsSurviveTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public async Task Sorting_the_results_reorders_them_instead_of_replacing_them()
    {
        using var temp = new TempFolder();
        temp.AddFile("here.log");
        var deep = temp.AddFolder("nested");
        File.WriteAllText(Path.Combine(deep, "one-target.log"), "");
        File.WriteAllText(Path.Combine(deep, "two-target.log"), "");

        var vm = await Searched(temp.Root, "target");
        Assert.Equal(["one-target.log", "two-target.log"], Names(vm));

        // One click, not two: the list already starts on Name, so clicking it turns it round.
        vm.SortCommand.Execute(SortKey.Name);

        Assert.True(vm.Search.Active, "the sort dropped us out of the search");
        Assert.Equal(["two-target.log", "one-target.log"], Names(vm));
    }

    [Fact]
    public async Task A_result_below_the_folder_you_searched_is_not_swapped_for_the_folder()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        temp.AddFile("b.txt");
        var deep = temp.AddFolder("nested");
        File.WriteAllText(Path.Combine(deep, "only-target.txt"), "");

        var vm = await Searched(temp.Root, "target");

        vm.SortCommand.Execute(SortKey.Size);

        Assert.Equal(["only-target.txt"], Names(vm));
    }

    private static async Task<MainViewModel> Searched(string root, string query)
    {
        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(root);

        vm.Filter = query;
        vm.SearchCommand.Execute(null);

        for (var i = 0; i < 400 && (vm.Search.Running || vm.Rows.Count == 0); i++) await Task.Delay(20);

        Assert.True(vm.Search.Active, "the search never started");
        return vm;
    }

    private static string[] Names(MainViewModel vm) => [.. vm.Rows.Select(r => r.Name)];
}
