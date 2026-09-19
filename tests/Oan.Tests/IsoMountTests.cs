using Oan.Core.Mount;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Mounting cannot be faked: either virtdisk.dll attached a real image and Windows gave it a
/// letter, or it did not. So these run against a real 1.1 MB ISO.
///
/// Every test that attaches anything also asserts the machine is back where it started — the
/// same set of drive letters, nothing left in <see cref="IsoMount.Mounted"/>. A mount test that
/// leaks a mount is worse than no test.
/// </summary>
[Collection(DiskBound.Name)]
public class IsoMountTests
{
    private static string TestIso => TestImage.Iso;

    private static HashSet<string> Drives() => [.. Directory.GetLogicalDrives()];

    [Fact]
    public async Task Mounts_the_image_and_reports_the_letter_that_appeared()
    {
        var before = Drives();
        using var mount = new IsoMount();

        var result = await mount.MountAsync(TestIso);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.DriveLetter);

        // The letter it reported is the letter that turned up, not merely some letter.
        Assert.Equal([result.DriveLetter + @"\"], Drives().Except(before));
        Assert.NotEmpty(Directory.GetFileSystemEntries(result.DriveLetter + @"\"));
        Assert.Equal(result.DriveLetter, Assert.Single(mount.Mounted).DriveLetter);

        Assert.Null(await mount.UnmountAsync(TestIso));
        Assert.Empty(mount.Mounted);
        await DrivesReturnTo(before);
    }

    [Fact]
    public async Task A_file_that_is_not_a_disk_image_is_refused_with_a_reason()
    {
        using var temp = new TempFolder();
        var text = temp.AddFile("notes.txt", "this is not an image");
        var before = Drives();
        using var mount = new IsoMount();

        var result = await mount.MountAsync(text);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Null(result.DriveLetter);
        Assert.Empty(mount.Mounted);
        Assert.Equal(before, Drives());
    }

    [Fact]
    public async Task A_path_with_no_file_behind_it_is_refused()
    {
        using var temp = new TempFolder();
        using var mount = new IsoMount();

        var result = await mount.MountAsync(Path.Combine(temp.Root, "gone.iso"));

        Assert.False(result.Ok);
        Assert.Contains("no file", result.Error);
        Assert.Empty(mount.Mounted);
    }

    [Fact]
    public async Task Unmounting_an_image_OAN_never_mounted_is_refused()
    {
        using var mount = new IsoMount();

        var why = await mount.UnmountAsync(TestIso);

        Assert.False(string.IsNullOrWhiteSpace(why));
        Assert.Empty(mount.Mounted);
    }

    /// <summary>
    /// The case this machine makes real: a virtual disk is already attached that Bitdefender
    /// owns. Being attached is not permission to detach — only having attached it is.
    /// </summary>
    [Fact]
    public async Task An_image_someone_else_attached_stays_attached()
    {
        var before = Drives();
        using var owner = new IsoMount();
        var mounted = await owner.MountAsync(TestIso);
        Assert.True(mounted.Ok, mounted.Error);

        using (var stranger = new IsoMount())
        {
            Assert.False(string.IsNullOrWhiteSpace(await stranger.UnmountAsync(TestIso)));
        }

        // The stranger was disposed, which detaches everything it attached — it attached nothing.
        Assert.Contains(mounted.DriveLetter + @"\", Drives());
        Assert.Single(owner.Mounted);

        Assert.Null(await owner.UnmountAsync(TestIso));
        await DrivesReturnTo(before);
    }

    /// <summary>
    /// Measured: a second attach of the same image succeeds and takes a second drive letter.
    /// Windows will not stop it, so this is the one place that has to.
    /// </summary>
    [Fact]
    public async Task Mounting_the_same_image_twice_keeps_one_mount()
    {
        var before = Drives();
        using var mount = new IsoMount();

        var first = await mount.MountAsync(TestIso);
        var second = await mount.MountAsync(TestIso);

        Assert.True(first.Ok, first.Error);
        Assert.Equal(first.DriveLetter, second.DriveLetter);
        Assert.Single(mount.Mounted);
        Assert.Single(Drives().Except(before));

        Assert.Null(await mount.UnmountAsync(TestIso));
        await DrivesReturnTo(before);
    }

    [Fact]
    public async Task A_cancelled_mount_leaves_nothing_attached()
    {
        var before = Drives();
        using var mount = new IsoMount();
        // Measured: the whole mount takes about 20 ms on this machine, most of it inside the
        // attach. Five lands in the middle of it — during the attach, or in the wait for the
        // letter just after. Which of the two is a race, so both outcomes are asserted below.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));

        try
        {
            _ = await mount.MountAsync(TestIso, cancel.Token);
            // It beat the cancellation. Then it is an ordinary mount and must undo cleanly.
            Assert.Null(await mount.UnmountAsync(TestIso));
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Empty(mount.Mounted);
        await DrivesReturnTo(before);
    }

    [Fact]
    public async Task Disposing_ejects_what_is_still_mounted()
    {
        var before = Drives();
        var mount = new IsoMount();
        var result = await mount.MountAsync(TestIso);
        Assert.True(result.Ok, result.Error);

        mount.Dispose();

        Assert.Empty(mount.Mounted);
        await DrivesReturnTo(before);
    }

    /// <summary>The volume goes away a moment after the detach returns, not during it.</summary>
    private static async Task DrivesReturnTo(HashSet<string> expected)
    {
        for (var i = 0; i < 60 && !Drives().SetEquals(expected); i++) await Task.Delay(50);
        Assert.Equal(expected, Drives());
    }
}
