using Oan.App.ViewModels;
using Oan.Core.FileSystem;
using Xunit;

namespace Oan.Tests;

[Collection(DiskBound.Name)]
public class PlaceViewModelTests
{
    const long Gb = 1024L * 1024 * 1024;

    static PlaceViewModel Drive(long? free, long? total, bool available = true) =>
        new(new Place
        {
            Name = "Local Disk (C:)", Path = @"C:\", Kind = PlaceKind.Drive,
            Available = available, FreeBytes = free, TotalBytes = total,
        });

    static PlaceViewModel Folder() =>
        new(new Place { Name = "Documents", Path = @"C:\Users\x\Documents", Kind = PlaceKind.Folder });

    [Fact]
    public void A_drive_says_how_much_of_how_much()
    {
        Assert.Equal("10 GB free of 100 GB", Drive(10 * Gb, 100 * Gb).CapacityText);
    }

    [Fact]
    public void A_drive_that_is_not_ready_is_an_em_dash_and_never_zero()
    {
        var drive = Drive(null, null, available: false);

        Assert.Equal("—", drive.CapacityText);
        Assert.False(drive.Available);
        Assert.Null(drive.UsedFraction);
    }

    [Fact]
    public void Half_an_answer_is_no_answer()
    {
        Assert.Equal("—", Drive(10 * Gb, null).CapacityText);
        Assert.Equal("—", Drive(null, 100 * Gb).CapacityText);
    }

    [Fact]
    public void A_full_drive_still_reads_as_a_measurement()
    {
        Assert.Equal("0 bytes free of 100 GB", Drive(0, 100 * Gb).CapacityText);
    }

    [Fact]
    public void A_folder_nobody_has_measured_shows_nothing_at_all()
    {
        Assert.Equal("", Folder().SizeText);
    }

    [Fact]
    public void A_folder_being_measured_says_so()
    {
        var folder = Folder();
        folder.Measuring();

        Assert.Equal("…", folder.SizeText);
    }

    [Fact]
    public void A_measured_folder_shows_its_size()
    {
        var folder = Folder();
        folder.Measured(new FolderSizeResult { Path = folder.Path, Bytes = 4096 });

        Assert.Equal("4 KB", folder.SizeText);
    }

    [Fact]
    public void An_empty_folder_and_an_unreadable_one_do_not_read_the_same()
    {
        var empty = Folder();
        empty.Measured(new FolderSizeResult { Path = empty.Path, Bytes = 0 });

        var unreadable = Folder();
        unreadable.Measured(FolderSizeResult.Unknown(unreadable.Path));

        Assert.Equal("0 bytes", empty.SizeText);
        Assert.Equal("—", unreadable.SizeText);
    }

    [Fact]
    public void A_total_that_is_missing_a_denied_branch_is_shown_as_a_floor()
    {
        var folder = Folder();
        folder.Measured(new FolderSizeResult { Path = folder.Path, Bytes = 4096, Skipped = 1 });

        Assert.Equal("≥ 4 KB", folder.SizeText);
        Assert.Contains("not counted", folder.Tip);
    }

    [Fact]
    public void A_junction_left_unfollowed_makes_the_number_a_floor_too()
    {
        var folder = Folder();
        folder.Measured(new FolderSizeResult { Path = folder.Path, Bytes = 4096, NotFollowed = 1 });

        Assert.Equal("≥ 4 KB", folder.SizeText);
    }

    [Fact]
    public void The_tooltip_says_which_of_the_states_this_is()
    {
        var folder = Folder();
        Assert.Contains("not measured", folder.Tip);

        folder.Measured(FolderSizeResult.Unknown(folder.Path));
        Assert.Contains("could not be read", folder.Tip);
    }

    /// <summary>
    /// The sidebar used to draw one folder glyph for every place, so Desktop, Downloads and
    /// Documents looked identical while Windows gives each its own. The icon has to be the
    /// shell's, and it has to be a different one for different places.
    /// </summary>
    [Fact]
    public void Different_places_get_different_icons_from_the_shell()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Assert.True(Directory.Exists(desktop) && Directory.Exists(pictures), "no profile to read");

        var one = Icon(desktop);
        var two = Icon(pictures);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(Bytes(one!), Bytes(two!));
    }

    private static System.Windows.Media.ImageSource? Icon(string path)
    {
        var place = new PlaceViewModel(new Place { Name = "x", Path = path, Kind = PlaceKind.Folder });
        _ = place.Icon;

        for (var i = 0; i < 200 && place.Icon is null; i++) Thread.Sleep(20);
        return place.Icon;
    }

    private static byte[] Bytes(System.Windows.Media.ImageSource source)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
            (System.Windows.Media.Imaging.BitmapSource)source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The bar turns red at the same point Explorer's does: under a tenth of the drive left.
    /// A drive that will not say how big it is has no fraction, and so no warning either.
    /// </summary>
    [Theory]
    [InlineData(100, 1000, false)]
    [InlineData(101, 1000, false)]
    [InlineData(99, 1000, true)]
    [InlineData(0, 1000, true)]
    public void The_drive_bar_warns_only_under_a_tenth_left(long free, long total, bool expected) =>
        Assert.Equal(expected, Drive(free, total).NearlyFull);

    [Fact]
    public void A_drive_that_knows_neither_number_does_not_warn()
    {
        Assert.False(Drive(null, null).NearlyFull);
        Assert.False(Drive(null, 1000).NearlyFull);
    }
}
