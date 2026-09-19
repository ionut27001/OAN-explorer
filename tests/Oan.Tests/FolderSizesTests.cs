using System.IO;
using Oan.App.ViewModels;
using Oan.Core.FileSystem;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The Size column for folders. The states matter more than the numbers: measured on this machine,
/// ten of the user profile's sixty-five folders cannot be read at all and four more can only be
/// given a floor, so "blank", "—" and "≥ 1.2 GB" each have to mean their own thing.
/// </summary>
public class FolderSizesTests
{
    [Fact]
    public void A_file_shows_its_own_size_without_anyone_measuring_anything()
    {
        var row = Row("notes.txt", FileAttributes.Normal, size: 2048);

        Assert.Equal(Format.Size(2048), row.SizeText);
        Assert.Equal(2048, row.SortSize);
    }

    [Fact]
    public void A_folder_nobody_has_asked_about_is_blank_not_a_dash_and_not_a_zero()
    {
        var row = Row("stuff", FileAttributes.Directory);

        Assert.Equal("", row.SizeText);
        Assert.Null(row.SortSize);
    }

    [Fact]
    public void While_it_is_being_walked_it_says_so()
    {
        var row = Row("stuff", FileAttributes.Directory);

        row.Measuring();

        Assert.Equal("…", row.SizeText);
        Assert.Null(row.SortSize);
    }

    [Fact]
    public void A_partial_answer_is_marked_as_a_floor_rather_than_passed_off_as_a_total()
    {
        var row = Row("stuff", FileAttributes.Directory);

        row.Measured(new FolderSizeResult { Path = @"C:\stuff", Bytes = 4096, NotFollowed = 3 });

        Assert.StartsWith("≥", row.SizeText);
        Assert.Contains("At least", row.SizeTip);
        Assert.Contains("3 item(s) were not counted", row.SizeTip);
    }

    [Fact]
    public void A_folder_that_could_not_be_read_is_a_dash_and_says_why()
    {
        var row = Row("stuff", FileAttributes.Directory);

        row.Measured(FolderSizeResult.Unknown(@"C:\stuff"));

        Assert.Equal("—", row.SizeText);
        Assert.Contains("could not be read", row.SizeTip);
        Assert.Null(row.SortSize);
    }

    [Fact]
    public void A_folder_inside_an_archive_is_unknowable_rather_than_merely_unmeasured()
    {
        var row = new EntryViewModel(
            new FileEntry { Name = "docs", FullPath = "docs", Attributes = FileAttributes.Directory },
            16, measurable: false);

        Assert.Equal("—", row.SizeText);
    }

    [Fact]
    public void Switching_the_column_off_puts_the_rows_back_to_knowing_nothing()
    {
        using var temp = new TempFolder();
        temp.AddFolder("inner");
        File.WriteAllText(Path.Combine(temp.Root, "inner", "a.bin"), new string('x', 1000));

        using var sizes = new FolderSizesViewModel();
        var row = Row("inner", FileAttributes.Directory, path: Path.Combine(temp.Root, "inner"));
        sizes.Show([row]);

        sizes.On = true;
        Assert.True(Settles(() => row.SortSize == 1000), $"never measured, showed {row.SizeText}");

        sizes.On = false;
        Assert.Equal("", row.SizeText);
        Assert.Null(row.SortSize);
    }

    [Fact]
    public void A_size_already_known_is_painted_without_the_column_being_on()
    {
        using var temp = new TempFolder();
        temp.AddFolder("inner");
        File.WriteAllText(Path.Combine(temp.Root, "inner", "a.bin"), new string('x', 500));
        var path = Path.Combine(temp.Root, "inner");

        using var sizes = new FolderSizesViewModel();
        var first = Row("inner", FileAttributes.Directory, path: path);
        sizes.Show([first]);
        sizes.On = true;
        Assert.True(Settles(() => first.SortSize == 500), "never measured");

        // Navigating away and back: same queue, same cache, a fresh row, and the switch is off.
        sizes.On = false;
        var again = Row("inner", FileAttributes.Directory, path: path);
        sizes.Show([again]);

        Assert.Equal(500, again.SortSize);
    }

    [Fact]
    public void Sorting_by_size_uses_what_was_measured_not_the_folder_flag()
    {
        var small = Row("small", FileAttributes.Directory);
        var big = Row("big", FileAttributes.Directory);
        small.Measured(new FolderSizeResult { Path = @"C:\small", Bytes = 10 });
        big.Measured(new FolderSizeResult { Path = @"C:\big", Bytes = 9000 });

        var sort = new SortViewModel();
        sort.By(SortKey.Size);

        var rows = new[] { big, small };
        Array.Sort(rows, sort.Compare);

        Assert.Equal(["small", "big"], rows.Select(r => r.Name));
    }

    private static EntryViewModel Row(string name, FileAttributes attributes, long? size = null, string? path = null) =>
        new(new FileEntry
        {
            Name = name,
            FullPath = path ?? Path.Combine(@"C:\nowhere", name),
            Attributes = attributes,
            Size = size,
        }, 16);

    private static bool Settles(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) Thread.Sleep(20);
        return done();
    }
}
