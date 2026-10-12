using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Core.Shell;

/// <summary>The shell imaging calls <see cref="Native"/> does not carry.</summary>
internal static class ThumbnailNative
{
    /// <summary>Which picture GetImage may return, and where it is allowed to come from.</summary>
    [Flags]
    internal enum Siigbf : uint
    {
        /// <summary>Fit inside the requested box, keeping the aspect ratio. The default.</summary>
        ResizeToFit = 0x0000,
        BiggerSizeOk = 0x0001,
        IconOnly = 0x0004,
        ThumbnailOnly = 0x0008,
        /// <summary>Answer from the shell's own thumbnail cache or not at all. Loads no provider.</summary>
        InCacheOnly = 0x0010,
        ScaleUp = 0x0100,
    }

    /// <summary>Written into bits 24-31 of iIcon, but only alongside SHGFI_ICON, never SHGFI_SYSICONINDEX. Measured; see ShellOverlays.</summary>
    internal const uint SHGFI_OVERLAYINDEX = 0x0040;

    internal const uint SHGFI_ICON = 0x0100;

    internal const uint SHGFI_SMALLICON = 0x0001;

    internal static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, Siigbf flags, out nint bitmap);
    }

    /// <summary>Declared as far as GetOverlayImage. The methods before it are placeholders holding their vtable slot:
    /// order is commctrl.h's, do not reorder. Their argument lists are empty because nothing calls them.</summary>
    [ComImport, Guid("46eb5926-582e-4017-9fdf-e8998daa0950"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig] int Add();
        [PreserveSig] int ReplaceIcon();
        [PreserveSig] int SetOverlayImage();
        [PreserveSig] int Replace();
        [PreserveSig] int AddMasked();
        [PreserveSig] int Draw();
        [PreserveSig] int Remove();
        [PreserveSig] int GetIcon();
        [PreserveSig] int GetImageInfo();
        [PreserveSig] int Copy();
        [PreserveSig] int Merge();
        [PreserveSig] int Clone();
        [PreserveSig] int GetImageRect();
        [PreserveSig] int GetIconSize(out int cx, out int cy);
        [PreserveSig] int SetIconSize();
        [PreserveSig] int GetImageCount(out int count);
        [PreserveSig] int SetImageCount();
        [PreserveSig] int SetBkColor();
        [PreserveSig] int GetBkColor();
        [PreserveSig] int BeginDrag();
        [PreserveSig] int DragEnter();
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragMove();
        [PreserveSig] int SetDragCursorImage();
        [PreserveSig] int DragShowNolock();
        [PreserveSig] int GetDragImage();
        [PreserveSig] int GetItemFlags();
        [PreserveSig] int GetOverlayImage(int overlay, out int index);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    internal static extern int GetObject(nint handle, int size, ref DIBSECTION dib);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DIBSECTION
    {
        public BITMAP Bitmap;
        public uint HeaderSize;
        public int HeaderWidth;

        /// <summary>Negative for a top-down DIB, which is what the shell hands back.</summary>
        public int HeaderHeight;

        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColoursUsed;
        public uint ColoursImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public nint Section;
        public uint Offset;
    }

    /// <summary>The HBITMAP the shell returns, as a frozen WPF image; the handle is deleted either way.
    /// Not <c>Imaging.CreateBitmapSourceFromHBitmap</c>: that reads 32bpp as Bgr32 and drops the alpha channel.</summary>
    internal static ImageSource? Adopt(nint bitmap)
    {
        if (bitmap == 0) return null;

        try
        {
            var dib = default(DIBSECTION);
            if (GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0 || dib.Bitmap.Bits == 0)
                return Fallback(bitmap);

            var format = dib.BitCount switch
            {
                32 => PixelFormats.Pbgra32,
                24 => PixelFormats.Bgr24,
                _ => default,
            };

            if (format == default) return Fallback(bitmap);

            var image = BitmapSource.Create(dib.Bitmap.Width, dib.Bitmap.Height, 96, 96, format, null,
                Rows(dib), dib.Bitmap.WidthBytes);
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    private static ImageSource? Fallback(nint bitmap)
    {
        var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    /// <summary>Bottom-up is the DIB default; the rows come back in drawing order either way.</summary>
    private static byte[] Rows(DIBSECTION dib)
    {
        int stride = dib.Bitmap.WidthBytes, height = dib.Bitmap.Height;
        var pixels = new byte[stride * height];

        if (dib.HeaderHeight < 0)
        {
            Marshal.Copy(dib.Bitmap.Bits, pixels, 0, pixels.Length);
            return pixels;
        }

        for (var y = 0; y < height; y++)
            Marshal.Copy(dib.Bitmap.Bits + (height - 1 - y) * stride, pixels, y * stride, stride);

        return pixels;
    }
}
