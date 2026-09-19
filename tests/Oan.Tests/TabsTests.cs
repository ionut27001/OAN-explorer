using Oan.App.ViewModels;
using Xunit;

namespace Oan.Tests;

[Collection(DiskBound.Name)]
public class TabsTests
{
    private static TabsViewModel With(params string[] paths)
    {
        var tabs = new TabsViewModel();
        tabs.Items[0].Path = paths[0];
        foreach (var path in paths.Skip(1)) tabs.Open(path);
        tabs.Active = tabs.Items[0];
        return tabs;
    }

    [Fact]
    public void A_window_always_has_one_tab_and_it_cannot_be_closed()
    {
        var tabs = new TabsViewModel();

        Assert.Single(tabs.Items);
        Assert.False(tabs.CanClose);

        tabs.Close(tabs.Active);

        // A window with no tab has nothing to show, so the last one stays whatever is asked.
        Assert.Single(tabs.Items);
    }

    [Fact]
    public void A_new_tab_opens_beside_the_active_one_not_at_the_end()
    {
        var tabs = With(@"C:\a", @"C:\b", @"C:\c");
        tabs.Active = tabs.Items[0];

        var opened = tabs.Open(@"C:\new");

        Assert.Equal(1, tabs.Items.IndexOf(opened));
        Assert.Same(opened, tabs.Active);
    }

    [Fact]
    public void Each_tab_keeps_its_own_history()
    {
        var tabs = With(@"C:\a");
        tabs.Active.History.Visit(@"C:\a");
        tabs.Active.History.Visit(@"C:\a\deep");

        var second = tabs.Open(@"C:\b");
        second.History.Visit(@"C:\b");

        // Back in the new tab must not walk into where the first tab has been.
        Assert.False(second.History.CanGoBack);
        Assert.True(tabs.Items[0].History.CanGoBack);
    }

    [Fact]
    public void Closing_the_active_tab_hands_you_the_one_on_its_right()
    {
        var tabs = With(@"C:\a", @"C:\b", @"C:\c");
        tabs.Active = tabs.Items[1];

        tabs.Close(tabs.Items[1]);

        Assert.Equal(@"C:\c", tabs.Active.Path);
    }

    [Fact]
    public void Closing_the_last_tab_hands_you_the_one_on_its_left()
    {
        var tabs = With(@"C:\a", @"C:\b");
        tabs.Active = tabs.Items[1];

        tabs.Close(tabs.Items[1]);

        Assert.Equal(@"C:\a", tabs.Active.Path);
    }

    [Fact]
    public void Ctrl_Tab_wraps_round_the_end()
    {
        var tabs = With(@"C:\a", @"C:\b");
        tabs.Active = tabs.Items[1];

        tabs.NextTabCommand.Execute(null);

        Assert.Same(tabs.Items[0], tabs.Active);
    }

    [Fact]
    public void Only_the_active_tab_says_it_is_active()
    {
        var tabs = With(@"C:\a", @"C:\b", @"C:\c");
        tabs.Active = tabs.Items[2];

        Assert.Equal([false, false, true], tabs.Items.Select(t => t.IsActive));
    }

    [Fact]
    public void Switching_asks_the_window_to_load_that_tab_s_folder()
    {
        var tabs = With(@"C:\a", @"C:\b");
        string? asked = null;
        tabs.Switched += path => asked = path;

        tabs.Active = tabs.Items[1];

        Assert.Equal(@"C:\b", asked);
    }

    [Fact]
    public void Dragging_a_tab_moves_it_and_a_target_past_the_end_clamps()
    {
        var tabs = With(@"C:\a", @"C:\b", @"C:\c");

        tabs.Move(tabs.Items[0], 2);
        Assert.Equal([@"C:\b", @"C:\c", @"C:\a"], tabs.Items.Select(t => t.Path));

        tabs.Move(tabs.Items[0], 99);
        Assert.Equal(@"C:\b", tabs.Items[2].Path);
    }

    [Fact]
    public void A_drive_root_keeps_its_own_name_rather_than_showing_a_blank_tab()
    {
        var tabs = With(@"C:\");

        // Path.GetFileName("C:\") is empty, and a tab labelled with nothing is unfindable.
        Assert.Equal(@"C:\", tabs.Items[0].Title);
    }

    [Fact]
    public void A_folder_tab_is_named_after_the_folder()
    {
        var tabs = With(@"C:\Users\oance\Downloads");

        Assert.Equal("Downloads", tabs.Items[0].Title);
    }

    /// <summary>
    /// Explorer puts the folder's own icon on each tab; ours drew one hardcoded glyph on all of
    /// them. Two assertions, and the second is the one that matters: a tab's path moves, unlike a
    /// sidebar place's, so an icon fetched once and latched would be wrong from the first
    /// navigation onward.
    /// </summary>
    [Fact]
    public void A_tab_carries_the_icon_of_the_folder_it_is_showing()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Assert.True(Directory.Exists(windows) && Directory.Exists(pictures), "no folders to read");

        var tab = new TabViewModel { Path = pictures };
        var first = Settle(tab);
        Assert.NotNull(first);

        tab.Path = windows;
        var second = Settle(tab, first);

        Assert.NotNull(second);
        Assert.NotEqual(Bytes(first!), Bytes(second!));
    }

    private static System.Windows.Media.ImageSource? Settle(
        TabViewModel tab, System.Windows.Media.ImageSource? was = null)
    {
        for (var i = 0; i < 200 && (tab.Icon is null || ReferenceEquals(tab.Icon, was)); i++)
            Thread.Sleep(20);

        return tab.Icon;
    }

    private static byte[] Bytes(System.Windows.Media.ImageSource source)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
            (System.Windows.Media.Imaging.BitmapSource)source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
