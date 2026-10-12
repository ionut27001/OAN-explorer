using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Core.Shell;

/// <summary>Shell icons as frozen WPF images. A lookup that fails returns null rather than throwing.</summary>
public static class ShellIcons
{
    private const int MaxPerPath = 4096;
    private const int MaxShared = 512;

    /// <summary>Extensions whose icon lives in the file itself, so it cannot be shared.</summary>
    private static readonly HashSet<string> PerFile =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".scr", ".dll", ".lnk", ".ico", ".cur", ".ani", ".url", ".msc" };

    private static readonly Dictionary<string, ImageSource> Shared = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ImageSource> PerPath = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, nint> ImageLists = [];

    /// <summary>The icon for <paramref name="path"/>; <paramref name="size"/> snaps up to the shell's 16, 32, 48 or 256 list.</summary>
    /// <remarks>Folders, executables and shortcuts are read from the item itself: a shortcut to a dead share blocks for
    /// the network timeout. Call this off the UI thread.</remarks>
    public static ImageSource? Get(string path, bool isDirectory, int size = 16)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // Folders can carry their own icon (desktop.ini, OneDrive) and are never shared. Files are, unless the icon
        // lives inside the file; no extension at all is a kind like any other.
        var extension = isDirectory ? null : Path.GetExtension(path);
        var shared = extension is not null && extension.Length != 1 && !PerFile.Contains(extension);

        var cache = shared ? Shared : PerPath;
        var key = shared ? $"{size}|*{extension}" : $"{size}|{path}";

        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        // A folder that is not on disk — one inside an archive — still gets the plain folder icon.
        var icon = shared
            ? Load("file" + extension, Native.FILE_ATTRIBUTE_NORMAL, size)
            : Load(path, null, size) ?? (isDirectory ? Load("folder", Native.FILE_ATTRIBUTE_DIRECTORY, size) : null);

        // Failures are not cached: a locked file or a drive still spinning up recovers.
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

    /// <param name="assumed">Set to resolve from the name alone, as a thing of these attributes.</param>
    private static ImageSource? Load(string path, uint? assumed, int size)
    {
        try
        {
            var flags = Native.SHGFI_SYSICONINDEX;
            var attributes = assumed ?? 0;

            // Resolves from the name alone, so a generic icon costs no disk access.
            if (assumed is not null) flags |= Native.SHGFI_USEFILEATTRIBUTES;

            var info = default(Native.SHFILEINFO);
            unsafe
            {
                if (Native.SHGetFileInfo(path, attributes, ref info, (uint)sizeof(Native.SHFILEINFO), flags) == 0)
                    return null;
            }

            var which = ImageListFor(size);
            var list = SystemImageList(which);
            if (list == 0) return null;

            var icon = Native.ImageList_GetIcon(list, info.iIcon, Native.ILD_TRANSPARENT);
            if (icon == 0) return null;

            try
            {
                BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();

                if (which == Native.SHIL_JUMBO)
                {
                    image = Unpad(image);
                    image.Freeze();
                }

                return image;
            }
            finally
            {
                // ImageList_GetIcon hands back a copy; leaked HICONs exhaust the process GDI quota.
                Native.DestroyIcon(icon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The jumbo list pads rather than scales: an item with no 256px asset is its small icon at the origin of
    /// a transparent 256px bitmap. Crop back to the box the drawing fills.</summary>
    private static BitmapSource Unpad(BitmapSource image)
    {
        var drawn = DrawnExtent(image);
        var box = drawn <= 16 ? 16 : drawn <= 32 ? 32 : drawn <= 48 ? 48 : image.PixelWidth;

        // Nothing drawn at all, or real 256px art: hand back what the shell gave us.
        if (drawn == 0 || box >= image.PixelWidth) return image;

        return new CroppedBitmap(image, new Int32Rect(0, 0, box, box));
    }

    /// <summary>The side of the square at the origin that contains every non-transparent pixel.</summary>
    private static int DrawnExtent(BitmapSource image)
    {
        var source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var extent = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = width - 1; x >= 0; x--)
            {
                if (pixels[row + x * 4 + 3] == 0) continue;
                extent = Math.Max(extent, Math.Max(x + 1, y + 1));
                break;
            }
        }

        return extent;
    }

    private static int ImageListFor(int size) =>
        size <= 16 ? Native.SHIL_SMALL :
        size <= 32 ? Native.SHIL_LARGE :
        size <= 48 ? Native.SHIL_EXTRALARGE : Native.SHIL_JUMBO;

    private static nint SystemImageList(int which)
    {
        lock (ImageLists)
        {
            if (ImageLists.TryGetValue(which, out var cached)) return cached;

            // A failure is not cached, so the next call retries.
            if (Native.SHGetImageList(which, Native.IID_IImageList, out var list) != 0 || list == 0)
                return 0;

            // The system image lists are process-wide and outlive us; the reference is never released.
            ImageLists[which] = list;
            return list;
        }
    }
}
