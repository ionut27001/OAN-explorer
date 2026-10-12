using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// These call the real shell, so the assertions are ones no machine can fail for the wrong reason:
/// "these two files do not draw the same picture", "this file's picture changed when the file
/// did", never "this thumbnail looks like that". Everything runs on an STA, because that is the
/// apartment a thumbnail provider expects and a test that gets the answer through a marshalling
/// host is testing something the app never does.
/// </summary>
[Collection(DiskBound.Name)]
public class ThumbnailTests
{
    /// <summary>
    /// The whole reason the engine exists: today a folder of photographs draws one JPEG icon four
    /// hundred times, which is less than the details list already told you.
    /// </summary>
    [Fact]
    public void Two_photographs_do_not_draw_the_same_picture()
    {
        using var corpus = new ThumbnailCorpus();
        var other = Path.Combine(corpus.Root, "other.jpg");
        File.Copy(corpus.Png, other.Replace(".jpg", ".png"));

        var one = Picture(corpus.Jpeg, 256);
        var two = Picture(corpus.Png, 256);

        Assert.Equal(ThumbnailKind.Extracted, one.Kind);
        Assert.Equal(ThumbnailKind.Extracted, two.Kind);
        Assert.NotEqual(Pixels(one.Image), Pixels(two.Image));

        // And neither is the type icon, which is what makes it a thumbnail rather than a lookup.
        Assert.NotEqual(Pixels(ShellIcons.Get(corpus.Jpeg, false, 256)), Pixels(one.Image));
    }

    /// <summary>1600x1200 into a 256 box is 256x192 — fitted, not squashed and not padded.</summary>
    [Fact]
    public void The_picture_fits_the_box_and_keeps_its_shape()
    {
        using var corpus = new ThumbnailCorpus();
        var picture = Assert.IsAssignableFrom<BitmapSource>(Picture(corpus.Jpeg, 256).Image);

        Assert.Equal(256, picture.PixelWidth);
        Assert.Equal(192, picture.PixelHeight);
    }

