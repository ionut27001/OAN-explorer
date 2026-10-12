namespace Oan.Core.Archives;

/// <summary>What the bytes at the front of a file say it is, not what its extension claims.</summary>
public enum ArchiveKind
{
    None, Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz,

    /// <summary>Known by its extension, and readable only through the user's 7-Zip.</summary>
    Other,
}

/// <summary>One item inside an archive. Nullable means "the archive does not say", never "zero".</summary>
public sealed record ArchiveEntry
{
    /// <summary>The path as the archive records it. This is a name, not an address.</summary>
    public required string FullName { get; init; }

    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public long? Compressed { get; init; }
    public DateTime? Modified { get; init; }
    public bool Encrypted { get; init; }
}

/// <summary>An archive's table of contents. <see cref="Error"/> is why it could not be read;
/// entries read before it went wrong are kept.</summary>
public sealed record ArchiveListing
{
    public required string Path { get; init; }
    public required IReadOnlyList<ArchiveEntry> Entries { get; init; }
    public ArchiveKind Kind { get; init; }
    public string? Error { get; init; }

    /// <summary>The names themselves are encrypted: nothing can be shown until a password is given.</summary>
    public bool NeedsPassword { get; init; }

    public bool IsComplete => Error is null;

    public static ArchiveListing Failed(string path, string error, ArchiveKind kind = ArchiveKind.None) =>
        new() { Path = path, Entries = [], Kind = kind, Error = error };
}
