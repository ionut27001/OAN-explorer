using System.IO.Compression;
using System.Text;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

public class ArchiveBrowsingTests
{
    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used here"));
    }

    private static string Zip(string folder, params string[] names)
    {
        var path = Path.Combine(folder, "sample.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var name in names)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes("body of " + name));
        }

        return path;
    }

    private static MainViewModel Open(string root)
    {
        var vm = new MainViewModel(new NoOps(), new Settings());
        vm.NavigateAsync(root).GetAwaiter().GetResult();
        return vm;
    }

    private static async Task Settle(MainViewModel vm)
    {
        // Waiting for it to happen, not timing it: on a busy machine the old two and a half
        // seconds was the difference between a green suite and a red one.
        for (var i = 0; i < 600 && !vm.Archive.Active; i++) await Task.Delay(20);
    }

    private static EntryViewModel Row(MainViewModel vm, string name) =>
        vm.Rows.Single(r => r.Name == name);

    [Fact]
    public async Task Opening_an_archive_shows_what_is_at_its_top_level()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "readme.txt", "docs/note.txt", "docs/deep/inner.txt");

        var vm = Open(temp.Root);
        vm.Open(Row(vm, "sample.zip"));
        await Settle(vm);

        // "docs" is never an entry of its own in that zip — only the files under it are. A view
        // that showed only named entries would show nothing but readme.txt.
        Assert.Equal(["docs", "readme.txt"], vm.Rows.Select(r => r.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Nothing_inside_an_archive_can_be_written()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "readme.txt");

        var vm = Open(temp.Root);
        vm.Open(Row(vm, "sample.zip"));
        await Settle(vm);
        vm.Selection = [Row(vm, "readme.txt")];

        Assert.False(vm.CanWriteHere);
        Assert.False(vm.NewFolderCommand.CanExecute(null));
        Assert.False(vm.RenameCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.CutCommand.CanExecute(null));

        // Copy stays on: taking something out is allowed, it is putting things in that is not.
        Assert.True(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Walking_down_and_back_up_ends_where_the_archive_lives()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "docs/deep/inner.txt");

        var vm = Open(temp.Root);
        vm.Open(Row(vm, "sample.zip"));
        await Settle(vm);

        vm.Open(Row(vm, "docs"));
        Assert.Equal(["deep"], vm.Rows.Select(r => r.Name));

        vm.Open(Row(vm, "deep"));
        Assert.Equal(["inner.txt"], vm.Rows.Select(r => r.Name));

        vm.UpCommand.Execute(null);
        Assert.Equal(["deep"], vm.Rows.Select(r => r.Name));

        vm.UpCommand.Execute(null);
        Assert.Equal(["docs"], vm.Rows.Select(r => r.Name));

        // One more and we are out of the archive entirely, back in the folder holding it.
        vm.UpCommand.Execute(null);

        // Leaving is immediate; reloading the folder behind it is not, so the wait is on the
        // rows arriving rather than on the flag that already flipped.
        for (var i = 0; i < 600 && !vm.Rows.Any(r => r.Name == "sample.zip"); i++) await Task.Delay(20);

        Assert.False(vm.Archive.Active);
        Assert.Contains(vm.Rows, r => r.Name == "sample.zip");
    }

    [Fact]
    public async Task A_folder_inside_an_archive_has_no_size_rather_than_a_size_of_zero()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "docs/note.txt");

        var vm = Open(temp.Root);
        vm.Open(Row(vm, "sample.zip"));
        await Settle(vm);

        Assert.Null(Row(vm, "docs").Entry.Size);
        Assert.Equal("—", Row(vm, "docs").SizeText);
    }

    [Fact]
    public async Task The_status_bar_says_the_archive_is_read_only()
    {
        using var temp = new TempFolder();
        Zip(temp.Root, "readme.txt");

        var vm = Open(temp.Root);
        vm.Open(Row(vm, "sample.zip"));
        await Settle(vm);

        Assert.Contains("read only", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
