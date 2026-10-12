namespace Oan.Core.Archives;

/// <summary>Where an entry is allowed to land. The name inside an archive is attacker-chosen (<c>..\..\Windows\System32\x.dll</c>).</summary>
internal static class SafeEntryPath
{
    /// <summary>The absolute path <paramref name="entryName"/> may be written to under <paramref name="root"/>, or null and the reason.
    /// <paramref name="root"/> must already be canonical. Pure string work: no disk is touched.</summary>
    public static (string? Full, string? Why) Under(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return (null, "the entry has no name");

        // Refused before Combine: Path.Combine("C:\dest", "C:\Windows\x") returns C:\Windows\x, the rooted argument wins.
        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/') || entryName.StartsWith('\\'))
            return (null, "the entry names an absolute path");

        // "notes.txt:payload" is an alternate data stream on notes.txt; it stays inside the destination, so the fence below misses it.
        if (entryName.Contains(':')) return (null, "the entry names an alternate data stream");

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, entryName));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "the entry name is not a usable path");
        }

        // The separator is part of the comparison: C:\dest-evil starts with C:\dest.
        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fence, StringComparison.OrdinalIgnoreCase))
            return (null, "the entry points outside the destination folder");

        return (full, null);
    }

    /// <summary>Creates the folders leading to an entry, or returns why it stopped. Level by level because GetFullPath does not
    /// follow links: a junction planted in the destination would be written through.</summary>
    public static string? Create(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        if (relative is "." or "") return null;

        var at = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);

            if (Directory.Exists(at))
            {
                if (IsLink(at)) return $"'{part}' in the destination is a link";
                continue;
            }
            if (File.Exists(at)) return $"'{part}' in the destination is a file, not a folder";

            Directory.CreateDirectory(at);
        }
        return null;
    }

    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not safe: treat it as a link so the entry is refused.
            return true;
        }
    }
}
