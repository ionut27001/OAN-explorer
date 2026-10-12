using System.Runtime.InteropServices;
using Oan.Core.Links;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// Reparse points come in kinds that behave nothing alike, and the classification is where a file
/// manager gets them wrong. The classifier is a pure function of tag, directory-ness and target,
/// so most of it is tested exactly, with no machine in the way.
///
/// Two tests read the real filesystem: the junction every Windows install has, and an app
/// execution alias. A symbolic link cannot be created here without Developer Mode or
/// administrator, and this project never elevates — the test that wants one says which machine
/// fact it needed and does not pretend to have run.
/// </summary>
public class ReparseTests(ITestOutputHelper output)
{
    private const uint MountPoint = 0xA000_0003;
    private const uint Symlink = 0xA000_000C;
    private const uint AppExecLink = 0x8000_001B;
    private const uint Dedup = 0x8000_0013;
    private const uint Wof = 0x8000_0017;
    private const uint LxSymlink = 0xA000_001D;
    private const uint OneDriveOld = 0x8000_0021;

    [Fact]
    public void A_mount_point_tag_is_a_junction_when_it_names_a_directory() =>
        Assert.Equal(ReparseKind.Junction, Reparse.KindOf(MountPoint, isDirectory: true, @"C:\ProgramData"));

