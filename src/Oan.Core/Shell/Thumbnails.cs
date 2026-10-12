using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Core.Shell;

/// <summary>What a row's picture turned out to be. Not a nullable image: "not looked yet" and "never will have one" differ.</summary>
public enum ThumbnailKind
{
    /// <summary>Windows already had this picture. No provider ran, nothing was read.</summary>
    Cached,

    /// <summary>The file's own provider made it just now.</summary>
    Extracted,

    /// <summary>Not in the shell's cache, and nobody asked for the slow pass yet.</summary>
    NotCached,

    /// <summary>Nothing registered can draw this file.</summary>
    None,

    /// <summary>We could not find out — unreadable, or the provider answered with an error.</summary>
    Unavailable,
}

/// <summary>A picture, and which of the five things happened. Null image unless it is a picture.</summary>
public readonly record struct Thumbnail(ImageSource? Image, ThumbnailKind Kind)
{
    public static readonly Thumbnail NotCached = new(null, ThumbnailKind.NotCached);
}

/// <summary>What a whole folder's worth of asking came to, so items that could not be read are counted and said.</summary>
public readonly record struct ThumbnailTally(int Pictures, int NoPicture, int Unavailable)
{
    public ThumbnailTally Plus(ThumbnailKind kind) => kind switch
    {
        ThumbnailKind.Cached or ThumbnailKind.Extracted => this with { Pictures = Pictures + 1 },
        ThumbnailKind.None => this with { NoPicture = NoPicture + 1 },
        ThumbnailKind.Unavailable => this with { Unavailable = Unavailable + 1 },
        _ => this,
    };

    /// <summary>The sentence for the status bar, or null when nothing went wrong.</summary>
    public string? Note => Unavailable == 0
        ? null
        : $"{Unavailable} item{(Unavailable == 1 ? "" : "s")} in this folder could not be previewed";
}

/// <summary>Real thumbnails: the picture inside the file, not the icon of its type. Failures come back as a kind, never thrown.
/// GetImage runs a third-party IThumbnailProvider: call it off the UI thread, on an STA, under a deadline (<c>ThumbnailPump</c>).</summary>
public static class Thumbnails
{
    /// <summary>The smallest view worth a picture. Measured: a photo asked for at 16 comes back 16x12, worse than the icon it replaces.</summary>
    public const int Smallest = 32;

    /// <summary>Bounded in bytes (one 256px picture is 256 KB of Pbgra32) and evicted oldest-first, so a scroll never re-decodes the screen.
    /// Settable only for the eviction test.</summary>
    internal static long Budget = 64L * 1024 * 1024;

    private static readonly Dictionary<string, Thumbnail> Cache = [];
    private static readonly Queue<string> Order = new();
    private static long _held;

    /// <summary>The picture for <paramref name="path"/> at <paramref name="size"/> pixels, or why there is none.
    /// <paramref name="extract"/> false uses SIIGBF_INCACHEONLY: no provider loaded, no file read, safe over a whole listing.</summary>
    public static Thumbnail Get(string path, int size, bool extract)
    {
        if (string.IsNullOrWhiteSpace(path) || size < Smallest) return Thumbnail.NotCached;

        // What the file was when drawn is part of the key, so a changed file misses instead of getting a stale picture.
        if (Stamp(path) is not { } stamp) return new(null, ThumbnailKind.Unavailable);

        var key = $"{size}|{path}|{stamp}";

        lock (Cache)
        {
            // A remembered NotCached only answers another fast pass, never an extract pass.
            if (Cache.TryGetValue(key, out var cached) && (cached.Kind != ThumbnailKind.NotCached || !extract))
                return cached;
        }

        var found = Ask(path, size, extract);

        // Unavailable is not remembered: locked files and drives still spinning up recover.
        if (found.Kind != ThumbnailKind.Unavailable) Remember(key, found);

        return found;
    }

    public static void Clear()
    {
        lock (Cache)
        {
            Cache.Clear();
            Order.Clear();
            _held = 0;
        }
    }

    /// <summary>How much of the budget is spent.</summary>
    public static (int Entries, long Bytes) Held
    {
        get
        {
            lock (Cache) return (Cache.Count, _held);
        }
    }

    private static Thumbnail Ask(string path, int size, bool extract)
    {
        ThumbnailNative.IShellItemImageFactory? factory = null;

        try
        {
            var iid = ThumbnailNative.IID_IShellItemImageFactory;
            if (ThumbnailNative.SHCreateItemFromParsingName(path, 0, in iid, out factory) < 0 || factory is null)
                return new(null, ThumbnailKind.Unavailable);

            // THUMBNAILONLY, never ICONONLY: measured, the factory's icon costs 1.3-24x the image list ShellIcons reads.
            // Without THUMBNAILONLY the shell silently falls back to that icon and we would cache a 256 KB copy of it.
            var flags = ThumbnailNative.Siigbf.ThumbnailOnly | ThumbnailNative.Siigbf.ResizeToFit;
            if (!extract) flags |= ThumbnailNative.Siigbf.InCacheOnly;

            var box = new ThumbnailNative.SIZE { Cx = size, Cy = size };
            var hr = factory.GetImage(box, flags, out var bitmap);

            if (hr < 0) return new(null, extract ? ThumbnailKind.None : ThumbnailKind.NotCached);

            var image = ThumbnailNative.Adopt(bitmap);
            if (image is null) return new(null, ThumbnailKind.Unavailable);

            // Windows' cache hands back whatever fixed size it has and never scales up (asked 256, got the 48px entry).
            // Too small counts as a miss, so the extract pass runs.
            if (!extract && TooSmall(image, size)) return Thumbnail.NotCached;

            return new(image, extract ? ThumbnailKind.Extracted : ThumbnailKind.Cached);
        }
        catch (Exception)
        {
            return new(null, ThumbnailKind.Unavailable);
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Last write time and length, or null when the item is gone or unreadable.</summary>
    private static string? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists) return $"{file.LastWriteTimeUtc.Ticks}:{file.Length}";

            // A folder's timestamp does not move when a picture inside is edited; refreshing the folder corrects it.
            var folder = new DirectoryInfo(path);
            return folder.Exists ? $"{folder.LastWriteTimeUtc.Ticks}:folder" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Remember(string key, Thumbnail found)
    {
        var cost = Cost(found.Image);

        lock (Cache)
        {
            if (!Cache.TryAdd(key, found))
            {
                _held += cost - Cost(Cache[key].Image);
                Cache[key] = found;
                return;
            }

            Order.Enqueue(key);
            _held += cost;

            while (_held > Budget && Order.Count > 1)
            {
                var oldest = Order.Dequeue();
                if (!Cache.Remove(oldest, out var gone)) continue;
                _held -= Cost(gone.Image);
            }
        }
    }

    private static bool TooSmall(ImageSource image, int size) =>
        image is BitmapSource bitmap && Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) * 2 < size;

    private static long Cost(ImageSource? image) =>
        image is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : 0;
}
