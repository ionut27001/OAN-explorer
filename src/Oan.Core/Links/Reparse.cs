namespace Oan.Core.Links;

/// <summary>What a reparse point actually is; most of them are not links.</summary>
public enum ReparseKind
{
    None,

    /// <summary>A directory standing in for another local directory (<c>mklink /J</c>). May point at its own ancestor.</summary>
    Junction,

    /// <summary>The junction tag pointing at a volume: the target is a <c>\\?\Volume{...}</c> name, not a navigable path.</summary>
    VolumeMountPoint,

    /// <summary>A directory symbolic link; unlike a junction it can point across machines.</summary>
    DirectorySymlink,

    /// <summary>A file symbolic link. Its own length is zero; the bytes belong to the target.</summary>
    FileSymlink,

    /// <summary>OneDrive and other cloud-filter items. NOT a link: the item itself, held remotely. Opening one downloads it.</summary>
    CloudPlaceholder,

    /// <summary>The zero-byte stubs under <c>WindowsApps</c> that launch a packaged app.</summary>
    AppExecutionAlias,

    /// <summary>Data Deduplication moved the bytes to a chunk store; reads and reported length are normal.</summary>
    Deduplicated,

    /// <summary>Compressed by the Windows Overlay Filter; transparent to every read.</summary>
    Compressed,

    /// <summary>A Linux symlink created inside WSL. Windows will not follow it.</summary>
    WslLink,

    /// <summary>An unrecognised tag, named by number.</summary>
    Other,
}

/// <summary>What one reparse point is and where it goes. Unknown stays null.</summary>
public sealed record ReparseInfo
{
    public required string Path { get; init; }
    public required ReparseKind Kind { get; init; }

    /// <summary>The raw tag, kept so <see cref="ReparseKind.Other"/> can still be named.</summary>
    public required uint Tag { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Where it points. Null for tags that carry no path (cloud placeholder, app alias).</summary>
    public string? Target { get; init; }

    /// <summary>Null when it was not asked, which is the case for every tag with no target.</summary>
    public bool? TargetExists { get; init; }

    public string? Refusal { get; init; }
}

/// <summary>Reads the reparse tag off a directory entry (<c>FindFirstFileW</c>, <c>dwReserved0</c>): no handle is opened,
/// so a OneDrive placeholder is not hydrated. The target costs a real open: ask it for one item, never per row.</summary>
public static class Reparse
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA000_0003;
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000_000C;
    private const uint IO_REPARSE_TAG_APPEXECLINK = 0x8000_001B;
    private const uint IO_REPARSE_TAG_DEDUP = 0x8000_0013;
    private const uint IO_REPARSE_TAG_WOF = 0x8000_0017;
    private const uint IO_REPARSE_TAG_LX_SYMLINK = 0xA000_001D;
    private const uint IO_REPARSE_TAG_ONEDRIVE = 0x8000_0021;

    /// <summary>The cloud filter takes sixteen tags, 0x9000_001A through 0x9000_F01A; the provider nibble is masked out.</summary>
    private const uint IO_REPARSE_TAG_CLOUD = 0x9000_001A;
    private const uint CloudMask = 0xFFFF_0FFF;

    /// <summary>A volume mount point's target is <c>\\?\Volume{guid}\</c>, not a path.</summary>
    private const string VolumePrefix = @"\\?\Volume";

