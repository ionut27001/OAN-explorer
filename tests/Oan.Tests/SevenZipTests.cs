using Xunit;
using System.Diagnostics;
using Oan.Core.Archives;

namespace Oan.Tests;

/// <summary>
/// The 7z.dll path, against archives made by the 7-Zip installed here. OAN ships no 7-Zip of
/// its own, so on a machine without one these have nothing to test and pass empty.
/// </summary>
public class SevenZipTests
{
    private static readonly string? SevenZipExe = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
    }.FirstOrDefault(File.Exists);

    /// <summary>Two files and a folder, packed with whatever switches the case is about.</summary>
    private static string Pack(TempFolder temp, string name, params string[] switches)
    {
        var source = Path.Combine(temp.Root, "src");
        Directory.CreateDirectory(Path.Combine(source, "docs"));
        File.WriteAllText(Path.Combine(source, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(source, "docs", "deep.txt"), new string('x', 50_000));

        var archive = Path.Combine(temp.Root, name);
        var start = new ProcessStartInfo(SevenZipExe!) { WorkingDirectory = source, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in (string[])["a", archive, "*", "-r", .. switches]) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return archive;
    }

    [Theory]
    [InlineData("plain.7z")]
    [InlineData("solid.7z", "-ms=on", "-m0=lzma2")]
    [InlineData("bcj2.7z", "-m0=bcj2", "-m1=lzma")]
    [InlineData("ppmd.7z", "-m0=ppmd")]
    [InlineData("image.wim")]
    public async Task Lists_and_extracts_what_7zip_made(string name, params string[] switches)
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        var archive = Pack(temp, name, switches);

        var listing = await ArchiveReader.ListAsync(archive);
        Assert.Null(listing.Error);
        Assert.Contains(listing.Entries, e => e.FullName == "docs/deep.txt" && e.Size == 50_000);
        Assert.Contains(listing.Entries, e => e.FullName == "docs" && e.IsDirectory);

        var dest = Path.Combine(temp.Root, "out");
        var result = await ArchiveReader.ExtractAsync(archive, dest);
        Assert.True(result.Ok, result.Error ?? string.Join("; ", result.Refused.Select(r => r.Message)));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "hello.txt")));
        Assert.Equal(50_000, new FileInfo(Path.Combine(dest, "docs", "deep.txt")).Length);
    }

    [Fact]
    public async Task One_entry_by_name_is_the_only_one_written()
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        var archive = Pack(temp, "solid.7z", "-ms=on");

        var dest = Path.Combine(temp.Root, "out");
        var result = await ArchiveReader.ExtractAsync(archive, dest, ["docs/deep.txt"]);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Extracted);
        Assert.False(File.Exists(Path.Combine(dest, "hello.txt")));
    }

    [Fact]
    public async Task Encrypted_names_ask_for_a_password_and_open_with_it()
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        var archive = Pack(temp, "locked.7z", "-psecret", "-mhe=on");

        var locked = await ArchiveReader.ListAsync(archive);
        Assert.True(locked.NeedsPassword);
        Assert.Empty(locked.Entries);

        var wrong = await ArchiveReader.ListAsync(archive, password: "nope");
        Assert.True(wrong.NeedsPassword);

        var dest = Path.Combine(temp.Root, "out");
        var result = await ArchiveReader.ExtractAsync(archive, dest, password: "secret");
        Assert.True(result.Ok, result.Error);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "hello.txt")));
    }

    [Theory]
    [InlineData("locked.7z")]
    [InlineData("locked.zip")]
    public async Task Encrypted_bytes_are_refused_without_a_password_and_written_with_it(string name)
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        var archive = Pack(temp, name, "-psecret");

        var refused = await ArchiveReader.ExtractAsync(archive, Path.Combine(temp.Root, "no"));
        Assert.True(refused.NeedsPassword, $"{refused.Error} | {refused.Extracted} | {string.Join("; ", refused.Refused.Select(r => r.Reason + " " + r.Message))}");
        Assert.False(File.Exists(Path.Combine(temp.Root, "no", "hello.txt")));

        var wrong = await ArchiveReader.ExtractAsync(archive, Path.Combine(temp.Root, "wrong"), password: "nope");
        Assert.False(wrong.Ok, $"{wrong.Extracted} written, {wrong.Bytes} bytes, {File.Exists(Path.Combine(temp.Root, "wrong", "hello.txt")) && File.ReadAllText(Path.Combine(temp.Root, "wrong", "hello.txt")) == "hello"}");
        Assert.False(File.Exists(Path.Combine(temp.Root, "wrong", "docs", "deep.txt")));

        var dest = Path.Combine(temp.Root, "yes");
        var result = await ArchiveReader.ExtractAsync(archive, dest, password: "secret");
        Assert.True(result.Ok, result.Error ?? string.Join("; ", result.Refused.Select(r => r.Message)));
        Assert.Equal(50_000, new FileInfo(Path.Combine(dest, "docs", "deep.txt")).Length);
    }

    [Fact]
    public async Task A_split_archive_is_read_across_its_volumes()
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(temp.Root, "src")).FullName, "noise.bin"),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(300_000));
        Pack(temp, "split.7z", "-v100k");

        var first = Path.Combine(temp.Root, "split.7z.001");
        Assert.NotEqual(ArchiveKind.None, ArchiveReader.Detect(first));

        var dest = Path.Combine(temp.Root, "out");
        var result = await ArchiveReader.ExtractAsync(first, dest);
        Assert.True(result.Ok, result.Error ?? string.Join("; ", result.Refused.Select(r => r.Message)));
        Assert.Equal(300_000, new FileInfo(Path.Combine(dest, "noise.bin")).Length);
    }

    [Fact]
    public async Task The_cap_refuses_what_7zip_would_write_past_it()
    {
        if (SevenZipExe is null) return;
        using var temp = new TempFolder();
        var archive = Pack(temp, "big.7z");

        var dest = Path.Combine(temp.Root, "out");
        var limits = new ArchiveLimits { MaxTotalBytes = 10_000, MinTotalBytes = 10_000 };
        var result = await ArchiveReader.ExtractAsync(archive, dest, limits: limits);

        Assert.Contains(result.Refused, r => r.Reason is RefusalReason.TooLarge);
        Assert.False(File.Exists(Path.Combine(dest, "docs", "deep.txt")));
    }
}
