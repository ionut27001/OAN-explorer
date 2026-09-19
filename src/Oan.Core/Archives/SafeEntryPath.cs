namespace Oan.Core.Archives;

/// <summary>
/// Where an entry is allowed to land.
///
/// The name inside an archive is a request, not an address: someone chose it, and that someone
/// is not the user. `..\..\Windows\System32\x.dll` is the oldest trick in the format and it
/// still works against anything that concatenates the name onto a folder and opens the result.
/// </summary>
internal static class SafeEntryPath
{
    /// <summary>
    /// The absolute path <paramref name="entryName"/> may be written to under <paramref name="root"/>,
    /// or null and the reason it may not be written at all. <paramref name="root"/> must already
    /// be canonical. Pure string work: no disk is touched, so the rules are testable on their own.
    /// </summary>
    public static (string? Full, string? Why) Under(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return (null, "the entry has no name");

        // Refused before Combine, not after: Path.Combine("C:\dest", "C:\Windows\x") returns
        // C:\Windows\x — the rooted second argument wins outright and the destination is simply
        // dropped. Same for a name starting at the root of the current drive.
        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/') || entryName.StartsWith('\\'))
            return (null, "the entry names an absolute path");

        // "notes.txt:payload" is not a file called that, it is an alternate data stream hidden
        // on notes.txt. It never leaves the destination, which is exactly why it passes the
        // test below and has to be caught here instead.
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

        // The separator is part of the comparison. Without it C:\dest-evil starts with C:\dest
        // and an archive gets to pick a folder next door.
        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fence, StringComparison.OrdinalIgnoreCase))
            return (null, "the entry points outside the destination folder");

        return (full, null);
    }

    /// <summary>
    /// Creates the folders leading to an entry, one level at a time, or returns why it stopped.
    ///
    /// Level by level because <see cref="Path.GetFullPath(string)"/> normalises text and does not
    /// follow links: a junction planted in the destination is still inside it as far as the
    /// string is concerned, and writing through it lands wherever it points.
    /// </summary>
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
            // Unreadable is not safe. Treat it as a link so the entry is refused rather than
            // written to something we could not look at.
            return true;
        }
    }
}
