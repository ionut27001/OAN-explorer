using Oan.Core.Navigation;
using Xunit;

namespace Oan.Tests;

public class HistoryTests
{
    [Fact]
    public void Starts_empty()
    {
        var history = new History();

        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
    }

    [Fact]
    public void Back_and_forward_walk_the_visited_paths()
    {
        var history = new History();
        history.Visit(@"C:\a");
        history.Visit(@"C:\b");
        history.Visit(@"C:\c");

        Assert.Equal(@"C:\b", history.Back());
        Assert.Equal(@"C:\a", history.Back());
        Assert.Equal(@"C:\a", history.Current);
        Assert.False(history.CanGoBack);

        Assert.Equal(@"C:\b", history.Forward());
        Assert.Equal(@"C:\c", history.Forward());
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Visiting_after_going_back_drops_the_forward_entries()
    {
        var history = new History();
        history.Visit(@"C:\a");
        history.Visit(@"C:\b");
        history.Visit(@"C:\c");
        history.Back();

        history.Visit(@"C:\d");

        Assert.Equal(@"C:\d", history.Current);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Forward());
        Assert.Equal(@"C:\b", history.Back());
    }

    [Fact]
    public void Visiting_the_current_path_again_adds_nothing()
    {
        var history = new History();
        history.Visit(@"C:\a");
        history.Visit(@"C:\b");
        history.Visit(@"C:\b");
        history.Visit(@"c:\B");

        Assert.Equal(@"C:\a", history.Back());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Refreshing_the_current_path_keeps_the_forward_entries()
    {
        var history = new History();
        history.Visit(@"C:\a");
        history.Visit(@"C:\b");
        history.Back();

        history.Visit(@"C:\a");

        Assert.True(history.CanGoForward);
        Assert.Equal(@"C:\b", history.Forward());
    }

    [Fact]
    public void Parent_of_a_folder_is_the_folder_above_it()
    {
        Assert.Equal(@"C:\Users", History.Parent(@"C:\Users\someone"));
        Assert.Equal(@"C:\Users", History.Parent(@"C:\Users\someone\"));
    }

    [Fact]
    public void Parent_of_a_unc_share_root_is_null()
    {
        Assert.Equal(@"\\server\share", History.Parent(@"\\server\share\folder"));
        Assert.Null(History.Parent(@"\\server\share"));
        Assert.Null(History.Parent(@"\\server\share\"));
    }

    [Theory]
    [InlineData("C:")]
    [InlineData(@"docs\notes")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parent_of_a_path_that_is_not_fully_qualified_is_null(string path) =>
        Assert.Null(History.Parent(path));

    [Fact]
    public void Parent_of_a_drive_root_is_null()
    {
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);

        Assert.All(roots, root =>
            Assert.True(History.Parent(root) is null, $"Parent(\"{root}\") returned \"{History.Parent(root)}\""));
    }
}
