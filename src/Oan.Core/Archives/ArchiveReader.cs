namespace Oan.Core.Archives;

/// <summary>
/// Reads what is inside an archive, and writes it out again.
///
/// A file manager that cannot look inside a zip is leaning on somebody else's program to do its
/// job — which on this machine means a shell extension, in Explorer's process, hanging it.
/// Nothing here shells out: zip comes from the framework, the rest from a managed library.
///
/// The whole file is attacker-controlled input. Nothing an entry says about where it wants to
/// go is believed; see <see cref="SafeEntryPath"/> and <see cref="ArchiveExtraction"/>.
/// </summary>
public static class ArchiveReader
{
    /// <summary>
    /// What the file is, by the bytes at the front of it. Not by the extension: renaming an
    /// installer to .zip does not make it one, and naming a zip .dat does not hide it.
    /// </summary>
    public static ArchiveKind Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ArchiveKind.None;

        try
        {
            using var file = File.OpenRead(path);

            // 262 because tar's magic is not at the front: "ustar" sits at offset 257.
            Span<byte> head = stackalloc byte[262];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Sniff(head[..read]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ArchiveKind.None;
        }
    }

    public static Task<ArchiveListing> ListAsync(string path, ArchiveLimits? limits = null, CancellationToken ct = default) =>
        Task.Run(() => List(path, limits ?? new ArchiveLimits(), ct), ct);

    /// <summary>
    /// Writes <paramref name="entries"/> — or everything, when it is null — into
    /// <paramref name="destination"/>. Names are matched exactly as
    /// <see cref="ArchiveListing"/> reports them; naming a folder does not imply what is under it.
    /// </summary>
    public static Task<ExtractResult> ExtractAsync(string path, string destination,
        IReadOnlyList<string>? entries = null, ArchiveLimits? limits = null, CancellationToken ct = default) =>
        Task.Run(() => Extract(path, destination, entries, limits ?? new ArchiveLimits(), ct), ct);

    private static ArchiveListing List(string path, ArchiveLimits limits, CancellationToken ct)
    {
        var kind = Detect(path);
        if (kind is ArchiveKind.None) return ArchiveListing.Failed(path, NotAnArchive(path));

        ct.ThrowIfCancellationRequested();

        var entries = new List<ArchiveEntry>();
        string? error;
        try
        {
            error = ArchiveSource.Open(path, kind, source => Collect(source, entries, limits.MaxEntries, ct));
        }
        catch (Exception e) when (ArchiveSource.Recoverable(e))
        {
            // Whatever was read before it went wrong is kept: a truncated archive shows what it
            // had and still says, through Error, that this is not all of it.
            error = Unreadable(e);
        }

        return new ArchiveListing { Path = path, Kind = kind, Entries = entries, Error = error };
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
        ArchiveLimits limits, CancellationToken ct)
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
            var archiveBytes = new FileInfo(path).Length;
            return ArchiveSource.Open(path, kind,
                source => ArchiveExtraction.Run(source, root, entries, limits, archiveBytes, ct));
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
