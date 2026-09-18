using System.Diagnostics;
using Oan.Core.FileSystem;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class FolderSizeTests
{
    [Fact]
    public void Adds_up_every_file_beneath_the_folder()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt", new string('x', 10));
        temp.AddHiddenFile("hidden.txt", new string('x', 5));
        var nested = temp.AddFolder("nested");
        Write(nested, "b.txt", 100);
        Directory.CreateDirectory(Path.Combine(nested, "deeper"));
        Write(Path.Combine(nested, "deeper"), "c.txt", 1000);

        var result = new FolderSize().Measure(temp.Root);

        Assert.Equal(1115, result.Bytes);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.NotFollowed);
        Assert.False(result.IsFloor);
    }

    [Fact]
    public void An_empty_folder_measures_zero_rather_than_unknown()
    {
        using var temp = new TempFolder();

        var result = new FolderSize().Measure(temp.Root);

        Assert.Equal(0, result.Bytes);
        Assert.False(result.IsFloor);
        Assert.Equal("0 bytes", Format.Size(result.Bytes));
    }

    [Fact]
    public async Task A_missing_folder_measures_unknown_not_zero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "oan-missing-" + Guid.NewGuid().ToString("N"));
        var sizes = new FolderSize();

        Assert.Null(sizes.Measure(missing).Bytes);
        Assert.Null(await sizes.MeasureAsync(missing));
        Assert.Equal("—", Format.Size(sizes.Measure(missing).Bytes));
    }

    [Fact]
    public void A_file_passed_as_a_folder_measures_unknown()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("readme.txt", "hello");

        Assert.Null(new FolderSize().Measure(file).Bytes);
    }

    [Fact]
    public void A_folder_that_appears_later_is_not_answered_from_a_remembered_unknown()
    {
        using var temp = new TempFolder();
        var later = Path.Combine(temp.Root, "later");
        var sizes = new FolderSize();

        Assert.Null(sizes.Measure(later).Bytes);

        Directory.CreateDirectory(later);
        Write(later, "a.txt", 7);

        Assert.Equal(7, sizes.Measure(later).Bytes);
    }

    [Fact]
    public async Task MeasureAsync_reports_the_same_total_as_Measure()
    {
        using var temp = new TempFolder();
        Write(temp.Root, "a.txt", 42);

        Assert.Equal(42, await new FolderSize().MeasureAsync(temp.Root));
    }

    [Fact]
    public void A_junction_is_not_followed_and_is_counted()
    {
        using var temp = new TempFolder();
        var real = temp.AddFolder("real");
        Write(real, "a.txt", 100);
        var link = Path.Combine(temp.Root, "link");
        Assert.True(MakeJunction(link, real),
            "mklink /J did not create a junction, so this test would prove nothing.");

        try
        {
            var result = new FolderSize().Measure(temp.Root);

            Assert.Equal(100, result.Bytes);
            Assert.Equal(1, result.NotFollowed);
            Assert.True(result.IsFloor);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void An_unreadable_subfolder_leaves_a_floor_rather_than_a_total()
    {
        using var temp = new TempFolder();
        Write(temp.AddFolder("open"), "a.txt", 50);
        var locked = temp.AddFolder("locked");
        Write(locked, "b.txt", 500);
        Assert.True(Icacls($"\"{locked}\" /deny \"{Environment.UserName}\":(OI)(CI)(RX)"),
            "icacls could not deny access, so the skipped-entry path would go untested.");

        try
        {
            var result = new FolderSize().Measure(temp.Root);

            Assert.Equal(50, result.Bytes);
            Assert.True(result.Skipped > 0, "the unreadable folder was not counted as skipped");
            Assert.True(result.IsFloor);
        }
        finally
        {
            Assert.True(Icacls($"\"{locked}\" /remove:d \"{Environment.UserName}\""));
        }
    }

    [Fact]
    public void A_folder_that_cannot_be_opened_at_all_measures_unknown_rather_than_zero()
    {
        using var temp = new TempFolder();
        var locked = temp.AddFolder("locked");
        Write(locked, "b.txt", 500);
        Assert.True(Icacls($"\"{locked}\" /deny \"{Environment.UserName}\":(OI)(CI)(RX)"),
            "icacls could not deny access, so the fatal-open path would go untested.");

        try
        {
            Assert.Null(new FolderSize().Measure(locked).Bytes);
        }
        finally
        {
            Assert.True(Icacls($"\"{locked}\" /remove:d \"{Environment.UserName}\""));
        }
    }

    [Fact]
    public void Cancellation_is_raised_rather_than_returning_a_partial_total()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt", "hello");
        temp.AddFile("b.txt", "hello");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new FolderSize().Measure(temp.Root, cts.Token));
    }

    [Fact]
    public void Cancellation_of_an_empty_folder_is_raised_before_the_walk_begins()
    {
        using var temp = new TempFolder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new FolderSize().Measure(temp.Root, cts.Token));
    }

    [Fact]
    public async Task MeasureAsync_raises_cancellation_rather_than_answering_unknown()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt", "hello");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FolderSize().MeasureAsync(temp.Root, cts.Token));
    }

    [Fact]
    public void A_second_measurement_is_served_from_the_cache_instead_of_walking_again()
    {
        using var temp = new TempFolder();
        Write(temp.Root, "a.txt", 10);
        var sizes = new FolderSize();

        var first = sizes.Measure(temp.Root);
        Write(temp.Root, "b.txt", 90);
        var second = sizes.Measure(temp.Root);

        Assert.Equal(10, second.Bytes);
        Assert.Equal(first.MeasuredAt, second.MeasuredAt);
        Assert.Equal(10, sizes.Cached(temp.Root)?.Bytes);
    }

    [Fact]
    public void A_stale_measurement_is_taken_again()
    {
        using var temp = new TempFolder();
        Write(temp.Root, "a.txt", 10);
        var sizes = new FolderSize(TimeSpan.Zero);

        Assert.Equal(10, sizes.Measure(temp.Root).Bytes);
        Write(temp.Root, "b.txt", 90);

        Assert.Equal(100, sizes.Measure(temp.Root).Bytes);
        Assert.Null(sizes.Cached(temp.Root));
    }

    [Fact]
    public void Cached_reports_nothing_for_a_folder_that_was_never_measured()
    {
        using var temp = new TempFolder();

        Assert.Null(new FolderSize().Cached(temp.Root));
    }

    [Fact]
    public void The_cache_does_not_grow_without_bound()
    {
        const int capacity = 64; // FolderSize.Capacity
        using var temp = new TempFolder();
        var sizes = new FolderSize();
        var oldest = temp.AddFolder("f0");
        sizes.Measure(oldest);
        Assert.NotNull(sizes.Cached(oldest));

        for (var i = 1; i <= capacity; i++)
            sizes.Measure(temp.AddFolder($"f{i}"));

        Assert.Null(sizes.Cached(oldest));
        Assert.NotNull(sizes.Cached(Path.Combine(temp.Root, $"f{capacity}")));
    }

    [Fact]
    public void A_path_written_differently_hits_the_same_cache_entry()
    {
        using var temp = new TempFolder();
        Write(temp.Root, "a.txt", 10);
        var sizes = new FolderSize();

        sizes.Measure(temp.Root);

        Assert.Equal(10, sizes.Cached(temp.Root + Path.DirectorySeparatorChar)?.Bytes);
        Assert.Equal(10, sizes.Cached(Path.Combine(temp.Root, "sub", ".."))?.Bytes);
    }

    static void Write(string folder, string name, int bytes) =>
        File.WriteAllText(Path.Combine(folder, name), new string('x', bytes));

    static bool MakeJunction(string link, string target) =>
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") && Directory.Exists(link);

    static bool Icacls(string arguments) => Run("icacls.exe", arguments);

    static bool Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
