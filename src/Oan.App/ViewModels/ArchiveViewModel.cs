using System.IO;
using Oan.Core.Archives;
using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>
/// Looking inside an archive as if it were a folder. Entries are read once and filtered by prefix:
/// a solid 7z is one compressed stream, and listing it per subfolder would decompress it all again.
/// </summary>
public sealed class ArchiveViewModel : Observable
{
    private ArchiveListing? _listing;
    private string _inside = "";

    /// <summary>The archive on disk, or null when the window is showing a real folder.</summary>
    public string? ArchivePath { get; private set; }

    public bool Active => ArchivePath is not null;

    /// <summary>The password that opened this archive, kept so its entries can be taken out.</summary>
    public string? Password { get; private set; }

    public Func<string, string?>? AskPassword { get; set; }

    /// <summary>What the address bar shows: the archive, then the path within it.</summary>
    public string DisplayPath => ArchivePath is null
        ? ""
        : _inside.Length == 0 ? ArchivePath : Path.Combine(ArchivePath, _inside);

    /// <summary>Reads the archive and steps into it. Returns why it could not, or null.</summary>
    public async Task<string?> OpenAsync(string archivePath, CancellationToken ct = default)
    {
        string? password = null;
        var listing = await ArchiveReader.ListAsync(archivePath, ct: ct);

        // The names themselves are encrypted: asked for until one works or the user stops offering.
        while (listing.NeedsPassword && AskPassword?.Invoke(Path.GetFileName(archivePath)) is { Length: > 0 } offered)
        {
            password = offered;
            listing = await ArchiveReader.ListAsync(archivePath, password: password, ct: ct);
        }
        if (listing.Error is { } error) return error;

        Enter(archivePath, listing);
        Password = password;
        return null;
    }

    /// <summary>Takes one entry out to a temp folder so it can be opened. Read-only: nothing written to it goes back.</summary>
    public async Task<(string? File, string? Error)> CopyOutAsync(string insidePath, CancellationToken ct = default)
    {
        if (ArchivePath is null) return (null, "There is no archive open.");

        var temp = Path.Combine(Path.GetTempPath(), "OAN", Guid.NewGuid().ToString("N")[..8]);
        var result = await ArchiveReader.ExtractAsync(ArchivePath, temp, [insidePath], password: Password, ct: ct);
        while (result.NeedsPassword && AskPassword?.Invoke(Path.GetFileName(ArchivePath)) is { Length: > 0 } offered)
        {
            Password = offered;
            result = await ArchiveReader.ExtractAsync(ArchivePath, temp, [insidePath], password: Password, ct: ct);
        }

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

    /// <summary>The archive's own names for these rows and everything beneath them.</summary>
    public IReadOnlyList<string> EntriesUnder(IEnumerable<string> insidePaths)
    {
        if (_listing is null) return [];

        var picked = insidePaths.Select(p => p.Trim('/')).ToArray();
        return
        [
            .. _listing.Entries
                .Select(e => e.FullName)
                .Where(name => picked.Any(p =>
                {
                    var plain = name.Replace('\\', '/').Trim('/');
                    return plain.Equals(p, StringComparison.OrdinalIgnoreCase)
                        || plain.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
                })),
        ];
    }

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
        Password = null;
        _listing = null;
        _inside = "";
        OnChanged(nameof(Active));
        OnChanged(nameof(DisplayPath));
    }

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
    /// The entries directly under where we are. Folders the archive never names are inferred:
    /// a zip may list only a/b/c.txt.
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
