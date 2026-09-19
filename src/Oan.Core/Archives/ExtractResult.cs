namespace Oan.Core.Archives;

/// <summary>Why one entry was not written. Every refusal carries one.</summary>
public enum RefusalReason
{
    /// <summary>The name points outside the destination, at a drive, or at a data stream.</summary>
    UnsafePath,

    /// <summary>The entry is a symlink, or a link is already sitting in the way.</summary>
    Link,

    Encrypted,

    /// <summary>Writing it would pass the size or ratio cap.</summary>
    TooLarge,

    /// <summary>Asked for by name, not in the archive.</summary>
    NotFound,

    /// <summary>The archive would not give up its bytes.</summary>
    Failed,
}

public sealed record RefusedEntry(string FullName, RefusalReason Reason, string Message);

/// <summary>
/// What one extraction did. "40 of 42, and here are the two" is a different answer from "42",
/// and the window has to be able to tell them apart — so refusals are counted and named, never
/// folded into the total.
///
/// <see cref="Error"/> is set when the run could not start, or was stopped partway: entries
/// written before that are still on disk and still counted.
/// </summary>
public sealed record ExtractResult
{
    public required string Destination { get; init; }

    /// <summary>Entries written. Folders the archive asked for count, they are entries too.</summary>
    public int Extracted { get; init; }

    public long Bytes { get; init; }
    public IReadOnlyList<RefusedEntry> Refused { get; init; } = [];
    public string? Error { get; init; }

    public bool Ok => Error is null && Refused.Count == 0;

    public static ExtractResult Failed(string destination, string error) =>
        new() { Destination = destination, Error = error };
}

/// <summary>
/// The caps an archive cannot talk its way past. They are arguments rather than constants
/// because the tests have to be able to trip them without writing a real bomb to this disk.
/// </summary>
public sealed record ArchiveLimits
{
    /// <summary>A backstop against an honest but enormous archive filling the disk.</summary>
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Allowed however small the archive is: the ratio cap on its own refuses a
    /// 200-byte zip of a megabyte of zeroes, which is a real file and not an attack.</summary>
    public long MinTotalBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxEntries { get; init; } = 200_000;

    /// <summary>
    /// Written bytes per byte of archive. This is the anti-bomb cap and it is measured against
    /// bytes actually written, never against the sizes in the header — a 40 KB file claiming to
    /// hold a petabyte is lying about one of the two, and only one of them is a fact.
    /// </summary>
    public int MaxRatio { get; init; } = 500;
}