    /// <summary>What <paramref name="path"/> is, or null when it is not a reparse point. Only <paramref name="withTarget"/> opens the item.</summary>
    public static ReparseInfo? Of(string path, bool withTarget = true)
    {
        var tag = TagOf(path, out var isDirectory);
        if (tag is not { } found) return null;

        var target = withTarget ? TargetOf(path, isDirectory) : null;
        var kind = KindOf(found, isDirectory, target);

        return new ReparseInfo
        {
            Path = path,
            Kind = kind,
            Tag = found,
            IsDirectory = isDirectory,
            Target = target,
            TargetExists = target is null || target.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase)
                ? null
                : Directory.Exists(target) || File.Exists(target),
            Refusal = kind == ReparseKind.Other
                ? $"This is a reparse point of a kind OAN does not know (tag 0x{found:X8})."
                : target is null && Points(kind)
                    ? "This link's target could not be read."
                    : null,
        };
    }

    /// <summary>The reparse tag of one item, or null. One directory lookup and no open, so cheap enough per row.</summary>
    public static uint? TagOf(string path, out bool isDirectory)
    {
        isDirectory = false;
        if (string.IsNullOrWhiteSpace(path)) return null;

        // FindFirstFileW wants the item without a trailing separator; a drive root has nothing to strip to and is refused.
        var item = System.IO.Path.TrimEndingDirectorySeparator(path);
        if (System.IO.Path.GetPathRoot(item) is { } root &&
            string.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), item, StringComparison.OrdinalIgnoreCase))
            return null;

        var handle = LinkNative.FindFirstFile(item, out var data);
        if (handle == -1 || handle == 0) return null;

        try
        {
            var attributes = (FileAttributes)data.dwFileAttributes;
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            return (attributes & FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : null;
        }
        finally
        {
            LinkNative.FindClose(handle);
        }
    }

    public static ReparseKind KindOf(uint tag, bool isDirectory, string? target) => tag switch
    {
        IO_REPARSE_TAG_MOUNT_POINT =>
            target is not null && target.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase)
                ? ReparseKind.VolumeMountPoint
                : ReparseKind.Junction,
        IO_REPARSE_TAG_SYMLINK => isDirectory ? ReparseKind.DirectorySymlink : ReparseKind.FileSymlink,
        IO_REPARSE_TAG_APPEXECLINK => ReparseKind.AppExecutionAlias,
        IO_REPARSE_TAG_DEDUP => ReparseKind.Deduplicated,
        IO_REPARSE_TAG_WOF => ReparseKind.Compressed,
        IO_REPARSE_TAG_LX_SYMLINK => ReparseKind.WslLink,
        IO_REPARSE_TAG_ONEDRIVE => ReparseKind.CloudPlaceholder,
        _ => (tag & CloudMask) == IO_REPARSE_TAG_CLOUD ? ReparseKind.CloudPlaceholder : ReparseKind.Other,
    };

    /// <summary>The word for the Kind column.</summary>
    public static string Label(ReparseKind kind) => kind switch
    {
        ReparseKind.Junction => "Junction",
        ReparseKind.VolumeMountPoint => "Mounted drive",
        ReparseKind.DirectorySymlink => "Folder link",
        ReparseKind.FileSymlink => "File link",
        ReparseKind.CloudPlaceholder => "Online",
        ReparseKind.AppExecutionAlias => "App alias",
        ReparseKind.Deduplicated => "Deduplicated",
        ReparseKind.Compressed => "Compressed",
        ReparseKind.WslLink => "Linux link",
        _ => "Reparse point",
    };

    /// <summary>Whether a size walk should go inside. Junctions, mount points and directory symlinks are refused: following them
    /// double-counts or never terminates. A cloud placeholder folder must NOT be refused: its children enumerate without downloading.</summary>
    public static bool ShouldDescend(ReparseKind kind) => kind switch
    {
        ReparseKind.Junction or ReparseKind.VolumeMountPoint or ReparseKind.DirectorySymlink => false,
        _ => true,
    };

    /// <summary>The same for a folder already known to carry the reparse attribute; call it for no others (0.148 ms an entry).
    /// A failed lookup counts as an ordinary folder; a mount point whose target will not read is refused.</summary>
    public static bool ShouldDescendInto(string path)
    {
        if (TagOf(path, out var isDirectory) is not { } tag) return true;

        // Only the mount point tag needs its target, and that read costs an open.
        var target = tag == IO_REPARSE_TAG_MOUNT_POINT ? TargetOf(path, isDirectory) : null;
        return ShouldDescend(KindOf(tag, isDirectory, target));
    }

    /// <summary>Kinds that name somewhere else.</summary>
    private static bool Points(ReparseKind kind) =>
        kind is ReparseKind.Junction or ReparseKind.VolumeMountPoint
            or ReparseKind.DirectorySymlink or ReparseKind.FileSymlink or ReparseKind.WslLink;

    /// <summary>Read through the runtime: <c>LinkTarget</c> has understood junctions as well as symlinks since .NET 6.</summary>
    private static string? TargetOf(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
