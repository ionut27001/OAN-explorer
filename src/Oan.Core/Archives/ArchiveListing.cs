namespace Oan.Core.Archives;

/// <summary>What the bytes at the front of a file say it is, not what its extension claims.</summary>
public enum ArchiveKind { None, Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz }

/// <summary>
/// One item inside an archive. Nullable means "the archive does not say", never "zero": 7z can
/// omit a size, a solid block has no per-entry compressed size, and a folder has neither.
/// </summary>
public sealed record ArchiveEntry
{
    /// <summary>The path as the archive records it, separators and all. This is a name, not an address.</summary>
    public required string FullName { get; init; }

    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public long? Compressed { get; init; }
    public DateTime? Modified { get; init; }
    public bool Encrypted { get; init; }
}

/// <summary>
/// An archive's table of contents — the shape <see cref="Model.DirectoryListing"/> uses, for the
/// same reason: "empty" and "we do not know" must not arrive as the same answer.
///
/// <see cref="Error"/> is why the archive could not be read. Entries read before it went wrong
/// are kept, so a truncated archive shows what it had and still reports that it is not complete.
/// </summary>
public sealed record ArchiveListing
{
    public required string Path { get; init; }
    public required IReadOnlyList<ArchiveEntry> Entries { get; init; }
    public ArchiveKind Kind { get; init; }
    public string? Error { get; init; }

    public bool IsComplete => Error is null;

    public static ArchiveListing Failed(string path, string error, ArchiveKind kind = ArchiveKind.None) =>
        new() { Path = path, Entries = [], Kind = kind, Error = error };
}
