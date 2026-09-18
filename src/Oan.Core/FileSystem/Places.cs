using Oan.Core.Model;

namespace Oan.Core.FileSystem;

public enum PlaceKind { Folder, File, Drive, Recycle }

public sealed record Place
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required PlaceKind Kind { get; init; }

    /// <summary>False when the path is gone — a pinned folder that was deleted, an unmounted drive.</summary>
    public bool Available { get; init; } = true;

    /// <summary>Drives only. Null when the size could not be read, which is not the same as full.</summary>
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }

    public double? UsedFraction =>
        TotalBytes is > 0 && FreeBytes is not null
            ? 1.0 - (double)FreeBytes.Value / TotalBytes.Value
            : null;
}

public static class Places
{
    private static readonly (Environment.SpecialFolder Folder, string Name)[] QuickAccess =
    [
        (Environment.SpecialFolder.Desktop, "Desktop"),
        (Environment.SpecialFolder.UserProfile, "Home"),
        (Environment.SpecialFolder.MyDocuments, "Documents"),
        (Environment.SpecialFolder.MyPictures, "Pictures"),
        (Environment.SpecialFolder.MyMusic, "Music"),
        (Environment.SpecialFolder.MyVideos, "Videos"),
    ];

    /// <summary>
    /// Starred paths, resolved for display, in the order the user starred them.
    ///
    /// A path that no longer exists comes back with Available false rather than being dropped:
    /// a star is something the user put there, and silently removing it is both a loss and
    /// indistinguishable from never having starred it. Renaming or moving a starred item does
    /// break the entry — it is stored by path — and the honest answer is to show it as gone so
    /// the user can act, not to guess.
    /// </summary>
    public static IReadOnlyList<Place> Starred(IEnumerable<string> paths)
    {
        var places = new List<Place>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || !seen.Add(raw)) continue;

            var isFolder = Directory.Exists(raw);
            var isFile = !isFolder && File.Exists(raw);
            var name = System.IO.Path.GetFileName(raw.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

            places.Add(new Place
            {
                Name = string.IsNullOrEmpty(name) ? raw : name,
                Path = raw,
                Kind = isFile ? PlaceKind.File : PlaceKind.Folder,
                Available = isFolder || isFile,
            });
        }

        return places;
    }

    public static IReadOnlyList<Place> Quick()
    {
        var places = new List<Place>();

        foreach (var (folder, name) in QuickAccess)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0)
                places.Add(new Place { Name = name, Path = path, Kind = PlaceKind.Folder, Available = Directory.Exists(path) });
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            places.Insert(1, new Place { Name = "Downloads", Path = downloads, Kind = PlaceKind.Folder });

        return places;
    }

    public static IReadOnlyList<Place> Drives()
    {
        var drives = new List<Place>();
        foreach (var d in DriveInfo.GetDrives())
        {
            long? total = null, free = null;
            var ready = false;
            try
            {
                ready = d.IsReady;
                if (ready) { total = d.TotalSize; free = d.AvailableFreeSpace; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var label = ready && !string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.VolumeLabel : "Local Disk";
            drives.Add(new Place
            {
                Name = $"{label} ({d.Name.TrimEnd('\\')})",
                Path = d.RootDirectory.FullName,
                Kind = PlaceKind.Drive,
                Available = ready,
                TotalBytes = total,
                FreeBytes = free,
            });
        }
        return drives;
    }
}
