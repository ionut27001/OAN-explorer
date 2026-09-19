using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Oan.Core.Mount;
using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// What the window does with a disk image. Everything except the two calls into virtdisk.dll is
/// driven here: no window, no synthetic input. The one test that must attach a real image says
/// so, and puts the machine back.
/// </summary>
[Collection(DiskBound.Name)]
public class MountsViewModelTests
{
    private const string Iso = @"C:\images\linux.iso";

    private sealed class Log
    {
        public List<string> Mounts { get; } = [];
        public List<string> Unmounts { get; } = [];
        public List<string> Opened { get; } = [];
        public List<string> Said { get; } = [];
        public string Here { get; set; } = "";
    }

    private static MountResult Attached(string path, string? letter) =>
        new() { IsoPath = path, DriveLetter = letter, PhysicalPath = @"\.\CdRom0" };

    private static (MountsViewModel Vm, Log Log) Fake(
        Func<string, MountResult>? mount = null, Func<string, string?>? unmount = null)
    {
        var log = new Log();
        var vm = new MountsViewModel(
            path => { log.Mounts.Add(path); return Task.FromResult((mount ?? (p => Attached(p, "F:")))(path)); },
            path => { log.Unmounts.Add(path); return Task.FromResult(unmount?.Invoke(path)); });

        vm.Bind(log.Opened.Add, log.Said.Add, () => log.Here);
        return (vm, log);
    }

    [Fact]
    public void An_image_is_known_by_its_extension_whatever_its_case()
    {
        Assert.True(MountsViewModel.IsImage(@"D:\a\UBUNTU.ISO"));
        Assert.True(MountsViewModel.IsImage(@"D:\a\card.img"));
        Assert.False(MountsViewModel.IsImage(@"D:\a\notes.txt"));
        Assert.False(MountsViewModel.IsImage(@"D:\a\folder"));
    }

