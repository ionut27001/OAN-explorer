using System.IO.Compression;
using Oan.App.ViewModels;
using Oan.Core.Archives;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

/// <summary>Compress and extract as the window runs them: through the selection, onto the disk.</summary>
public class ArchiveOpsTests
{
    private sealed class Host(string folder) : IFileOpsHost
    {
        public string? Said;

        public string CurrentPath => folder;
        public nint OwnerHandle => 0;
        public IReadOnlyList<EntryViewModel> Rows => [];
        public IReadOnlyList<EntryViewModel> Selection { get; set; } = [];
        public bool CanWriteHere => true;
        public Func<string, string, string?>? AskName => null;
        public Func<string, string?>? AskPassword => null;
        public Action<IReadOnlyList<EntryViewModel>>? Reselect => null;
        public string? Notice { set => Said = value ?? Said; }
        public string? Info { set => Said = value; }

        public Task ReloadAsync() => Task.CompletedTask;
        public void RefreshClipboardState() { }

        public void Select(params string[] paths) => Selection =
        [
            .. paths.Select(p => new EntryViewModel(new FileEntry
            {
                Name = Path.GetFileName(p),
                FullPath = p,
                Attributes = Directory.Exists(p) ? FileAttributes.Directory : FileAttributes.Normal,
            }, 16)),
        ];
    }

    [Fact]
    public async Task A_folder_survives_the_trip_into_a_zip_and_out_again()
    {
        using var temp = new TempFolder();
        var project = temp.AddFolder("project");
        Directory.CreateDirectory(Path.Combine(project, "empty"));
        File.WriteAllText(Path.Combine(project, "readme.txt"), "read me");

        var host = new Host(temp.Root);
        var ops = new ArchiveOps(host, new ArchiveViewModel());

        host.Select(project);
        await ops.CompressAsync();

        var zip = Path.Combine(temp.Root, "project.zip");
        Assert.True(File.Exists(zip), host.Said);

        host.Select(zip);
        Assert.Contains(ops.MenuItems(), i => i.Id == ArchiveOps.ExtractToFolderId);
        await ops.ExtractAsync(ownFolder: true);

        // "project" is taken by the original, so the extraction made a folder of its own.
        var copy = Path.Combine(temp.Root, "project (2)", "project");
        Assert.Equal("read me", File.ReadAllText(Path.Combine(copy, "readme.txt")));
        Assert.True(Directory.Exists(Path.Combine(copy, "empty")));
    }

    [Fact]
    public async Task Extract_here_leaves_what_is_already_there_alone_and_says_so()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Root, "notes.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("notes.txt").Open());
            writer.Write("from the zip");
        }
        temp.AddFile("notes.txt", "mine");

        var host = new Host(temp.Root);
        host.Select(zip);
        await new ArchiveOps(host, new ArchiveViewModel()).ExtractAsync(ownFolder: false);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(temp.Root, "notes.txt")));
        Assert.Contains("skipped", host.Said);
    }

    [Fact]
    public void The_menu_goes_by_the_name_and_never_opens_the_file()
    {
        using var temp = new TempFolder();
        var host = new Host(temp.Root);
        var ops = new ArchiveOps(host, new ArchiveViewModel());

        // Neither file exists: reading them would have said "not an archive".
        host.Select(Path.Combine(temp.Root, "backup.7z"), Path.Combine(temp.Root, "photo.jpg"));
        Assert.Contains(ops.MenuItems(), i => i.Label == "Extract to \"backup\\\"");

        // An image is mounted, not extracted.
        host.Select(Path.Combine(temp.Root, "linux.iso"));
        Assert.DoesNotContain(ops.MenuItems(), i => i.Id == ArchiveOps.ExtractHereId);
    }

    [Fact]
    public async Task A_file_only_named_like_an_archive_is_refused_when_extracted()
    {
        using var temp = new TempFolder();
        var fake = temp.AddFile("notes.zip", "just text");

        var host = new Host(temp.Root);
        host.Select(fake);
        await new ArchiveOps(host, new ArchiveViewModel()).ExtractAsync(ownFolder: true);

        Assert.Contains("not an archive", host.Said);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "notes")));
    }

    [Fact]
    public async Task Every_selected_archive_is_extracted_however_many()
    {
        using var temp = new TempFolder();
        var zips = Enumerable.Range(1, 25).Select(i => Zip(temp.Root, $"part{i}.zip", "a.txt")).ToArray();

        var host = new Host(temp.Root);
        host.Select(zips);
        await new ArchiveOps(host, new ArchiveViewModel()).ExtractAsync(ownFolder: true);

        Assert.All(zips, z => Assert.True(File.Exists(Path.Combine(temp.Root, Path.GetFileNameWithoutExtension(z), "a.txt")), z));
    }

    /// <summary>The name rule stands in for the bytes, so every kind the bytes can say needs a name the rule knows.</summary>
    [Fact]
    public void Every_kind_known_by_its_bytes_has_a_name_the_menu_knows()
    {
        using var temp = new TempFolder();
        var tar = new byte[512];
        "ustar"u8.CopyTo(tar.AsSpan(257));

        (string Name, byte[] Head, ArchiveKind Kind)[] samples =
        [
            ("a.zip", [0x50, 0x4B, 0x03, 0x04], ArchiveKind.Zip),
            ("a.jar", [0x50, 0x4B, 0x03, 0x04], ArchiveKind.Zip),
            ("a.cbz", [0x50, 0x4B, 0x03, 0x04], ArchiveKind.Zip),
            ("a.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], ArchiveKind.SevenZip),
            ("a.rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], ArchiveKind.Rar),
            ("a.cbr", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], ArchiveKind.Rar),
            ("a.xz", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], ArchiveKind.Xz),
            ("a.txz", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], ArchiveKind.Xz),
            ("a.gz", [0x1F, 0x8B], ArchiveKind.GZip),
            ("a.tgz", [0x1F, 0x8B], ArchiveKind.GZip),
            ("a.bz2", "BZh"u8.ToArray(), ArchiveKind.BZip2),
            ("a.tbz2", "BZh"u8.ToArray(), ArchiveKind.BZip2),
            ("a.tar", tar, ArchiveKind.Tar),
        ];

        foreach (var (name, head, kind) in samples)
        {
            var path = Path.Combine(temp.Root, name);
            File.WriteAllBytes(path, head);
            Assert.Equal(kind, ArchiveReader.Detect(path));
            Assert.True(ArchiveReader.IsArchiveName(path), name);
        }

        // A kind added to the sniffing without a sample here fails too.
        Assert.Equal(Enum.GetValues<ArchiveKind>().Except([ArchiveKind.None, ArchiveKind.Other]).Order(), samples.Select(s => s.Kind).Distinct().Order());
    }

    private static string Zip(string folder, string name, string entry)
    {
        var path = Path.Combine(folder, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
        writer.Write(entry);
        return path;
    }

    [Fact]
    public async Task A_second_zip_of_the_same_thing_gets_a_name_of_its_own()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("a.txt", "a");

        var host = new Host(temp.Root);
        host.Select(file);
        var ops = new ArchiveOps(host, new ArchiveViewModel());
        await ops.CompressAsync();
        await ops.CompressAsync();

        Assert.True(File.Exists(Path.Combine(temp.Root, "a.zip")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "a (2).zip")));
    }
}
