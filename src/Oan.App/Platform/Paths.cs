using System.IO;

namespace Oan.App.Platform;

/// <summary>
/// Path questions with no view model in them. Moved out of MainViewModel because none of them
/// touch its state, and a window's model that also owns the path rules is how that class grew.
/// </summary>
/// <summary>One step of the address bar: what to show, and where clicking it goes.</summary>
public sealed record Crumb(string Name, string Path);

internal static class Paths
{
    /// <summary>
    /// The address bar's trail, root first. The root shows as "C:\" rather than as its own name,
    /// which for a drive is the empty string.
    /// </summary>
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

    /// <summary>
    /// The folder a typed address means, or null. Environment variables are expanded, so %TEMP%
    /// works the way it does in Explorer's address bar, and surrounding quotes are dropped
    /// because that is how a path arrives when it was copied from anywhere else.
    /// </summary>
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
