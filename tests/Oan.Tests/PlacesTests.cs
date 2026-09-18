using Oan.Core.FileSystem;
using Xunit;

namespace Oan.Tests;

public class PlacesTests
{
    [Fact]
    public void Quick_returns_named_folder_places()
    {
        var places = Places.Quick();

        Assert.NotEmpty(places);
        Assert.All(places, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(Path.IsPathRooted(p.Path), $"{p.Name} has an unrooted path: {p.Path}");
            Assert.Equal(PlaceKind.Folder, p.Kind);
        });
        Assert.Contains(places, p => p.Name == "Desktop");
    }

    [Fact]
    public void Quick_marks_a_place_available_exactly_when_its_path_exists()
    {
        var places = Places.Quick();

        Assert.All(places, p =>
            Assert.True(p.Available == Directory.Exists(p.Path),
                $"{p.Name} is marked Available={p.Available} but {p.Path} exists={Directory.Exists(p.Path)}."));
    }

    [Fact]
    public void Quick_lists_each_folder_once()
    {
        var paths = Places.Quick().Select(p => p.Path).ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Drives_includes_the_drive_the_tests_run_from()
    {
        var root = Path.GetPathRoot(Directory.GetCurrentDirectory())!;

        var drive = Assert.Single(Places.Drives(), d => string.Equals(d.Path, root, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(PlaceKind.Drive, drive.Kind);
        Assert.True(drive.Available);
        Assert.NotNull(drive.TotalBytes);
        Assert.NotNull(drive.FreeBytes);
        Assert.True(drive.TotalBytes > 0);
        Assert.InRange(drive.UsedFraction!.Value, 0.0, 1.0);
    }

    [Fact]
    public void A_drive_size_is_unknown_rather_than_zero()
    {
        var drives = Places.Drives();

        Assert.NotEmpty(drives);
        Assert.All(drives, d =>
        {
            Assert.True(d.TotalBytes is null or > 0, $"{d.Name} reports a total size of {d.TotalBytes} instead of an unknown size.");
            Assert.True(d.Available || d.TotalBytes is null, $"{d.Name} is not ready but reports a size of {d.TotalBytes}.");
            Assert.True(d.Available || d.FreeBytes is null, $"{d.Name} is not ready but reports {d.FreeBytes} bytes free.");
        });
    }

    [Fact]
    public void Used_fraction_is_unknown_when_a_size_is_unknown()
    {
        Assert.Null(new Place { Name = "A", Path = @"A:\", Kind = PlaceKind.Drive }.UsedFraction);
        Assert.Null(new Place { Name = "A", Path = @"A:\", Kind = PlaceKind.Drive, TotalBytes = 100 }.UsedFraction);
        Assert.Null(new Place { Name = "A", Path = @"A:\", Kind = PlaceKind.Drive, FreeBytes = 100 }.UsedFraction);
        Assert.Equal(0.75, new Place { Name = "A", Path = @"A:\", Kind = PlaceKind.Drive, TotalBytes = 100, FreeBytes = 25 }.UsedFraction);
    }

    [Fact]
    public void Drives_never_reports_a_drive_twice()
    {
        var paths = Places.Drives().Select(d => d.Path).ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
