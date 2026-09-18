using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// These call the real shell, so they mostly assert cache identity and keying rather than pixels.
/// Every test that claims something is cached asserts Same on a repeat lookup — NotSame alone
/// would pass with the cache deleted, because each miss builds a fresh bitmap.
/// Where pixels are unavoidable the assertion is one no machine can fail for the wrong reason:
/// "these two icons are not the same image", never "this icon looks like that".
/// The context menu and the shell verbs put UI on screen and are not covered here.
/// </summary>
public class ShellIconsTests
{
    [Fact]
    public void FilesOfTheSameKindShareOneIcon()
    {
        var a = ShellIcons.Get(@"C:\one\alpha.txt", isDirectory: false);
        var b = ShellIcons.Get(@"C:\two\beta.txt", isDirectory: false);

        Assert.NotNull(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void FilesWithNoExtensionShareOneIcon()
    {
        var license = ShellIcons.Get(@"C:\one\LICENSE", isDirectory: false);
        var makefile = ShellIcons.Get(@"C:\two\Makefile", isDirectory: false);

        Assert.NotNull(license);
        Assert.Same(license, makefile);
    }

    [Fact]
    public void ExecutablesAreKeyedByPath()
    {
        var explorer = ShellIcons.Get(@"C:\Windows\explorer.exe", isDirectory: false);
        var notepad = ShellIcons.Get(@"C:\Windows\notepad.exe", isDirectory: false);

        Assert.NotNull(explorer);
        Assert.NotNull(notepad);
        Assert.NotSame(explorer, notepad);
        Assert.Same(explorer, ShellIcons.Get(@"C:\Windows\explorer.exe", isDirectory: false));
    }

    [Fact]
    public void FoldersAreKeyedByPath()
    {
        var windows = ShellIcons.Get(@"C:\Windows", isDirectory: true);
        var users = ShellIcons.Get(@"C:\Users", isDirectory: true);

        Assert.NotNull(windows);
        Assert.NotNull(users);
        Assert.NotSame(windows, users);
        Assert.Same(windows, ShellIcons.Get(@"C:\Windows", isDirectory: true));
    }

    [Fact]
    public void SizeIsPartOfTheKey()
    {
        var small = ShellIcons.Get(@"C:\one\alpha.doc", isDirectory: false, size: 16);
        var large = ShellIcons.Get(@"C:\one\alpha.doc", isDirectory: false, size: 32);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.NotSame(small, large);
        Assert.Same(small, ShellIcons.Get(@"C:\one\alpha.doc", isDirectory: false, size: 16));
        Assert.Same(large, ShellIcons.Get(@"C:\one\alpha.doc", isDirectory: false, size: 32));
    }

    [Fact]
    public void IconsAreFrozenSoAnyThreadCanBindThem()
    {
        var icon = ShellIcons.Get(@"C:\one\alpha.png", isDirectory: false);

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
    }

    [Fact]
    public void ClearDropsTheCache()
    {
        var before = ShellIcons.Get(@"C:\one\gamma.rtf", isDirectory: false);
        Assert.NotNull(before);
        Assert.Same(before, ShellIcons.Get(@"C:\one\gamma.rtf", isDirectory: false));

        ShellIcons.Clear();

        Assert.NotSame(before, ShellIcons.Get(@"C:\one\gamma.rtf", isDirectory: false));
    }

    [Fact]
    public void UnusableInputReturnsNullInsteadOfThrowing()
    {
        Assert.Null(ShellIcons.Get("", isDirectory: false));
        Assert.Null(ShellIcons.Get("   ", isDirectory: true));

        _ = ShellIcons.Get("::this is not a path::", isDirectory: false);
        _ = ShellIcons.Get(new string('x', 500) + ".txt", isDirectory: false);
    }

    [Fact]
    public void SizeIsPartOfTheKeyForItemsKeyedByPathToo()
    {
        var small = ShellIcons.Get(@"C:\Windows", isDirectory: true, size: 16);
        var large = ShellIcons.Get(@"C:\Windows", isDirectory: true, size: 48);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.Same(small, ShellIcons.Get(@"C:\Windows", isDirectory: true, size: 16));
        Assert.Same(large, ShellIcons.Get(@"C:\Windows", isDirectory: true, size: 48));
        Assert.Equal(16, ((BitmapSource)small).PixelWidth);
        Assert.Equal(48, ((BitmapSource)large).PixelWidth);
    }

    /// <summary>
    /// Four separate shell image lists back these four numbers. If the size parameter did not
    /// reach SHGetImageList they would all return the same 16px bitmap for WPF to blur.
    /// </summary>
    [Fact]
    public void EachSizeComesBackAtItsOwnPixelSize()
    {
        using var temp = new TempFolder();

        foreach (var size in new[] { 16, 32, 48, 256 })
        {
            var icon = (BitmapSource?)ShellIcons.Get(temp.Root, isDirectory: true, size);

            Assert.NotNull(icon);
            Assert.Equal(size, icon.PixelWidth);
            Assert.Equal(size, icon.PixelHeight);
        }
    }

    /// <summary>
    /// The jumbo list pads instead of scaling, so an item with no 256px asset comes back as a
    /// 256px bitmap holding a 48px icon at the origin — on screen, a speck in an empty box.
    /// An item that really does have 256px art fills its bitmap and passes this unchanged.
    /// </summary>
    [Fact]
    public void JumboNeverReturnsASpeckFloatingInAnEmptyBox()
    {
        using var temp = new TempFolder();

        foreach (var (label, path, isDirectory) in new[]
                 {
                     ("custom folder icon", WithCustomIcon(temp), true),
                     ("plain folder", temp.Root, true),
                     ("text file", temp.AddFile("note.txt"), false),
                     ("word document", temp.AddFile("letter.docx"), false),
                 })
        {
            var icon = (BitmapSource?)ShellIcons.Get(path, isDirectory, size: 256);

            Assert.NotNull(icon);
            var drawn = DrawnExtent(icon);
            Assert.True(drawn * 2 >= icon.PixelWidth,
                $"{label}: draws {drawn}px of a {icon.PixelWidth}px bitmap, the rest is padding.");
        }
    }

    [Fact]
    public void AFolderWithItsOwnIconIsNotTheGenericFolder()
    {
        using var temp = new TempFolder();
        var custom = WithCustomIcon(temp);

        foreach (var size in new[] { 16, 32, 48 })
            Assert.NotEqual(
                Pixels(ShellIcons.Get(temp.Root, isDirectory: true, size)),
                Pixels(ShellIcons.Get(custom, isDirectory: true, size)));
    }

    /// <summary>
    /// SHGFI_USEFILEATTRIBUTES would answer from the name alone and hand back the generic folder
    /// for every one of these.
    /// </summary>
    [Fact]
    public void KnownFoldersKeepTheirOwnIcons()
    {
        using var temp = new TempFolder();
        var generic = Pixels(ShellIcons.Get(temp.Root, isDirectory: true, size: 32));

        var known = new[]
            {
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos,
            }
            .Select(Environment.GetFolderPath)
            .Where(Directory.Exists)
            .ToList();

        Assert.NotEmpty(known);
        foreach (var path in known)
            Assert.NotEqual(generic, Pixels(ShellIcons.Get(path, isDirectory: true, size: 32)));
    }

    /// <summary>A folder whose icon the shell can only learn by reading the folder itself.</summary>
    static string WithCustomIcon(TempFolder temp)
    {
        var folder = temp.AddFolder("Custom");
        var ini = Path.Combine(folder, "desktop.ini");

        File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=%SystemRoot%\\system32\\SHELL32.dll,44\r\n");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        // Without +S on the folder the shell never reads desktop.ini at all.
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.System);
        return folder;
    }

    static byte[] Pixels(ImageSource? source)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];

        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>The side of the square at the origin holding every non-transparent pixel.</summary>
    static int DrawnExtent(BitmapSource icon)
    {
        var pixels = Pixels(icon);
        var stride = icon.PixelWidth * 4;
        var extent = 0;

        for (var y = 0; y < icon.PixelHeight; y++)
            for (var x = icon.PixelWidth - 1; x >= 0; x--)
                if (pixels[y * stride + x * 4 + 3] != 0)
                {
                    extent = Math.Max(extent, Math.Max(x + 1, y + 1));
                    break;
                }

        return extent;
    }
}
