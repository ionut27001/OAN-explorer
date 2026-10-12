using Oan.App.ViewModels;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class StatusTextsTests
{
    private static EntryViewModel Row(string name, long? size) =>
        new(new FileEntry { Name = name, FullPath = @"C:\x\" + name, Attributes = size is null ? FileAttributes.Directory : FileAttributes.Normal, Size = size }, 16);

    private static DirectoryListing Listing(string path = @"C:\x", string? error = null, int skipped = 0) =>
        new() { Path = path, Entries = [], Error = error, Skipped = skipped };

    [Fact]
    public void A_selection_of_files_shows_its_total()
    {
        var text = StatusTexts.Status(new SearchViewModel(), false, @"C:\x", 3, 3, false, [Row("a", 1024), Row("b", 1024)]);

        Assert.Equal($"3 items  ·  2 selected  ·  {Format.Size(2048)}", text);
    }

    [Fact]
    public void A_selection_with_a_folder_in_it_shows_no_total()
    {
        var text = StatusTexts.Status(new SearchViewModel(), false, @"C:\x", 3, 3, false, [Row("a", 1024), Row("folder", null)]);

        Assert.Equal("3 items  ·  2 selected", text);
    }

    [Fact]
    public void A_filter_says_how_much_it_is_hiding()
    {
        Assert.Equal("1 of 9 items", StatusTexts.Status(new SearchViewModel(), false, @"C:\x", 1, 9, true, []));
    }

    [Fact]
    public void A_failed_listing_never_reads_as_an_empty_folder()
    {
        Assert.Equal("Access is denied.", StatusTexts.Empty(new SearchViewModel(), Listing(error: "Access is denied."), 0, false));
        Assert.Equal("This folder is empty.", StatusTexts.Empty(new SearchViewModel(), Listing(), 0, false));
        Assert.Equal("No items match the filter.", StatusTexts.Empty(new SearchViewModel(), Listing(), 0, true));
    }

    [Fact]
    public void Skipped_entries_are_said_out_loud()
    {
        Assert.Equal("2 item(s) in this folder could not be read", StatusTexts.Warning(new SearchViewModel(), Listing(skipped: 2)));
        Assert.Null(StatusTexts.Warning(new SearchViewModel(), Listing()));
    }
}
