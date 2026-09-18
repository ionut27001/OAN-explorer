using Oan.Core.Shell;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// These call the real shell, so they assert cache identity and keying rather than pixels.
/// Every test that claims something is cached asserts Same on a repeat lookup — NotSame alone
/// would pass with the cache deleted, because each miss builds a fresh bitmap.
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
}
