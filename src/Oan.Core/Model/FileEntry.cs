namespace Oan.Core.Model;

/// <summary>One item in a folder. Nullable fields mean "not known", never "zero".</summary>
public sealed record FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required FileAttributes Attributes { get; init; }

    /// <summary>Null for folders, and for files whose size could not be read.</summary>
    public long? Size { get; init; }

    public DateTime? Modified { get; init; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsHidden => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
}
