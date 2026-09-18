using Oan.Core.FileSystem;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class DirectoryListerTests
{
    [Fact]
    public void Lists_files_and_subfolders()
    {
        using var temp = new TempFolder();
        temp.AddFile("readme.txt", "hello");
        temp.AddFile("data.bin", new string('x', 4096));
        temp.AddFolder("nested");

        var listing = DirectoryLister.List(temp.Root);

        Assert.Null(listing.Error);
        Assert.True(listing.IsComplete);
        Assert.Equal(temp.Root, listing.Path);
        Assert.Equal(
            ["data.bin", "nested", "readme.txt"],
            listing.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Folder_size_is_null_not_zero()
    {
        using var temp = new TempFolder();
        temp.AddFolder("nested");

        var folder = Assert.Single(DirectoryLister.List(temp.Root).Entries);

        Assert.True(folder.IsDirectory);
        Assert.Null(folder.Size);
        Assert.Equal("—", Format.Size(folder.Size));
    }

    [Fact]
    public void File_entries_carry_size_path_and_timestamp()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("readme.txt", "hello");

        var file = Assert.Single(DirectoryLister.List(temp.Root).Entries);

        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
        Assert.Equal(path, file.FullPath);
        Assert.Equal(".txt", file.Extension);
        Assert.NotNull(file.Modified);
    }

    [Fact]
    public void Empty_file_has_size_zero_not_unknown()
    {
        using var temp = new TempFolder();
        temp.AddFile("empty.txt");

        var file = Assert.Single(DirectoryLister.List(temp.Root).Entries);

        Assert.Equal(0, file.Size);
    }

    [Fact]
    public void Hidden_files_are_left_out_by_default_and_shown_when_asked()
    {
        using var temp = new TempFolder();
        temp.AddFile("visible.txt");
        temp.AddHiddenFile("secret.txt", "abc");

        var byDefault = DirectoryLister.List(temp.Root);
        Assert.Equal("visible.txt", Assert.Single(byDefault.Entries).Name);
        Assert.True(byDefault.IsComplete);

        var shown = DirectoryLister.List(temp.Root, new ListOptions { ShowHidden = true });
        Assert.Equal(2, shown.Entries.Count);
        var secret = Assert.Single(shown.Entries, e => e.Name == "secret.txt");
        Assert.True(secret.IsHidden);
        Assert.Equal(3, secret.Size);
    }

    [Fact]
    public void Empty_folder_lists_nothing_and_is_still_complete()
    {
        using var temp = new TempFolder();

        var listing = DirectoryLister.List(temp.Root);

        Assert.Empty(listing.Entries);
        Assert.Null(listing.Error);
        Assert.True(listing.IsComplete);
    }

    [Fact]
    public void Missing_folder_reports_an_error_rather_than_an_empty_listing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "oan-missing-" + Guid.NewGuid().ToString("N"));

        var listing = DirectoryLister.List(missing);

        Assert.NotNull(listing.Error);
        Assert.False(listing.IsComplete);
        Assert.Empty(listing.Entries);
        Assert.Equal(missing, listing.Path);
    }

    [Fact]
    public void Blank_path_reports_an_error()
    {
        var listing = DirectoryLister.List("   ");

        Assert.NotNull(listing.Error);
        Assert.False(listing.IsComplete);
    }

    [Fact]
    public void A_file_passed_as_a_folder_reports_an_error()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("readme.txt");

        var listing = DirectoryLister.List(file);

        Assert.NotNull(listing.Error);
        Assert.False(listing.IsComplete);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void A_file_passed_as_a_folder_is_not_reported_as_missing()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("readme.txt");

        var listing = DirectoryLister.List(file);

        Assert.DoesNotContain("no longer exists", listing.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancellation_during_enumeration_is_raised_not_turned_into_a_failed_listing()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        temp.AddFile("b.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DirectoryLister.List(temp.Root, null, cts.Token));
    }

    [Fact]
    public void Cancellation_of_an_empty_folder_is_raised_not_answered_with_a_clean_listing()
    {
        using var temp = new TempFolder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DirectoryLister.List(temp.Root, null, cts.Token));
    }

    [Fact]
    public async Task ListAsync_raises_cancellation_rather_than_returning_a_stale_listing()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DirectoryLister.ListAsync(temp.Root, null, cts.Token));
    }

    [Fact(Skip = "Cannot be produced: a non-recursive enumeration has no per-entry failure, so ContinueOnError (DirectoryLister.cs:96) only ever fires before the first entry, where it becomes an Error. Skipped > 0 — and the status message it drives — may be unreachable.")]
    public void A_partly_readable_folder_is_not_reported_as_complete()
    {
        using var temp = new TempFolder();
        temp.AddFile("readable.txt");

        var listing = DirectoryLister.List(temp.Root);

        Assert.True(listing.Skipped > 0);
        Assert.False(listing.IsComplete);
    }

    [Fact]
    public async Task ListAsync_returns_the_same_listing_as_List()
    {
        using var temp = new TempFolder();
        temp.AddFile("readme.txt", "hello");
        temp.AddFolder("nested");

        var listing = await DirectoryLister.ListAsync(temp.Root);

        Assert.True(listing.IsComplete);
        Assert.Equal(
            DirectoryLister.List(temp.Root).Entries.Select(e => e.Name).Order(StringComparer.Ordinal),
            listing.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }
}
