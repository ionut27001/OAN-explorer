using System.IO.Compression;
using System.Text;
using Oan.Core.Archives;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// An archive is input someone else wrote. These build the hostile cases by hand and prove the
/// guard by running it — reading the code is not evidence that it holds.
/// </summary>
public class ArchiveSafetyTests
{
    /// <summary>A zip with entry names written straight into the central directory, traversal and all.</summary>
    private static string Zip(string folder, params (string Name, string Body)[] entries)
    {
        var path = Path.Combine(folder, "hostile.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (name, body) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(body));
        }

        return path;
    }

    private static async Task<(ExtractResult Result, string Dest)> Extract(TempFolder temp, string archive)
    {
        var dest = Path.Combine(temp.Root, "out");
        Directory.CreateDirectory(dest);
        return (await ArchiveReader.ExtractAsync(archive, dest), dest);
    }

    [Theory]
    [InlineData(@"..\..\escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData(@"sub\..\..\escaped.txt")]
    public async Task An_entry_that_climbs_out_is_refused_and_writes_nothing(string name)
    {
        using var temp = new TempFolder();
        var archive = Zip(temp.Root, (name, "pwned"));

        var (result, dest) = await Extract(temp, archive);

        Assert.False(result.Ok);
        Assert.Contains(result.Refused, r => r.Reason == RefusalReason.UnsafePath);
        Assert.Empty(Directory.GetFiles(temp.Root, "escaped.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\oan-escaped.txt")]
    [InlineData(@"\Windows\Temp\oan-escaped.txt")]
    [InlineData("/etc/passwd")]
    public async Task An_entry_naming_an_absolute_path_is_refused(string name)
    {
        using var temp = new TempFolder();
        var archive = Zip(temp.Root, (name, "pwned"));

        var (result, _) = await Extract(temp, archive);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Refused);
        Assert.False(File.Exists(@"C:\Windows\Temp\oan-escaped.txt"));
    }

    [Fact]
    public async Task An_entry_naming_an_alternate_data_stream_is_refused()
    {
        using var temp = new TempFolder();
        // Not a file called "notes.txt:payload" — a stream hidden on notes.txt. It stays inside
        // the destination, which is why the boundary test alone would let it through.
        var archive = Zip(temp.Root, ("notes.txt:payload", "pwned"));

        var (result, dest) = await Extract(temp, archive);

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task A_destination_whose_name_is_a_prefix_of_another_is_not_a_way_out()
    {
        using var temp = new TempFolder();
        // C:\dest-evil starts with C:\dest. Without the separator in the comparison this escapes.
        var dest = Path.Combine(temp.Root, "dest");
        var neighbour = Path.Combine(temp.Root, "dest-evil");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(neighbour);

        var archive = Zip(temp.Root, (@"..\dest-evil\escaped.txt", "pwned"));
        var result = await ArchiveReader.ExtractAsync(archive, dest);

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFiles(neighbour));
    }

    [Fact]
    public async Task A_bomb_stops_at_the_cap_instead_of_filling_the_disk()
    {
        using var temp = new TempFolder();
        var archive = Zip(temp.Root, ("big.txt", new string('a', 400_000)));

        var dest = Path.Combine(temp.Root, "out");
        Directory.CreateDirectory(dest);
        var result = await ArchiveReader.ExtractAsync(archive, dest,
            limits: new ArchiveLimits { MaxTotalBytes = 1000 });

        Assert.False(result.Ok);
        Assert.True(new DirectoryInfo(dest).EnumerateFiles().Sum(f => f.Length) < 400_000,
            "the cap let the whole thing through");
    }

    [Fact]
    public async Task An_ordinary_archive_still_extracts()
    {
        using var temp = new TempFolder();
        var archive = Zip(temp.Root, ("readme.txt", "hello"), (@"docs\note.txt", "inside"));

        var (result, dest) = await Extract(temp, archive);

        Assert.True(result.Ok, result.Error ?? string.Join(", ", result.Refused.Select(r => r.Message)));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "readme.txt")));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(dest, "docs", "note.txt")));
    }

    [Fact]
    public async Task A_file_that_is_not_an_archive_is_an_error_not_an_empty_listing()
    {
        using var temp = new TempFolder();
        var notAnArchive = temp.AddFile("plain.txt");

        var listing = await ArchiveReader.ListAsync(notAnArchive);

        Assert.NotNull(listing.Error);
        Assert.False(listing.IsComplete);
    }

    [Fact]
    public void A_zip_is_recognised_by_its_bytes_not_its_name()
    {
        using var temp = new TempFolder();
        var archive = Zip(temp.Root, ("a.txt", "x"));
        var renamed = Path.Combine(temp.Root, "notazip.dat");
        File.Move(archive, renamed);

        Assert.Equal(ArchiveKind.Zip, ArchiveReader.Detect(renamed));
    }
}
