using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Core.Shell;

/// <summary>
/// Shell icons as frozen WPF images. A lookup that fails returns null rather than throwing, so
/// the caller can draw a placeholder and move on.
/// </summary>
public static class ShellIcons
{
    private const int MaxPerPath = 4096;
    private const int MaxShared = 512;

    /// <summary>Extensions whose icon lives in the file itself, so it cannot be shared.</summary>
    private static readonly HashSet<string> PerFile =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".scr", ".dll", ".lnk", ".ico", ".cur", ".ani", ".url", ".msc" };

    /// <summary>One entry per extension and size, so it is bounded by how many extensions exist.</summary>
    private static readonly Dictionary<string, ImageSource> Shared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One entry per item. This is the one that grows with the filesystem.</summary>
    private static readonly Dictionary<string, ImageSource> PerPath = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, nint> ImageLists = [];

    /// <summary>
    /// The icon for <paramref name="path"/> at roughly <paramref name="size"/> pixels. The
    /// bitmap comes back at the nearest shell image-list size; let WPF scale it.
    /// </summary>
    /// <remarks>
    /// Folders, executables and shortcuts are read from the item itself, so a cache miss blocks
    /// in the shell: resolving a shortcut target, reading desktop.ini, hydrating a cloud
    /// placeholder. A shortcut to a dead share blocks for the network timeout. Call this off the
    /// UI thread. Every other file resolves from its name and never touches disk.
    /// </remarks>
    public static ImageSource? Get(string path, bool isDirectory, int size = 16)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // A folder can carry its own icon — desktop.ini, OneDrive, a repository — so folders are
        // never shared. Files are, unless the icon lives inside the file. No extension at all is
        // a kind like any other: LICENSE, Makefile and Dockerfile all share the generic icon.
        var extension = isDirectory ? null : Path.GetExtension(path);
        var shared = extension is not null && extension.Length != 1 && !PerFile.Contains(extension);

        var cache = shared ? Shared : PerPath;
        var key = shared ? $"{size}|*{extension}" : $"{size}|{path}";

        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        var icon = shared
            ? Load("file" + extension, byExtension: true, size)
            : Load(path, byExtension: false, size);

        // Failures are not cached. A locked file, a drive still spinning up or an extension that
        // faulted once during startup all recover, and a memoised null never would.
        if (icon is null) return null;

        lock (cache)
        {
            if (cache.Count >= (shared ? MaxShared : MaxPerPath)) cache.Clear();
            cache[key] = icon;
        }

        return icon;
    }

    public static void Clear()
    {
        lock (Shared) Shared.Clear();
        lock (PerPath) PerPath.Clear();
    }

    private static ImageSource? Load(string path, bool byExtension, int size)
    {
        try
        {
            var flags = Native.SHGFI_SYSICONINDEX;
            uint attributes = 0;
            if (byExtension)
            {
                // Resolves from the name alone, so a generic icon costs no disk access.
                flags |= Native.SHGFI_USEFILEATTRIBUTES;
                attributes = Native.FILE_ATTRIBUTE_NORMAL;
            }

            var info = default(Native.SHFILEINFO);
            unsafe
            {
                if (Native.SHGetFileInfo(path, attributes, ref info, (uint)sizeof(Native.SHFILEINFO), flags) == 0)
                    return null;
            }

            var list = SystemImageList(size);
            if (list == 0) return null;

            var icon = Native.ImageList_GetIcon(list, info.iIcon, Native.ILD_TRANSPARENT);
            if (icon == 0) return null;

            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                // ImageList_GetIcon hands back a copy. Leaked HICONs exhaust the process GDI
                // quota, and the symptom is the desktop failing to draw, not a crash here.
                Native.DestroyIcon(icon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static nint SystemImageList(int size)
    {
        var which =
            size <= 16 ? Native.SHIL_SMALL :
            size <= 32 ? Native.SHIL_LARGE :
            size <= 48 ? Native.SHIL_EXTRALARGE : Native.SHIL_JUMBO;

        lock (ImageLists)
        {
            if (ImageLists.TryGetValue(which, out var cached)) return cached;

            // A failure is left out of the map so the next call retries; caching the 0 would
            // lose every icon at this size for the life of the process.
            if (Native.SHGetImageList(which, Native.IID_IImageList, out var list) != 0 || list == 0)
                return 0;

            // The system image lists are process-wide and outlive us; the reference is never released.
            ImageLists[which] = list;
            return list;
        }
    }
}