    [Fact]
    public async Task Mounting_opens_the_drive_the_image_appeared_as()
    {
        var (vm, log) = Fake();

        await vm.OpenAsync(Iso);

        Assert.Equal([Iso], log.Mounts);
        Assert.Equal([@"F:\"], log.Opened);

        var image = Assert.Single(vm.Images);
        Assert.Equal("linux.iso", image.Name);
        Assert.Equal("F:", image.LetterText);
        Assert.True(vm.Any);
    }

    [Fact]
    public async Task A_mount_with_no_drive_letter_is_said_out_loud_and_shows_an_em_dash()
    {
        var (vm, log) = Fake(p => Attached(p, null));

        await vm.OpenAsync(Iso);

        // It is mounted, so it is listed — but there is nowhere to go, and a blank where the
        // letter belongs would read as a mount that quietly worked.
        Assert.Equal("—", Assert.Single(vm.Images).LetterText);
        Assert.False(Assert.Single(vm.Images).HasLetter);
        Assert.Empty(log.Opened);
        Assert.Contains("drive letter", Assert.Single(log.Said, s => s.StartsWith("linux", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_mount_says_why_and_lists_nothing()
    {
        var (vm, log) = Fake(p => MountResult.Refused(p, "not a disk image Windows can mount"));

        await vm.OpenAsync(Iso);

        Assert.Empty(vm.Images);
        Assert.False(vm.Any);
        Assert.Empty(log.Opened);
        Assert.Contains(log.Said, s => s.Contains("not a disk image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opening_an_image_that_is_already_ours_goes_to_its_drive_without_mounting_again()
    {
        var (vm, log) = Fake();
        await vm.OpenAsync(Iso);

        await vm.OpenAsync(Iso);

        Assert.Single(log.Mounts);
        Assert.Single(vm.Images);
        Assert.Equal([@"F:\", @"F:\"], log.Opened);
    }

    [Fact]
    public async Task The_menu_offers_Unmount_only_once_the_image_is_ours()
    {
        var (vm, _) = Fake();
        vm.Selected([Iso]);
        Assert.True(vm.ShowMount);
        Assert.Equal("Mount", vm.MountHeader);

        await vm.ToggleAsync();
        Assert.Equal("Unmount", vm.MountHeader);

        await vm.ToggleAsync();
        Assert.Equal("Mount", vm.MountHeader);
        Assert.Empty(vm.Images);
    }

    [Fact]
    public void Mount_is_offered_for_one_image_and_for_nothing_else()
    {
        var (vm, _) = Fake();

        vm.Selected([@"C:\a\notes.txt"]);
        Assert.False(vm.ShowMount);

        // Two images selected cannot say which one the item would act on.
        vm.Selected([Iso, @"C:\images\other.iso"]);
        Assert.False(vm.ShowMount);

        vm.Selected([]);
        Assert.False(vm.ShowMount);
    }

    [Fact]
    public async Task An_image_OAN_did_not_mount_is_never_ejected()
    {
        var (vm, log) = Fake();

        await vm.UnmountAsync(@"C:\images\bitdefender.iso");

        // The Core refuses to detach what it did not attach; the window must not offer it either,
        // and must not go asking.
        Assert.Empty(log.Unmounts);
        Assert.Empty(log.Said);
    }

    [Fact]
    public async Task Ejecting_the_drive_we_are_standing_in_goes_back_to_the_folder_the_image_came_from()
    {
        var (vm, log) = Fake();
        await vm.OpenAsync(Iso);
        log.Here = @"F:\boot";

        await vm.UnmountAsync(Iso);

        Assert.Empty(vm.Images);
        Assert.Equal([@"F:\", @"C:\images"], log.Opened);
    }

    [Fact]
    public async Task Ejecting_a_drive_we_are_not_in_leaves_the_list_where_it_is()
    {
        var (vm, log) = Fake();
        await vm.OpenAsync(Iso);
        log.Here = @"C:\Users";

        await vm.UnmountAsync(Iso);

        Assert.Equal([@"F:\"], log.Opened);
        Assert.Contains(log.Said, s => s.Contains("unmounted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_eject_that_was_refused_keeps_the_image_listed()
    {
        var (vm, log) = Fake(unmount: _ => "the volume is in use");
        await vm.OpenAsync(Iso);

        await vm.UnmountAsync(Iso);

        Assert.Single(vm.Images);
        Assert.Contains(log.Said, s => s.Contains("in use", StringComparison.Ordinal));
    }

    private sealed class NoOps : IFileOperations
    {
        public Task<OperationResult> RunAsync(OperationRequest request, nint owner, CancellationToken ct = default) =>
            Task.FromResult(OperationResult.Refused("not used in these tests"));
    }

    [Fact]
    public async Task Double_clicking_an_image_mounts_it_instead_of_running_the_shells_verb()
    {
        var (mounts, log) = Fake();
        var vm = new MainViewModel(new NoOps(), new Settings(), mounts);

        vm.Open(new EntryViewModel(new FileEntry { Name = "linux.iso", FullPath = Iso, Attributes = FileAttributes.Normal }, 16));

        // The two branches are exclusive: asking VirtDisk to attach it is the proof that the
        // default verb — the thing that hangs this machine's Explorer for hours — was not run.
        for (var i = 0; i < 100 && log.Mounts.Count == 0; i++) await Task.Delay(10);
        Assert.Equal([Iso], log.Mounts);

        vm.Shutdown();
    }

    [Fact]
    public async Task Closing_the_window_releases_the_image_it_mounted()
    {
        var before = Directory.GetLogicalDrives();
        var vm = new MainViewModel(new NoOps(), new Settings());

        await vm.Mounts.OpenAsync(TestImage.Iso);
        var image = Assert.Single(vm.Mounts.Images);
        Assert.True(image.HasLetter);
        Assert.Contains(image.Root, Directory.GetLogicalDrives());

        vm.Shutdown();

        // An image still attached after OAN exits is litter the user finds before we do.
        for (var i = 0; i < 100 && Directory.GetLogicalDrives().Contains(image.Root); i++) await Task.Delay(50);
        Assert.Equal(before, Directory.GetLogicalDrives());
    }
}
