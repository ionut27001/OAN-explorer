using System.IO;
using Oan.Core.Archives;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>
/// Looking inside an archive as if it were a folder.
///
/// The entries are read once when the archive is opened and then filtered by prefix as the user
/// walks down: a solid 7z is a single compressed stream, so listing it again for every subfolder
/// would decompress the whole thing again.
///
/// It is not a folder, and the window must not pretend it is. Nothing here can be renamed,
/// deleted or written to, and <see cref="Writable"/> is what the commands ask before enabling
/// themselves — a button that is lit and then says "not supported" is the defect this project
/// exists to avoid.
/// </summary>
public sealed class ArchiveViewModel : Observable
{
    private ArchiveListing? _listing;
    private string _inside = "";

    /// <summary>The archive on disk, or null when the window is showing a real folder.</summary>
    public string? ArchivePath { get; private set; }

    public bool Active => ArchivePath is not null;

    /// <summary>Always false. Named rather than hard-coded at the call sites so the reason is one place.</summary>
    public bool Writable => false;

    /// <summary>What the address bar shows: the archive, then the path within it.</summary>
    public string DisplayPath => ArchivePath is null
        ? ""
        : _inside.Length == 0 ? ArchivePath : Path.Combine(ArchivePath, _inside);

    /// <summary>
    /// Reads the archive and steps into it. Returns why it could not, or null.
    /// </summary>
    public async Task<string?> OpenAsync(string archivePath, CancellationToken ct = default)
    {
        var listing = await ArchiveReader.ListAsync(archivePath, ct: ct);
        if (listing.Error is { } error) return error;

        Enter(archivePath, listing);
        return null;
    }

    /// <summary>
    /// Takes one entry out to a temp folder so it can be opened. The copy is marked read-only:
    /// nothing written to it goes back into the archive, and a user who is not told that loses
    /// work believing otherwise.
    /// </summary>
    public async Task<(string? File, string? Error)> CopyOutAsync(string insidePath, CancellationToken ct = default)
    {
        if (ArchivePath is null) return (null, "There is no archive open.");

        var temp = Path.Combine(Path.GetTempPath(), "OAN", Guid.NewGuid().ToString("N")[..8]);
        var result = await ArchiveReader.ExtractAsync(ArchivePath, temp, [insidePath], ct: ct);

        var file = Directory.Exists(temp)
            ? Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (file is null)
            return (null, result.Error ?? "That entry could not be taken out of the archive.");

        try { new FileInfo(file).IsReadOnly = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return (file, null);
    }

    /// <summary>The part of a row's path that is inside the archive, in the archive's own spelling.</summary>
    public string Inside(string fullPath) =>
        ArchivePath is null || fullPath.Length <= ArchivePath.Length
            ? ""
            : fullPath[(ArchivePath.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

    public void Enter(string archivePath, ArchiveListing listing)
    {
        ArchivePath = archivePath;
        _listing = listing;
        _inside = "";
        OnChanged(nameof(Active));
        OnChanged(nameof(DisplayPath));
    }

    public void Leave()
    {
        if (!Active) return;

        ArchivePath = null;
        _listing = null;
        _inside = "";
        OnChanged(nameof(Active));
        OnChanged(nameof(DisplayPath));
    }

    /// <summary>Walks down into a folder inside the archive.</summary>
    public void Descend(string folderFullName)
    {
        _inside = folderFullName.Trim('/', '\\');
        OnChanged(nameof(DisplayPath));
    }

    /// <summary>One level up, or false when there is nowhere left to go but out of the archive.</summary>
    public bool Ascend()
    {
        if (_inside.Length == 0) return false;

        var cut = _inside.LastIndexOfAny(['/', '\\']);
        _inside = cut < 0 ? "" : _inside[..cut];
        OnChanged(nameof(DisplayPath));
        return true;
    }

    /// <summary>
    /// The entries directly under where we are standing. Folders the archive never names are
    /// still shown: a zip is allowed to list only a/b/c.txt and no folder entries at all, and
    /// a listing that showed nothing until the leaf would be empty for most archives.
    /// </summary>
    public IReadOnlyList<FileEntry> Rows()
    {
        if (_listing is null) return [];

        var prefix = _inside.Length == 0 ? "" : _inside.Replace('\\', '/') + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<FileEntry>();

        foreach (var entry in _listing.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = name[prefix.Length..].TrimEnd('/');
            if (rest.Length == 0) continue;

            var slash = rest.IndexOf('/');
            var isFolder = slash >= 0 || entry.IsDirectory;
            var label = slash >= 0 ? rest[..slash] : rest;
            if (!seen.Add(label)) continue;

            rows.Add(new FileEntry
            {
                Name = label,
                FullPath = ArchivePath + Path.DirectorySeparatorChar + prefix.Replace('/', Path.DirectorySeparatorChar) + label,
                Attributes = isFolder ? FileAttributes.Directory : FileAttributes.Normal,
                // A folder has no size, and an inferred one has no entry to take a size from.
                Size = isFolder ? null : entry.Size,
                Modified = isFolder ? null : entry.Modified,
            });
        }

        return rows;
    }
}
