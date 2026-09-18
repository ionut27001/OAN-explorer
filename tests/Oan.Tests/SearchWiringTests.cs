using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The box in the toolbar filters the rows already loaded. Reported as broken because searching
/// "teo" from C:\ found nothing while the files sat in Downloads — which is what a filter does,
/// and not what the word search promises. These drive the view model directly: no window, no
/// synthetic input.
/// </summary>
public class SearchWiringTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used in these tests"));
    }

    private static MainViewModel Open(string root)
    {
        var vm = new MainViewModel(new NoOps(), new Settings());
        vm.NavigateAsync(root).GetAwaiter().GetResult();
        return vm;
    }

    private static async Task SearchAndSettle(MainViewModel vm)
    {
        vm.SearchCommand.Execute(null);
        for (var i = 0; i < 200 && vm.StatusText.StartsWith("Searching", StringComparison.Ordinal); i++)
            await Task.Delay(25);
    }

    [Fact]
    public async Task The_filter_alone_does_not_look_in_subfolders()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Root, "Downloads"));
        File.WriteAllText(Path.Combine(temp.Root, "Downloads", "teo-poze.txt"), "x");

        var vm = Open(temp.Root);
        vm.Filter = "teo";

        // Exactly the reported behaviour, kept as the baseline the search has to improve on.
        Assert.Empty(vm.Rows);
        Assert.False(vm.InSearch);
    }

    [Fact]
    public async Task Enter_finds_the_same_name_in_a_subfolder()
    {
        using var temp = new TempFolder();
        var down = Path.Combine(temp.Root, "Downloads");
        Directory.CreateDirectory(down);
        File.WriteAllText(Path.Combine(down, "teo-poze.txt"), "x");
        Directory.CreateDirectory(Path.Combine(down, "teo-arhiva"));
        File.WriteAllText(Path.Combine(temp.Root, "altceva.txt"), "x");

        var vm = Open(temp.Root);
        vm.Filter = "teo";
        await SearchAndSettle(vm);

        Assert.True(vm.InSearch);
        Assert.Equal(["teo-arhiva", "teo-poze.txt"], vm.Rows.Select(r => r.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task A_regex_that_does_not_compile_is_an_error_not_an_empty_result()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");

        var vm = Open(temp.Root);
        vm.SearchRegex = true;
        vm.Filter = "([unclosed";
        await SearchAndSettle(vm);

        Assert.False(vm.InSearch);
        Assert.NotNull(vm.WarningText);
    }

    [Fact]
    public async Task Content_search_finds_a_file_by_what_is_inside_it()
    {
        using var temp = new TempFolder();
        var deep = Path.Combine(temp.Root, "a", "b");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "note.txt"), "nothing here\nacul in carul cu fan\n");
        File.WriteAllText(Path.Combine(deep, "other.txt"), "nothing at all");

        var vm = Open(temp.Root);
        vm.SearchContents = true;
        vm.Filter = "acul in carul";
        await SearchAndSettle(vm);

        Assert.Equal("note.txt", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public async Task Leaving_search_goes_back_to_the_folder()
    {
        using var temp = new TempFolder();
        var down = Path.Combine(temp.Root, "Downloads");
        Directory.CreateDirectory(down);
        File.WriteAllText(Path.Combine(down, "teo.txt"), "x");

        var vm = Open(temp.Root);
        vm.Filter = "teo";
        await SearchAndSettle(vm);
        Assert.True(vm.InSearch);

        vm.EndSearchCommand.Execute(null);
        for (var i = 0; i < 100 && vm.InSearch; i++) await Task.Delay(25);

        Assert.False(vm.InSearch);
        Assert.Equal("", vm.Filter);
    }

    [Fact]
    public async Task Opening_a_folder_clears_the_typed_filter()
    {
        using var temp = new TempFolder();
        var sub = Path.Combine(temp.Root, "Downloads");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "raport.txt"), "x");
        File.WriteAllText(Path.Combine(temp.Root, "teo.txt"), "x");

        var vm = Open(temp.Root);
        vm.Filter = "teo";
        Assert.Single(vm.Rows);

        await vm.NavigateAsync(sub);

        // The filter is typed, transient and about the folder you were in. Carried over, the
        // next folder reads as empty and nothing on screen explains why.
        Assert.Equal("", vm.Filter);
        Assert.Equal("raport.txt", Assert.Single(vm.Rows).Name);
    }
}
