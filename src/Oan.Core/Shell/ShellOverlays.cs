using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Core.Shell;

/// <summary>The badge in the corner of an icon: shortcut arrow, sharing hand, cloud tick. Handed back on its own,
/// because it has to sit on a thumbnail as well as on an icon.</summary>
public static class ShellOverlays
{
    public const int NoOverlay = 0;

    /// <summary>The image list entry the badge is lifted off; any would do.</summary>
    private const int AnyIcon = 0;

    private static readonly Dictionary<string, ImageSource> Badges = [];
    private static readonly Dictionary<int, nint> ImageLists = [];

    /// <summary>Which badge <paramref name="path"/> carries, or <see cref="NoOverlay"/>. A shell slot number: only feed it to <see cref="Badge"/>.</summary>
    /// <remarks>Measured: SHGFI_OVERLAYINDEX works only with SHGFI_ICON; with SHGFI_SYSICONINDEX it silently answers 0. So an HICON is made and destroyed.
    /// It reads the item every time (call it off the UI thread) and is deliberately not cached: a badge is as fresh as its listing.</remarks>
    public static int IndexFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return NoOverlay;

        var info = default(Native.SHFILEINFO);

        try
        {
            var flags = ThumbnailNative.SHGFI_ICON | ThumbnailNative.SHGFI_SMALLICON |
                        ThumbnailNative.SHGFI_OVERLAYINDEX;

            unsafe
            {
                if (Native.SHGetFileInfo(path, 0, ref info, (uint)sizeof(Native.SHFILEINFO), flags) == 0)
                    return NoOverlay;
            }

            // The overlay index rides in the top byte of the same int the icon index is in.
            return (info.iIcon >> 24) & 0xF;
        }
        catch (Exception)
        {
            return NoOverlay;
        }
        finally
        {
            if (info.hIcon != 0) Native.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>The badge alone, on a transparent square the size of a whole icon. Null when there is none.</summary>
    /// <remarks>Measured: IImageList::GetOverlayImage answers S_OK with index 1 for all fifteen slots, so the badge is read out of the
    /// difference between one icon drawn with the overlay mask and the same icon drawn without. Built once per slot and size.</remarks>
    public static ImageSource? Badge(int overlay, int size)
    {
        if (overlay is <= NoOverlay or > 15) return null;

        var key = $"{size}|overlay{overlay}";

        lock (Badges)
        {
            if (Badges.TryGetValue(key, out var cached)) return cached;
        }

        var image = Extract(ImageListFor(size), overlay);
        if (image is null) return null;

        lock (Badges) Badges[key] = image;
        return image;
    }

    /// <summary>The badge for an item, in one call; null means none.</summary>
    public static ImageSource? For(string path, int size) => Badge(IndexFor(path), size);

    public static void Clear()
    {
        lock (Badges) Badges.Clear();
    }

    private static ImageSource? Extract(int which, int overlay)
    {
        var list = Open(which);
        if (list == 0) return null;

        // INDEXTOOVERLAYMASK: the overlay slot goes in bits 8-11 of the draw flags.
        var plain = Draw(list, Native.ILD_TRANSPARENT);
        var badged = Draw(list, Native.ILD_TRANSPARENT | (uint)(overlay << 8));
        if (plain is null || badged is null || plain.Length != badged.Length) return null;

        return OnlyWhereTheyDiffer(plain, badged, badged.Length, which);
    }

    private static byte[]? Draw(nint list, uint flags)
    {
        var icon = Native.ImageList_GetIcon(list, AnyIcon, flags);
        if (icon == 0) return null;

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            var stride = image.PixelWidth * 4;
            var pixels = new byte[stride * image.PixelHeight];
            new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // ImageList_GetIcon hands back a copy; leaked HICONs exhaust the process GDI quota.
            Native.DestroyIcon(icon);
        }
    }

    /// <summary>Every pixel the overlay changed, and nothing else.</summary>
    private static ImageSource? OnlyWhereTheyDiffer(byte[] plain, byte[] badged, int length, int which)
    {
        var side = SideOf(which);
        var stride = side * 4;
        if (length != stride * side) return null;

        var badge = new byte[length];
        var drawn = 0;

        for (var i = 0; i < length; i += 4)
        {
            if (plain[i] == badged[i] && plain[i + 1] == badged[i + 1] &&
                plain[i + 2] == badged[i + 2] && plain[i + 3] == badged[i + 3])
                continue;

            Array.Copy(badged, i, badge, i, 4);
            badge[i + 3] = 255;
            drawn++;
        }

        if (drawn == 0) return null;

        var image = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, badge, stride);
        image.Freeze();
        return image;
    }

    private static nint Open(int which)
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

    private static int ImageListFor(int size) =>
        size <= 16 ? Native.SHIL_SMALL :
        size <= 32 ? Native.SHIL_LARGE :
        size <= 48 ? Native.SHIL_EXTRALARGE : Native.SHIL_JUMBO;

    private static int SideOf(int which) =>
        which == Native.SHIL_SMALL ? 16 :
        which == Native.SHIL_LARGE ? 32 :
        which == Native.SHIL_EXTRALARGE ? 48 : 256;
}
