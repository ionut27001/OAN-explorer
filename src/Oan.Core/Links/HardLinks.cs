namespace Oan.Core.Links;

/// <summary>A hard link is a second name for one set of bytes, and no name is the original: the only answerable question is
/// how many names a file has. That costs a handle per file (GetFileInformationByHandle), so it is per item, never a column.</summary>
public static class HardLinks
{
    /// <summary>How many names this file has; above 1 means hard links exist on the volume. Null when the file could not be opened.</summary>
    public static int? CountOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // FILE_READ_ATTRIBUTES only, so an exclusively held file still answers. OPEN_REPARSE_POINT so a placeholder is not
        // downloaded and a symlink answers about itself; BACKUP_SEMANTICS so a directory can be opened too.
        var handle = LinkNative.CreateFile(path, LinkNative.FILE_READ_ATTRIBUTES, LinkNative.FILE_SHARE_ALL, 0,
            LinkNative.OPEN_EXISTING,
            LinkNative.FILE_FLAG_BACKUP_SEMANTICS | LinkNative.FILE_FLAG_OPEN_REPARSE_POINT, 0);

        if (handle == -1 || handle == 0) return null;

        try
        {
            return LinkNative.GetFileInformationByHandle(handle, out var info) ? (int)info.nNumberOfLinks : null;
        }
        finally
        {
            LinkNative.CloseHandle(handle);
        }
    }

    /// <summary>True when this file is known to have more than one name.</summary>
    public static bool? IsShared(string path) => CountOf(path) is { } count ? count > 1 : null;
}
