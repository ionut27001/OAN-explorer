using Oan.Core.Operations;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Pasting into the folder the items already live in.
///
/// Measured before the fix: the shell found the name taken, had nothing to ask, and returned
/// success having written nothing — no file, no message, no error. Explorer answers this with
/// "alpha - Copy.txt", and FOF_RENAMEONCOLLISION is what produces it. The flag must stay off
/// everywhere else, or a genuine conflict is silently renamed round instead of asked about.
/// </summary>
public class PasteIntoOwnFolderTests
{
    private static OperationRequest Copy(string destination, params string[] sources) =>
        new() { Kind = OperationKind.Copy, Sources = sources, Destination = destination };

    [Fact]
    public void Same_folder_is_recognised()
    {
        Assert.True(ShellFileOperations.IsPasteIntoOwnFolder(
            Copy(@"C:\work", @"C:\work\alpha.txt", @"C:\work\beta.txt")));
    }

    [Fact]
    public void A_different_folder_is_not()
    {
        Assert.False(ShellFileOperations.IsPasteIntoOwnFolder(
            Copy(@"C:\work\sub", @"C:\work\alpha.txt")));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        Assert.True(ShellFileOperations.IsPasteIntoOwnFolder(
            Copy(@"C:\work\", @"C:\work\alpha.txt")));
    }

    [Fact]
    public void Case_does_not_change_the_answer()
    {
        Assert.True(ShellFileOperations.IsPasteIntoOwnFolder(
            Copy(@"c:\WORK", @"C:\work\alpha.txt")));
    }

    [Fact]
    public void A_mixed_selection_is_not_a_same_folder_paste()
    {
        // One item from elsewhere means a real conflict is possible, and the shell must be
        // allowed to ask rather than quietly renaming.
        Assert.False(ShellFileOperations.IsPasteIntoOwnFolder(
            Copy(@"C:\work", @"C:\work\alpha.txt", @"D:\other\alpha.txt")));
    }

    [Fact]
    public void No_destination_is_not_a_same_folder_paste()
    {
        Assert.False(ShellFileOperations.IsPasteIntoOwnFolder(
            new OperationRequest { Kind = OperationKind.Copy, Sources = [@"C:\work\alpha.txt"] }));
    }

    [Fact]
    public void An_empty_selection_is_not_a_same_folder_paste()
    {
        Assert.False(ShellFileOperations.IsPasteIntoOwnFolder(Copy(@"C:\work")));
    }
}
