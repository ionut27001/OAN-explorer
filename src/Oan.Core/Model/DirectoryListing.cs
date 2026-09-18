namespace Oan.Core.Model;

/// <summary>
/// The result of reading a folder. <see cref="Error"/> is set when the folder could not be
/// read at all; <see cref="Skipped"/> counts entries that were present but unreadable, so a
/// partial listing is never shown as a complete one.
/// </summary>
public sealed record DirectoryListing
{
    public required string Path { get; init; }
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public int Skipped { get; init; }
    public string? Error { get; init; }

    public bool IsComplete => Error is null && Skipped == 0;

    public static DirectoryListing Failed(string path, string error) =>
        new() { Path = path, Entries = [], Error = error };
}
