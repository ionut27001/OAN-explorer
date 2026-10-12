using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.Core.Shell;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What a thumbnail costs against what an icon costs, per kind of file and per size, and which
/// kinds on this machine have a picture at all. The numbers behind docs/thumbnails.md.
///
/// Kept out of the normal pass by Category=Benchmark, which tests/Oan.Tests/Oan.Tests.csproj
/// excludes. Run it on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~Thumbnail" --logger "console;verbosity=detailed"
/// OAN_THUMB_MP4 and OAN_THUMB_PDF name real files for the two kinds that cannot be synthesised.
/// OAN_THUMB_HITRATE names a folder to measure the in-cache pass against; it defaults to Pictures.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class ThumbnailBenchmark(ITestOutputHelper output)
{
    private static readonly int[] Sizes = [16, 32, 48, 256];

    /// <summary>Above this a fresh copy per repetition costs more disk than the measurement is
    /// worth, so those subjects are measured once and the first number is the only number.</summary>
    private const long TooBigToCopy = 32L * 1024 * 1024;

    [Fact]
    public void Thumbnail_against_icon_for_every_kind_at_every_size()
    {
        using var corpus = new ThumbnailCorpus();
        Warm(corpus);

        foreach (var (label, path, isDirectory) in Subjects(corpus))
        {
            if (path is null)
            {
                output.WriteLine($"thumb\t{label}\tmissing on this machine, not measured");
                continue;
            }

            foreach (var size in Sizes) Row(corpus, label, path, isDirectory, size);
        }
    }

    /// <summary>
    /// icons.md claims IShellItemImageFactory is byte-identical to the image list at 16, 32 and 48
    /// and about three times the cost. That claim is why ShellIcons stays as it is, so it is
    /// re-measured rather than believed — and reported as how far apart the two are, because a
    /// yes/no hides the difference between one rounded channel and a different picture.
    /// </summary>
    [Fact]
    public void The_factory_against_the_image_list_for_items_with_no_picture()
    {
        using var corpus = new ThumbnailCorpus();
        Warm(corpus);

        foreach (var (label, path, isDirectory) in Subjects(corpus))
        {
            if (path is null || label is "jpeg" or "png" or "mp4" or "folder of pictures") continue;

            foreach (var size in Sizes)
            {
                var icon = Median(5, () => { ShellIcons.Clear(); return ShellIcons.Get(path, isDirectory, size); });
                var factory = Median(5, () => Raw(path, size, ThumbnailNative.Siigbf.IconOnly));

                output.WriteLine(
                    $"identical\t{label}\tsize={size}\ticon_ms={icon.Ms:0.00}\tfactory_ms={factory.Ms:0.00}\t" +
                    $"ratio={(icon.Ms <= 0 ? 0 : factory.Ms / icon.Ms):0.0}\t{Compare(icon.Result, factory.Result)}");
            }
        }
    }

    /// <summary>
    /// How often the fast pass answers on a folder the user has actually browsed. This is the
    /// number that decides whether asking in-cache-first is worth a call per row.
    /// </summary>
    [Fact]
    public void The_in_cache_only_pass_on_a_folder_that_has_been_looked_at()
    {
        var folder = Environment.GetEnvironmentVariable("OAN_THUMB_HITRATE")
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (!Directory.Exists(folder))
        {
            output.WriteLine($"hitrate\tmissing\t{folder}");
            return;
        }

        var files = Directory.EnumerateFiles(folder).Take(200).ToArray();
        if (files.Length == 0)
        {
            output.WriteLine($"hitrate\tempty\t{folder}");
            return;
        }

        foreach (var size in new[] { 32, 48, 256 })
        {
            Thumbnails.Clear();
            var tally = new ThumbnailTally();
            var clock = Stopwatch.StartNew();

            foreach (var file in files)
                tally = tally.Plus(OnSta.Run(() => Thumbnails.Get(file, size, extract: false)).Kind);

            // The same pass without the engine's rule about a cached picture being too small for
            // the box asked for: the gap between the two columns is what that rule turns away.
            int held = 0, tooSmall = 0;
            foreach (var file in files)
            {
                var found = OnSta.Run(() => Raw(file, size,
                    ThumbnailNative.Siigbf.ThumbnailOnly | ThumbnailNative.Siigbf.InCacheOnly));

                if (found is not BitmapSource picture) continue;

                held++;
                if (Math.Max(picture.PixelWidth, picture.PixelHeight) * 2 < size) tooSmall++;
            }

            output.WriteLine(
                $"hitrate\tsize={size}\t{folder}\tfiles={files.Length}\tused={tally.Pictures}\t" +
                $"rate={100.0 * tally.Pictures / files.Length:0.0}%\tcache_held={held}\ttoo_small={tooSmall}\t" +
                $"total_ms={clock.ElapsedMilliseconds}\tper_file_ms={(double)clock.ElapsedMilliseconds / files.Length:0.00}");
        }
    }

    /// <summary>What the overlay pass adds, which is paid on every row of every folder.</summary>
    [Fact]
    public void What_asking_for_the_overlay_index_costs()
    {
        using var corpus = new ThumbnailCorpus();
        Warm(corpus);

        foreach (var (label, path, isDirectory) in Subjects(corpus))
        {
            if (path is null) continue;

            var overlay = Median(5, () => ShellOverlays.IndexFor(path));
            var icon = Median(5, () => { ShellIcons.Clear(); return ShellIcons.Get(path, isDirectory, 32); });
            var cachedIcon = Median(5, () => ShellIcons.Get(path, isDirectory, 32));

            output.WriteLine(
                $"overlay\t{label}\tindex={overlay.Result}\toverlay_ms={overlay.Ms:0.00}\t" +
                $"icon_cold_ms={icon.Ms:0.00}\ticon_cached_ms={cachedIcon.Ms:0.000}");
        }

        foreach (var size in Sizes)
        {
            ShellOverlays.Clear();
            var badge = Median(1, () => ShellOverlays.Badge(2, size));
            var again = Median(5, () => ShellOverlays.Badge(2, size));

            output.WriteLine($"badge\tsize={size}\tfirst_ms={badge.Ms:0.00}\tcached_ms={again.Ms:0.000}\t" +
                             $"px={Shape(badge.Result)}\tink={Ink(badge.Result)}");
        }
    }

    /// <summary>
    /// The factory is called raw here, not through <see cref="Thumbnails"/>, because the engine
    /// refuses the small sizes on purpose and this is the measurement that has to justify the
    /// refusal. The cold column is a fresh copy every repetition — a file Windows has never seen —
    /// so it is not quietly measuring the second look.
    /// </summary>
    private void Row(ThumbnailCorpus corpus, string label, string path, bool isDirectory, int size)
    {
        const ThumbnailNative.Siigbf thumbnail = ThumbnailNative.Siigbf.ThumbnailOnly;
        const ThumbnailNative.Siigbf cacheOnly = thumbnail | ThumbnailNative.Siigbf.InCacheOnly;

        var icon = Median(5, () => { ShellIcons.Clear(); return ShellIcons.Get(path, isDirectory, size); });
        var repeats = Copyable(path) ? 3 : 1;

        var before = Median(repeats, () => Raw(corpus.Fresh(path), size, cacheOnly));
        var cold = Median(repeats, () => Raw(corpus.Fresh(path), size, thumbnail));

        Raw(path, size, thumbnail);
        var after = Median(5, () => Raw(path, size, cacheOnly));

        output.WriteLine(
            $"thumb\t{label}\tsize={size}\t" +
            $"icon_ms={icon.Ms:0.00}\ticon_px={Shape(icon.Result)}\ticon_bytes={Bytes(icon.Result)}\t" +
            $"incache_cold_ms={before.Ms:0.00}\tincache_cold={Shape(before.Result)}\t" +
            $"extract_ms={cold.Ms:0.00}\tthumb_px={Shape(cold.Result)}\tthumb_bytes={Bytes(cold.Result)}\t" +
            $"incache_warm_ms={after.Ms:0.00}\tincache_warm={Shape(after.Result)}");
    }

    private static bool Copyable(string path) =>
        File.Exists(path) && new FileInfo(path).Length < TooBigToCopy;

    /// <summary>One pass over everything before the clock starts: the first shell call in a
    /// process pays for COM, the image lists and the providers, and that is not per-file cost.</summary>
    private static void Warm(ThumbnailCorpus corpus)
    {
        OnSta.Run(() =>
        {
            ShellIcons.Get(corpus.Jpeg, false, 256);
            ShellOverlays.IndexFor(corpus.Jpeg);
            Raw(corpus.Jpeg, 256, ThumbnailNative.Siigbf.ThumbnailOnly);
        });
    }

    private static (string Label, string? Path, bool IsDirectory)[] Subjects(ThumbnailCorpus corpus) =>
    [
        ("jpeg", corpus.Jpeg, false),
        ("png", corpus.Png, false),
        ("pdf", corpus.Pdf, false),
        ("mp4", corpus.Mp4, false),
        ("docx", corpus.Docx, false),
        ("folder", corpus.Folder, true),
        ("folder of pictures", corpus.PictureFolder, true),
        ("exe", ThumbnailCorpus.Exe, false),
        ("no extension", corpus.NoExtension, false),
        ("shortcut (.lnk)", corpus.Shortcut, false),
        ("shortcut (.url)", corpus.UrlShortcut, false),
    ];

    /// <summary>The raw factory call, bypassing the engine's own rules.</summary>
    private static ImageSource? Raw(string path, int size, ThumbnailNative.Siigbf flags)
    {
        var iid = ThumbnailNative.IID_IShellItemImageFactory;
        if (ThumbnailNative.SHCreateItemFromParsingName(path, 0, in iid, out var factory) < 0) return null;

        var box = new ThumbnailNative.SIZE { Cx = size, Cy = size };
        return factory.GetImage(box, flags, out var bitmap) < 0 ? null : ThumbnailNative.Adopt(bitmap);
    }

    /// <summary>The middle of several runs, not the mean: one scheduling hiccup moves a mean and
    /// not a median, and every one of these numbers is single-digit milliseconds.</summary>
    private static (T Result, double Ms) Median<T>(int runs, Func<T> job)
    {
        var times = new List<double>(runs);
        var result = default(T);

        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            result = OnSta.Run(job);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return (result!, times[times.Count / 2]);
    }

    private static string Shape(ImageSource? image) =>
        image is BitmapSource bitmap ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : "-";

    private static long Bytes(ImageSource? image) =>
        image is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : 0;

    private static int Ink(ImageSource? image)
    {
        if (image is not BitmapSource bitmap) return 0;

        var pixels = Pixels(bitmap);
        var ink = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) ink++;

        return ink;
    }

    /// <summary>How far apart two icons are: same size, how many pixels differ, and by how much.</summary>
    private static string Compare(ImageSource? a, ImageSource? b)
    {
        if (a is not BitmapSource left || b is not BitmapSource right) return "same_pixels=-";
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
            return $"same_pixels=no\tsizes={left.PixelWidth}x{left.PixelHeight} vs {right.PixelWidth}x{right.PixelHeight}";

        byte[] one = Pixels(left), two = Pixels(right);
        int differing = 0, worst = 0;

        for (var i = 0; i < one.Length; i += 4)
        {
            var gap = 0;
            for (var c = 0; c < 4; c++) gap = Math.Max(gap, Math.Abs(one[i + c] - two[i + c]));
            if (gap == 0) continue;

            differing++;
            worst = Math.Max(worst, gap);
        }

        return $"same_pixels={(differing == 0 ? "yes" : "no")}\tdiffering={differing}/{one.Length / 4}\tworst_channel={worst}";
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];

        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