    /// <summary>
    /// The details view is 16px and keeps its icons: a photograph at 16 comes back 16x12, fewer
    /// pixels than the icon it would replace. Nothing is asked of the shell at all.
    /// </summary>
    [Fact]
    public void Below_the_smallest_view_nothing_is_asked()
    {
        using var corpus = new ThumbnailCorpus();

        var small = OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 16, extract: true));

        Assert.Equal(ThumbnailKind.NotCached, small.Kind);
        Assert.Null(small.Image);
    }

    /// <summary>
    /// The shell hands back a 32bpp DIB with an alpha channel. Read the obvious way — WPF's
    /// CreateBitmapSourceFromHBitmap — that alpha is dropped and every transparent pixel comes out
    /// black, so an icon arrives in a black box. Asked for an icon rather than a thumbnail, the
    /// factory returns art with transparent corners, which is what proves the alpha survived.
    ///
    /// A thumbnail does not: measured, the shell flattens a PNG's transparent corner to opaque
    /// before handing it over, so a photograph never carries alpha and this could not be shown
    /// with one.
    /// </summary>
    [Fact]
    public void The_alpha_channel_survives_being_read()
    {
        using var corpus = new ThumbnailCorpus();
        var icon = Assert.IsAssignableFrom<BitmapSource>(
            OnSta.Run(() => Factory(corpus.NoExtension, 48, ThumbnailNative.Siigbf.IconOnly)));

        var pixels = Pixels(icon);
        var opaque = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) opaque++;

        Assert.True(opaque > 0, "nothing at all is drawn");
        Assert.True(opaque < pixels.Length / 4, "every pixel is opaque: the alpha channel was lost");
    }

    /// <summary>
    /// A thumbnail is per file, so the key cannot be the extension the way an icon's is — two
    /// JPEGs must not share one picture, and the same JPEG twice must not be decoded twice.
    /// </summary>
    [Fact]
    public void The_key_is_the_file_and_not_its_kind()
    {
        using var corpus = new ThumbnailCorpus();
        var copy = Path.Combine(corpus.Root, "copy.jpg");
        File.Copy(corpus.Jpeg, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(corpus.Jpeg).AddMinutes(-1));

        Thumbnails.Clear();
        var original = OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: true));
        var duplicate = OnSta.Run(() => Thumbnails.Get(copy, 256, extract: true));

        Assert.NotNull(original.Image);
        Assert.NotSame(original.Image, duplicate.Image);
        Assert.Same(original.Image, OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: true)).Image);
    }

    /// <summary>
    /// A picture of a file that has since changed is a wrong answer shown confidently, which is
    /// worse than no picture. The file's timestamp and length are part of the key, so a rewritten
    /// file cannot be served the old one.
    /// </summary>
    [Fact]
    public void A_file_that_changed_is_not_served_its_old_picture()
    {
        using var corpus = new ThumbnailCorpus();
        var before = Picture(corpus.Jpeg, 256);

        var other = new ThumbnailCorpus();
        using (other)
        {
            File.Copy(other.Png, corpus.Jpeg, overwrite: true);
            File.SetLastWriteTimeUtc(corpus.Jpeg, DateTime.UtcNow.AddSeconds(5));
        }

        var after = OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: true));

        Assert.NotSame(before.Image, after.Image);
        Assert.NotEqual(Pixels(before.Image), Pixels(after.Image));
    }

    /// <summary>
    /// "Nothing can draw this" and "we could not find out" are different answers and the row
    /// treats them differently: the first is the type icon forever, the second is worth counting.
    /// </summary>
    [Fact]
    public void A_kind_with_no_provider_says_so_instead_of_failing()
    {
        using var corpus = new ThumbnailCorpus();

        var none = OnSta.Run(() => Thumbnails.Get(corpus.NoExtension, 256, extract: true));

        Assert.Equal(ThumbnailKind.None, none.Kind);
        Assert.Null(none.Image);
    }

    [Fact]
    public void A_file_that_is_not_there_is_unavailable_and_not_pictureless()
    {
        using var corpus = new ThumbnailCorpus();

        var gone = OnSta.Run(() => Thumbnails.Get(Path.Combine(corpus.Root, "never-existed.jpg"), 256, extract: true));

        Assert.Equal(ThumbnailKind.Unavailable, gone.Kind);
    }

    /// <summary>The fast pass reads Windows' cache and nothing else, so a file nobody has ever
    /// looked at comes back empty rather than being decoded behind the caller's back.</summary>
    [Fact]
    public void The_fast_pass_does_not_decode_anything()
    {
        using var corpus = new ThumbnailCorpus();

        var first = OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: false));
        Assert.Equal(ThumbnailKind.NotCached, first.Kind);

        OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: true));
        Thumbnails.Clear();

        Assert.Equal(ThumbnailKind.Cached, OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: false)).Kind);
    }

    [Fact]
    public void Unusable_input_comes_back_rather_than_throwing()
    {
        Assert.Equal(ThumbnailKind.NotCached, Thumbnails.Get("", 256, extract: true).Kind);
        Assert.Equal(ThumbnailKind.NotCached, Thumbnails.Get("   ", 256, extract: true).Kind);
        Assert.Equal(ThumbnailKind.Unavailable,
            OnSta.Run(() => Thumbnails.Get("::this is not a path::", 256, extract: true)).Kind);
    }

    /// <summary>
    /// One 256px picture is a quarter of a megabyte, so the cache is bounded in bytes rather than
    /// in entries. The budget is lowered here because proving the eviction at the real one means
    /// decoding sixty-four megabytes of photographs, which is a minute of disk for one assertion.
    /// </summary>
    [Fact]
    public void The_cache_is_bounded_in_bytes_and_drops_the_oldest()
    {
        using var corpus = new ThumbnailCorpus();
        var budget = Thumbnails.Budget;

        try
        {
            Thumbnails.Clear();
            Thumbnails.Budget = 300_000;

            var first = Path.Combine(corpus.Root, "a.jpg");
            File.Copy(corpus.Jpeg, first);

            OnSta.Run(() => Thumbnails.Get(first, 256, extract: true));
            Assert.Equal(1, Thumbnails.Held.Entries);

            for (var i = 0; i < 4; i++)
            {
                var next = Path.Combine(corpus.Root, $"b{i}.jpg");
                File.Copy(corpus.Jpeg, next);
                OnSta.Run(() => Thumbnails.Get(next, 256, extract: true));
            }

            Assert.True(Thumbnails.Held.Bytes <= Thumbnails.Budget,
                $"holding {Thumbnails.Held.Bytes} bytes against a budget of {Thumbnails.Budget}");
            Assert.True(Thumbnails.Held.Entries < 5, $"kept {Thumbnails.Held.Entries} entries, evicted none");
        }
        finally
        {
            Thumbnails.Budget = budget;
            Thumbnails.Clear();
        }
    }

    [Fact]
    public void Clear_drops_the_cache()
    {
        using var corpus = new ThumbnailCorpus();
        var before = Picture(corpus.Jpeg, 256);

        Thumbnails.Clear();

        Assert.NotSame(before.Image, OnSta.Run(() => Thumbnails.Get(corpus.Jpeg, 256, extract: true)).Image);
    }

    /// <summary>What the status bar is allowed to say, and what it must not: a text file with no
    /// preview is not a failure and does not get a sentence.</summary>
    [Fact]
    public void Only_the_items_that_went_wrong_are_reported()
    {
        var quiet = new ThumbnailTally()
            .Plus(ThumbnailKind.Extracted).Plus(ThumbnailKind.Cached).Plus(ThumbnailKind.None);

        Assert.Null(quiet.Note);
        Assert.Equal(2, quiet.Pictures);
        Assert.Equal(1, quiet.NoPicture);

        var loud = quiet.Plus(ThumbnailKind.Unavailable);
        Assert.Equal("1 item in this folder could not be previewed", loud.Note);
        Assert.Equal("2 items in this folder could not be previewed", loud.Plus(ThumbnailKind.Unavailable).Note);
    }

    private static ImageSource? Factory(string path, int size, ThumbnailNative.Siigbf flags)
    {
        var iid = ThumbnailNative.IID_IShellItemImageFactory;
        Assert.True(ThumbnailNative.SHCreateItemFromParsingName(path, 0, in iid, out var factory) >= 0);

        var box = new ThumbnailNative.SIZE { Cx = size, Cy = size };
        Assert.True(factory.GetImage(box, flags, out var bitmap) >= 0);
        return ThumbnailNative.Adopt(bitmap);
    }

    private static Thumbnail Picture(string path, int size)
    {
        Thumbnails.Clear();
        return OnSta.Run(() => Thumbnails.Get(path, size, extract: true));
    }

    private static byte[] Pixels(ImageSource? source)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];

        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