    [Fact]
    public void A_mount_point_tag_is_a_mounted_drive_when_it_names_a_volume() =>
        Assert.Equal(ReparseKind.VolumeMountPoint,
            Reparse.KindOf(MountPoint, isDirectory: true, @"\\?\Volume{a1b2c3d4-0000-0000-0000-000000000000}\"));

    [Fact]
    public void A_symlink_is_told_apart_by_what_it_is_attached_to()
    {
        Assert.Equal(ReparseKind.DirectorySymlink, Reparse.KindOf(Symlink, isDirectory: true, @"C:\somewhere"));
        Assert.Equal(ReparseKind.FileSymlink, Reparse.KindOf(Symlink, isDirectory: false, @"C:\somewhere\a.txt"));
    }

    /// <summary>
    /// Sixteen tags, one per registered cloud provider, and only the provider number differs. A
    /// classifier that matched the first one exactly would call OneDrive a placeholder and every
    /// other provider "unknown".
    /// </summary>
    [Theory]
    [InlineData(0x9000_001Au)]
    [InlineData(0x9000_101Au)]
    [InlineData(0x9000_801Au)]
    [InlineData(0x9000_F01Au)]
    public void Every_cloud_provider_tag_is_a_placeholder(uint tag) =>
        Assert.Equal(ReparseKind.CloudPlaceholder, Reparse.KindOf(tag, isDirectory: false, null));

    [Fact]
    public void The_tags_that_are_not_links_are_not_called_links()
    {
        Assert.Equal(ReparseKind.AppExecutionAlias, Reparse.KindOf(AppExecLink, false, null));
        Assert.Equal(ReparseKind.Deduplicated, Reparse.KindOf(Dedup, false, null));
        Assert.Equal(ReparseKind.Compressed, Reparse.KindOf(Wof, false, null));
        Assert.Equal(ReparseKind.WslLink, Reparse.KindOf(LxSymlink, false, null));
        Assert.Equal(ReparseKind.CloudPlaceholder, Reparse.KindOf(OneDriveOld, false, null));
    }

    [Fact]
    public void An_unknown_tag_is_named_by_its_number_rather_than_guessed_at() =>
        Assert.Equal(ReparseKind.Other, Reparse.KindOf(0x8000_00FFu, false, null));

    /// <summary>
    /// The three that redirect to somewhere else on the machine are refused, which is what
    /// SizeTree and FolderSize already do. The cloud placeholder is the one they should not
    /// refuse: it is the folder itself, and its children enumerate without downloading anything.
    /// </summary>
    [Fact]
    public void Only_the_kinds_that_point_elsewhere_stop_a_size_walk()
    {
        Assert.False(Reparse.ShouldDescend(ReparseKind.Junction));
        Assert.False(Reparse.ShouldDescend(ReparseKind.VolumeMountPoint));
        Assert.False(Reparse.ShouldDescend(ReparseKind.DirectorySymlink));
        Assert.True(Reparse.ShouldDescend(ReparseKind.CloudPlaceholder));
    }

    /// <summary>
    /// The one-line seam a size walk uses. An ordinary folder must come back true without the
    /// walk having to know anything about reparse points.
    /// </summary>
    [Fact]
    public void The_walk_descends_into_an_ordinary_folder_and_stops_at_a_junction()
    {
        using var temp = new TempFolder();
        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;

        Assert.True(Reparse.ShouldDescendInto(temp.AddFolder("plain")));
        Assert.True(Reparse.ShouldDescendInto(Path.Combine(temp.Root, "not-even-there")));
        Assert.False(Reparse.ShouldDescendInto(Path.Combine(users, "Default User")));
        Assert.False(Reparse.ShouldDescendInto(Path.Combine(users, "All Users")));
    }

    [Fact]
    public void A_junction_is_never_labelled_as_a_link()
    {
        Assert.Equal("Junction", Reparse.Label(ReparseKind.Junction));
        Assert.Equal("Mounted drive", Reparse.Label(ReparseKind.VolumeMountPoint));
        Assert.NotEqual(Reparse.Label(ReparseKind.Junction), Reparse.Label(ReparseKind.DirectorySymlink));
    }

    [Fact]
    public void An_ordinary_folder_has_no_tag_at_all()
    {
        using var temp = new TempFolder();

        Assert.Null(Reparse.Of(temp.AddFolder("plain")));
        Assert.Null(Reparse.Of(temp.AddFile("plain.txt")));
    }

    /// <summary>
    /// A drive root cannot be asked: FindFirstFileW wants an entry in a parent directory, and a
    /// root has no parent. Answering about the parent would be worse than refusing.
    /// </summary>
    [Fact]
    public void A_drive_root_is_refused_rather_than_answered_about_its_parent()
    {
        Assert.Null(Reparse.TagOf(@"C:\", out _));
        Assert.Null(Reparse.TagOf(@"C:", out _));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        using var temp = new TempFolder();
        var folder = temp.AddFolder("plain");

        Assert.Null(Reparse.TagOf(folder, out var direct));
        Assert.Null(Reparse.TagOf(folder + Path.DirectorySeparatorChar, out var trailing));
        Assert.True(direct);
        Assert.True(trailing);
    }

    /// <summary>
    /// <c>C:\Users</c> holds one of each, which is the point: <c>Default User</c> is a junction
    /// and <c>All Users</c> is a directory symbolic link, both put there by Windows setup, both
    /// showing the same attribute. Anything that reads the attribute alone calls them the same
    /// thing. (<c>FolderSize</c>'s own comment names All Users as a junction; <c>dir /al</c> on
    /// this machine says SYMLINKD, and the tag agrees.)
    /// </summary>
    [Fact]
    public void The_junction_and_the_symlink_Windows_itself_puts_side_by_side_are_told_apart()
    {
        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var junction = Path.Combine(users, "Default User");
        var symlink = Path.Combine(users, "All Users");

        Assert.True(Directory.Exists(junction), $"{junction} is missing, which no Windows install should be.");
        Assert.True(Directory.Exists(symlink), $"{symlink} is missing, which no Windows install should be.");

        var asJunction = Reparse.Of(junction);
        var asSymlink = Reparse.Of(symlink);

        output.WriteLine($"{junction}\ttag=0x{asJunction!.Tag:X8}\t{asJunction.Kind}\t-> {asJunction.Target}");
        output.WriteLine($"{symlink}\ttag=0x{asSymlink!.Tag:X8}\t{asSymlink.Kind}\t-> {asSymlink.Target}");

        Assert.Equal(ReparseKind.Junction, asJunction.Kind);
        Assert.Equal(Path.Combine(users, "Default"), asJunction.Target);
        Assert.True(asJunction.IsDirectory);

        Assert.Equal(ReparseKind.DirectorySymlink, asSymlink.Kind);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), asSymlink.Target);

        Assert.True(asJunction.TargetExists);
        Assert.True(asSymlink.TargetExists);
        Assert.False(Reparse.ShouldDescend(asJunction.Kind));
        Assert.False(Reparse.ShouldDescend(asSymlink.Kind));
    }

    [Fact]
    public void An_app_execution_alias_reads_as_an_alias_and_not_as_a_broken_file()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");

        var alias = Directory.Exists(apps)
            ? Directory.EnumerateFiles(apps, "*.exe").FirstOrDefault(f => Reparse.TagOf(f, out _) is not null)
            : null;

        Assert.True(alias is not null,
            $"No app execution alias under {apps}; this machine has no packaged apps with a command-line alias.");

        var info = Reparse.Of(alias);

        output.WriteLine($"{alias}\ttag=0x{info!.Tag:X8}\tkind={info.Kind}");
        Assert.Equal(ReparseKind.AppExecutionAlias, info.Kind);

        // It points at a package, not at a path, so there is nothing to say about a target.
        Assert.Null(info.TargetExists);
    }

    /// <summary>
    /// The one this machine cannot run. .NET passes SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE,
    /// so Developer Mode is enough — but if the machine refuses, the test says which right it
    /// wanted instead of going quietly green. Nothing here elevates.
    /// </summary>
    [Fact]
    public void A_directory_symlink_reads_as_a_folder_link()
    {
        using var temp = new TempFolder();
        var real = temp.AddFolder("real");
        var link = Path.Combine(temp.Root, "as-link");

        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"NOT RUN: this account cannot create a symbolic link — {e.Message}");
            Assert.Fail("Creating a symbolic link needs Developer Mode or administrator on this machine, " +
                        "and this suite never elevates. Turn Developer Mode on to run it.");
            return;
        }

