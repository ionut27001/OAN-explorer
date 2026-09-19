using System.IO;
using System.IO.Compression;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Sorting while inside an archive. The rows there come from a different path than the listing's,
/// and that path never learned about the sort.
/// </summary>
public class ArchiveSortTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    [Fact]
    public async Task An_archive_listing_is_in_order_like_any_other()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "zebra.txt", "apple.txt", "docs/note.txt");

        var vm = await Inside(temp);

        // Folders first, then names in order — the same rule the rest of the window follows.
        Assert.Equal(["docs", "apple.txt", "zebra.txt"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Clicking_a_column_header_inside_an_archive_does_not_walk_you_out_of_it()
    {
        using var temp = new TempFolder();
        temp.AddFile("outside.txt", "not in the zip");
        Zip(temp.Root, "readme.txt", "docs/note.txt");

        var vm = await Inside(temp);
        Assert.Equal(["docs", "readme.txt"], vm.Rows.Select(r => r.Name));

        vm.SortCommand.Execute(SortKey.Name);

        Assert.True(vm.Archive.Active, "the archive was left behind");
        Assert.DoesNotContain("outside.txt", vm.Rows.Select(r => r.Name));
        Assert.DoesNotContain("sample.zip", vm.Rows.Select(r => r.Name));
    }

    private static async Task<MainViewModel> Inside(TempFolder temp)
    {
        var vm = new MainViewModel(new NoOps(), new Settings());
        await vm.NavigateAsync(temp.Root);
        vm.Open(vm.Rows.Single(r => r.Name == "sample.zip"));

        for (var i = 0; i < 150 && !vm.Archive.Active; i++) await Task.Delay(20);
        Assert.True(vm.Archive.Active, "never got into the archive");
        return vm;
    }

    private static void Zip(string folder, params string[] names)
    {
        var path = Path.Combine(folder, "sample.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(name);
        }
    }
}
