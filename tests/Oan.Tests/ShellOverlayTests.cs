using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The badge in the corner. The subject is a .url file, written by the test rather than found,
/// because an .url is a text file that Windows still gives the shortcut arrow — so this runs on
/// any machine without needing a .lnk to exist or a privilege to make one.
/// </summary>
[Collection(DiskBound.Name)]
public class ShellOverlayTests
{
    [Fact]
    public void A_shortcut_carries_a_badge_and_ordinary_things_do_not()
    {
        using var corpus = new ThumbnailCorpus();

        Assert.NotEqual(ShellOverlays.NoOverlay, OnSta.Run(() => ShellOverlays.IndexFor(corpus.UrlShortcut)));
        Assert.Equal(ShellOverlays.NoOverlay, OnSta.Run(() => ShellOverlays.IndexFor(corpus.NoExtension)));
        Assert.Equal(ShellOverlays.NoOverlay, OnSta.Run(() => ShellOverlays.IndexFor(corpus.Folder)));
    }

    /// <summary>
    /// Without this a folder of shortcuts draws exactly like a folder of real files, which is the
    /// whole complaint.
    /// </summary>
    [Fact]
    public void The_badge_is_there_for_a_shortcut_and_absent_for_a_file()
    {
        using var corpus = new ThumbnailCorpus();

        Assert.NotNull(OnSta.Run(() => ShellOverlays.For(corpus.UrlShortcut, 32)));
        Assert.Null(OnSta.Run(() => ShellOverlays.For(corpus.NoExtension, 32)));
    }

    /// <summary>
    /// A badge is a badge and not a second icon: it covers a corner, so it must ink far less of
    /// its square than a whole icon does, and it must leave the opposite corner untouched.
    /// </summary>
    [Fact]
    public void The_badge_is_a_corner_and_not_a_whole_icon()
    {
        using var corpus = new ThumbnailCorpus();
        var overlay = OnSta.Run(() => ShellOverlays.IndexFor(corpus.UrlShortcut));
        var badge = Assert.IsAssignableFrom<BitmapSource>(OnSta.Run(() => ShellOverlays.Badge(overlay, 48)));
        var icon = Assert.IsAssignableFrom<BitmapSource>(ShellIcons.Get(corpus.UrlShortcut, false, 48));

        var drawn = Ink(badge);
        Assert.True(drawn > 0, "the badge is empty");
        Assert.True(drawn * 2 < Ink(icon), $"the badge inks {drawn} pixels of an icon's {Ink(icon)}: that is an icon");
        Assert.Equal(0, InkIn(badge, badge.PixelWidth / 2, 0));
    }

    [Fact]
    public void Every_size_gets_its_own_badge_and_they_are_cached()
    {
        using var corpus = new ThumbnailCorpus();
        var overlay = OnSta.Run(() => ShellOverlays.IndexFor(corpus.UrlShortcut));

        foreach (var size in new[] { 16, 32, 48, 256 })
        {
            var badge = Assert.IsAssignableFrom<BitmapSource>(OnSta.Run(() => ShellOverlays.Badge(overlay, size)));

            Assert.Equal(size, badge.PixelWidth);
            Assert.True(badge.IsFrozen);
            Assert.Same(badge, OnSta.Run(() => ShellOverlays.Badge(overlay, size)));
        }
    }

    [Fact]
    public void Nothing_to_draw_is_null_rather_than_an_empty_square()
    {
        Assert.Null(ShellOverlays.Badge(ShellOverlays.NoOverlay, 32));
        Assert.Null(ShellOverlays.Badge(-1, 32));
        Assert.Null(ShellOverlays.Badge(99, 32));
    }

    [Fact]
    public void Unusable_input_comes_back_rather_than_throwing()
    {
        Assert.Equal(ShellOverlays.NoOverlay, ShellOverlays.IndexFor(""));
        Assert.Equal(ShellOverlays.NoOverlay, ShellOverlays.IndexFor("   "));
        Assert.Equal(ShellOverlays.NoOverlay, OnSta.Run(() => ShellOverlays.IndexFor("::this is not a path::")));
    }

    [Fact]
    public void Clear_drops_the_badges()
    {
        using var corpus = new ThumbnailCorpus();
        var overlay = OnSta.Run(() => ShellOverlays.IndexFor(corpus.UrlShortcut));
        var before = OnSta.Run(() => ShellOverlays.Badge(overlay, 32));

        ShellOverlays.Clear();

        Assert.NotSame(before, OnSta.Run(() => ShellOverlays.Badge(overlay, 32)));
    }

    /// <summary>
    /// Why the badge is read out of a difference instead of asked for. IImageList::GetOverlayImage
    /// is the documented way and on this machine it answers S_OK with the same index for every one
    /// of the fifteen slots, which cannot be right. GetImageCount, four slots earlier in the same
    /// vtable, answers correctly — so the declaration is right and the shell's lists simply hold
    /// no overlay art. If a future Windows starts answering properly this test goes red, and the
    /// difference in ShellOverlays can be deleted.
    /// </summary>
    [Fact]
    public void The_shell_still_will_not_hand_over_an_overlay_image()
    {
        var answers = OnSta.Run(() =>
        {
            if (Native.SHGetImageList(Native.SHIL_SMALL, Native.IID_IImageList, out var handle) != 0)
                return [];
            if (Marshal.GetObjectForIUnknown(handle) is not ThumbnailNative.IImageList list)
                return Array.Empty<int>();

            Assert.Equal(0, list.GetImageCount(out var count));
            Assert.True(count > 1, $"the small image list holds {count} images, which is not a shell image list");

            return Enumerable.Range(1, 15)
                .Select(overlay => list.GetOverlayImage(overlay, out var index) < 0 ? -1 : index)
                .ToArray();
        });

        Assert.NotEmpty(answers);
        Assert.Single(answers.Distinct());
    }

    private static int Ink(BitmapSource image) => InkIn(image, 0, 0);

    /// <summary>Non-transparent pixels in the square starting at (x, y).</summary>
    private static int InkIn(BitmapSource image, int x, int y)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);

        var ink = 0;
        for (var row = y; row < image.PixelHeight; row++)
            for (var column = x; column < image.PixelWidth; column++)
                if (pixels[row * stride + column * 4 + 3] != 0)
                    ink++;

        return ink;
    }
}
