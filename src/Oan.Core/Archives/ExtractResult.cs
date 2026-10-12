namespace Oan.Core.Archives;

/// <summary>Why one entry was not written.</summary>
public enum RefusalReason
{
    /// <summary>The name points outside the destination, at a drive, or at a data stream.</summary>
    UnsafePath,

    /// <summary>The entry is a symlink, or a link is already sitting in the way.</summary>
    Link,

    Encrypted,

    /// <summary>Writing it would pass the size or ratio cap.</summary>
    TooLarge,

    /// <summary>Something of that name is already there. Extraction never overwrites.</summary>
    Exists,

    /// <summary>Asked for by name, not in the archive.</summary>
    NotFound,

    /// <summary>The archive would not give up its bytes.</summary>
    Failed,
}

public sealed record RefusedEntry(string FullName, RefusalReason Reason, string Message);

/// <summary>What one extraction did; refusals are counted and named, never folded into the total.
/// <see cref="Error"/> means the run could not start or stopped partway: entries written before that are still on disk and counted.</summary>
public sealed record ExtractResult
{
    public required string Destination { get; init; }

    /// <summary>Entries written, folders included.</summary>
    public int Extracted { get; init; }

    public long Bytes { get; init; }
    public IReadOnlyList<RefusedEntry> Refused { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>Asking again with a password would get further than this run did.</summary>
    public bool NeedsPassword
    {
        get => field || Refused.Any(r => r.Reason is RefusalReason.Encrypted);
        init;
    }

    public bool Ok => Error is null && Refused.Count == 0;

    public static ExtractResult Failed(string destination, string error) =>
        new() { Destination = destination, Error = error };
}

/// <summary>The caps an archive cannot talk its way past. Arguments rather than constants so tests can trip them.</summary>
public sealed record ArchiveLimits
{
    /// <summary>A backstop against an honest but enormous archive filling the disk.</summary>
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Allowed however small the archive is: the ratio cap alone refuses a 200-byte zip of a megabyte of zeroes.</summary>
    public long MinTotalBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxEntries { get; init; } = 200_000;

    /// <summary>Written bytes per byte of archive: the anti-bomb cap, measured against bytes actually written, never header sizes.</summary>
    public int MaxRatio { get; init; } = 500;
}
