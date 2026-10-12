using System.IO;

namespace Oan.App.Platform;

/// <summary>One step of the address bar: what to show, and where clicking it goes.</summary>
public sealed record Crumb(string Name, string Path);

internal static class Paths
{
    /// <summary>The folder to show in order to reveal a path: itself if it is one, otherwise the folder holding it.</summary>
    public static string? Holding(string path) =>
        Directory.Exists(path) ? path : Path.GetDirectoryName(path);

    /// <summary>The address bar's trail, root first. A drive's own name is empty, so the root shows as its path.</summary>
    public static IReadOnlyList<Crumb> Crumbs(string path)
    {
        var crumbs = new List<Crumb>();
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            var name = dir.Parent is null ? dir.FullName.TrimEnd(Path.DirectorySeparatorChar) : dir.Name;
            crumbs.Insert(0, new Crumb(name, dir.FullName));
        }
        return crumbs;
    }

    /// <summary>The folder a typed address means, or null. Expands %TEMP% and drops surrounding quotes, as Explorer does.</summary>
    public static string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            var full = Path.GetFullPath(expanded);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>Free space on the drive holding this path. Null when it could not be asked.</summary>
    public static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