        var info = Reparse.Of(link);

        Assert.NotNull(info);
        Assert.Equal(ReparseKind.DirectorySymlink, info.Kind);
        Assert.Equal(real, info.Target);
        Assert.True(info.TargetExists);
    }

    [Fact]
    public void A_file_symlink_reads_as_a_file_link()
    {
        using var temp = new TempFolder();
        var real = temp.AddFile("real.txt", "bytes");
        var link = Path.Combine(temp.Root, "as-link.txt");

        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"NOT RUN: this account cannot create a symbolic link — {e.Message}");
            Assert.Fail("Creating a symbolic link needs Developer Mode or administrator on this machine, " +
                        "and this suite never elevates. Turn Developer Mode on to run it.");
            return;
        }

        var info = Reparse.Of(link);

        Assert.NotNull(info);
        Assert.Equal(ReparseKind.FileSymlink, info.Kind);
        Assert.Equal(real, info.Target);
    }

    [Fact]
    public void A_dangling_symlink_is_named_and_reported_as_missing()
    {
        using var temp = new TempFolder();
        var real = temp.AddFolder("about-to-go");
        var link = Path.Combine(temp.Root, "dangling");

        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"NOT RUN: this account cannot create a symbolic link — {e.Message}");
            Assert.Fail("Creating a symbolic link needs Developer Mode or administrator on this machine, " +
                        "and this suite never elevates. Turn Developer Mode on to run it.");
            return;
        }

        Directory.Delete(real);
        var info = Reparse.Of(link);

        Assert.NotNull(info);
        Assert.Equal(real, info.Target);
        Assert.False(info.TargetExists);
    }

    // ---- cloud attributes: no syscall, straight off the listing ----

    [Theory]
    [InlineData(0x0040_0000u, CloudState.OnlineOnly)]      // RECALL_ON_DATA_ACCESS: dehydrated
    [InlineData(0x0004_0000u, CloudState.OnlineOnly)]      // RECALL_ON_OPEN: a placeholder folder
    [InlineData(0x0008_0000u, CloudState.AlwaysAvailable)] // PINNED
    [InlineData(0x0010_0000u, CloudState.OnThisDevice)]    // UNPINNED, and here
    [InlineData(0x0000_0020u, CloudState.NotCloud)]        // ARCHIVE
    public void Cloud_state_comes_off_the_attributes_alone(uint attributes, CloudState expected) =>
        Assert.Equal(expected, Cloud.StateOf((FileAttributes)attributes));

    /// <summary>
    /// Pinned and not yet downloaded is a real combination, and the honest answer is that the
    /// bytes are not here. A classifier that checked PINNED first would call it available.
    /// </summary>
    [Fact]
    public void Pinned_but_not_downloaded_is_still_online_only() =>
        Assert.Equal(CloudState.OnlineOnly, Cloud.StateOf((FileAttributes)(0x0008_0000u | 0x0040_0000u)));

    [Fact]
    public void The_one_question_worth_asking_before_reading_a_file()
    {
        Assert.True(Cloud.WouldDownload((FileAttributes)0x0040_0000u));
        Assert.False(Cloud.WouldDownload(FileAttributes.Archive));
        Assert.Null(Cloud.Label(CloudState.NotCloud));
    }

    // ---- hard links ----

    [Fact]
    public void An_ordinary_file_has_exactly_one_name()
    {
        using var temp = new TempFolder();

        Assert.Equal(1, HardLinks.CountOf(temp.AddFile("only.txt", "x")));
        Assert.False(HardLinks.IsShared(temp.AddFile("alone.txt", "x")));
    }

    [Fact]
    public void A_file_with_a_second_name_says_two_and_neither_name_is_the_original()
    {
        using var temp = new TempFolder();
        var first = temp.AddFile("first.txt", "shared bytes");
        var second = Path.Combine(temp.Root, "second.txt");

        Assert.True(CreateHardLinkW(second, first, 0),
            $"CreateHardLinkW failed with {Marshal.GetLastWin32Error()}; %TEMP% may not be NTFS.");

        Assert.Equal(2, HardLinks.CountOf(first));
        Assert.Equal(2, HardLinks.CountOf(second));
        Assert.True(HardLinks.IsShared(first));
        Assert.True(HardLinks.IsShared(second));
    }

    [Fact]
    public void A_file_that_is_not_there_is_null_and_not_zero()
    {
        using var temp = new TempFolder();

        Assert.Null(HardLinks.CountOf(Path.Combine(temp.Root, "gone.txt")));
        Assert.Null(HardLinks.IsShared(Path.Combine(temp.Root, "gone.txt")));
        Assert.Null(HardLinks.CountOf(""));
    }

    /// <summary>Test-only: the engine reads hard links and never makes them.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, nint security);
}
