namespace Oan.Core.Archives;

/// <summary>Reads what is inside an archive, and writes it out again, without shelling out. The whole file is attacker-controlled input:
/// see <see cref="SafeEntryPath"/> and <see cref="ArchiveExtraction"/>.</summary>
public static class ArchiveReader
{
    /// <summary>What the file is, by the bytes at the front of it, not by the extension.</summary>
    public static ArchiveKind Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ArchiveKind.None;

        try
        {
            using var file = File.OpenRead(path);

            // 262 because tar's magic is not at the front: "ustar" sits at offset 257.
            Span<byte> head = stackalloc byte[262];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var kind = Sniff(head[..read]);
            return kind is ArchiveKind.None && SevenZipOnly.Contains(Path.GetExtension(path)) && SevenZipLibrary.Instance is not null
                ? ArchiveKind.Other
                : kind;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ArchiveKind.None;
        }
    }

    /// <summary>What <see cref="Detect"/> would most likely say, from the name alone: for a menu or a command's state, which must not wait on the disk.
    /// Extracting still reads the bytes, and says so when the name was wrong.</summary>
    public static bool IsArchiveName(string path)
    {
        var extension = Path.GetExtension(path);
        return Named.Contains(extension) || (SevenZipOnly.Contains(extension) && SevenZipLibrary.Instance is not null);
    }

    /// <summary>The names the kinds <see cref="Sniff"/> knows go by. Documents that are zips inside (.docx, .epub) are left out.</summary>
    private static readonly HashSet<string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".jar", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz", ".bz2", ".tbz", ".tbz2", ".tb2", ".xz", ".txz",
        ".cbz", ".cbr", ".cb7", ".cbt",
    };

    /// <summary>What only 7-Zip reads, and OAN steps into on a double-click. A list on purpose: the DLL also "opens" .exe, .msi and .doc,
    /// which must keep meaning run, install and edit. Disk images are left to the mount.</summary>
    private static readonly HashSet<string> SevenZipOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ".001", ".arj", ".cab", ".chm", ".cpio", ".deb", ".dmg", ".esd", ".lha", ".lzh", ".lzma",
        ".rpm", ".squashfs", ".swm", ".wim", ".xar", ".z", ".zst", ".tzst",
    };

    /// <summary>The 7z.dll in use, or null when there is none and the managed readers work alone.</summary>
    public static string? SevenZipPath => SevenZipLibrary.Instance?.Location;

    /// <summary>For when 7-Zip was installed while OAN was running.</summary>
    public static void FindSevenZipAgain() => SevenZipLibrary.LookAgain();

    public static Task<ArchiveListing> ListAsync(string path, ArchiveLimits? limits = null,
        string? password = null, CancellationToken ct = default) =>
        Task.Run(() => List(path, limits ?? new ArchiveLimits(), password, ct), ct);

    /// <summary>Writes <paramref name="entries"/>, or everything when null, into <paramref name="destination"/>.
    /// Names match exactly as <see cref="ArchiveListing"/> reports them; a folder does not imply what is under it.</summary>
    public static Task<ExtractResult> ExtractAsync(string path, string destination,
        IReadOnlyList<string>? entries = null, ArchiveLimits? limits = null,
        string? password = null, CancellationToken ct = default) =>
        Task.Run(() => Extract(path, destination, entries, limits ?? new ArchiveLimits(), password, ct), ct);

    private static ArchiveListing List(string path, ArchiveLimits limits, string? password, CancellationToken ct)
    {
        var kind = Detect(path);
        if (kind is ArchiveKind.None) return ArchiveListing.Failed(path, NotAnArchive(path));

        ct.ThrowIfCancellationRequested();

        var entries = new List<ArchiveEntry>();
        string? error;
        var locked = false;
        try
        {
            error = ArchiveSource.Open(path, kind, password, source => Collect(source, entries, limits.MaxEntries, ct));
        }
        catch (PasswordNeededException e)
        {
            (error, locked) = (e.Message, true);
        }
        catch (Exception e) when (ArchiveSource.Recoverable(e))
        {
            // Whatever was read before it went wrong is kept; Error says this is not all of it.
            error = Unreadable(e);
        }

        return new ArchiveListing { Path = path, Kind = kind, Entries = entries, Error = error, NeedsPassword = locked };
    }

    private static string? Collect(IEnumerable<SourceEntry> source, List<ArchiveEntry> into, int max, CancellationToken ct)
    {
        foreach (var entry in source)
        {
            ct.ThrowIfCancellationRequested();

            if (into.Count >= max)
                return $"This archive lists more than {max:N0} entries; the first {max:N0} are shown.";

            into.Add(new ArchiveEntry
            {
                FullName = entry.Name,
                Name = Leaf(entry.Name),
                IsDirectory = entry.IsDirectory,
                Size = entry.Size,
                Compressed = entry.Compressed,
                Modified = entry.Modified,
                Encrypted = entry.Encrypted,
            });
        }
        return null;
    }

    private static ExtractResult Extract(string path, string destination, IReadOnlyList<string>? entries,
        ArchiveLimits limits, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return ExtractResult.Failed(destination ?? "", "No destination folder was given.");

        var kind = Detect(path);
        if (kind is ArchiveKind.None) return ExtractResult.Failed(destination, NotAnArchive(path));

        string root;
        try
        {
            root = Path.GetFullPath(destination);
            Directory.CreateDirectory(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ExtractResult.Failed(destination, "This folder could not be used: " + e.Message);
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            var run = new ArchiveExtraction(root, entries, limits, new FileInfo(path).Length);
            ArchiveSource.Extract(path, kind, password, run, ct);
            return run.Result();
        }
        catch (PasswordNeededException e)
        {
            return ExtractResult.Failed(root, e.Message) with { NeedsPassword = true };
        }
        catch (Exception e) when (ArchiveSource.Recoverable(e))
        {
            return ExtractResult.Failed(root, Unreadable(e));
        }
    }

    private static ArchiveKind Sniff(ReadOnlySpan<byte> head)
    {
        if (At(head, 0, [0x50, 0x4B, 0x03, 0x04]) || At(head, 0, [0x50, 0x4B, 0x05, 0x06]) || At(head, 0, [0x50, 0x4B, 0x07, 0x08]))
            return ArchiveKind.Zip;
        if (At(head, 0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return ArchiveKind.SevenZip;
        if (At(head, 0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07])) return ArchiveKind.Rar;
        if (At(head, 0, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])) return ArchiveKind.Xz;
        if (At(head, 0, [0x1F, 0x8B])) return ArchiveKind.GZip;
        if (At(head, 0, "BZh"u8)) return ArchiveKind.BZip2;
        if (At(head, 257, "ustar"u8)) return ArchiveKind.Tar;
        return ArchiveKind.None;
    }

    private static bool At(ReadOnlySpan<byte> head, int offset, ReadOnlySpan<byte> magic) =>
        head.Length >= offset + magic.Length && head.Slice(offset, magic.Length).SequenceEqual(magic);

    private static string Leaf(string name)
    {
        var trimmed = name.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private static string NotAnArchive(string path) =>
        File.Exists(path) ? "This file is not an archive OAN can read." : "There is no file at that path.";

    private static string Unreadable(Exception e) => "This archive could not be read: " + e.Message;
}
