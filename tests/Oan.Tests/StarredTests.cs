using Oan.Core.FileSystem;
using Xunit;

namespace Oan.Tests;

public class StarredTests
{
    [Fact]
    public void A_starred_folder_resolves_as_an_available_folder()
    {
        using var temp = new TempFolder();

        var place = Assert.Single(Places.Starred([temp.Root]));

        Assert.True(place.Available);
        Assert.Equal(PlaceKind.Folder, place.Kind);
        Assert.Equal(Path.GetFileName(temp.Root), place.Name);
    }

    [Fact]
    public void A_starred_file_resolves_as_a_file_not_a_folder()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("notite.txt");

        var place = Assert.Single(Places.Starred([file]));

        Assert.True(place.Available);
        Assert.Equal(PlaceKind.File, place.Kind);
        Assert.Equal("notite.txt", place.Name);
    }

    [Fact]
    public void A_starred_path_that_is_gone_is_kept_and_marked_unavailable()
    {
        var gone = Path.Combine(Path.GetTempPath(), "oan-nu-exista-" + Guid.NewGuid().ToString("N"));

        var place = Assert.Single(Places.Starred([gone]));

        // Not dropped: the user put it there, and removing it silently is the same outcome as
        // never having starred it.
        Assert.False(place.Available);
    }

    [Fact]
    public void The_same_path_twice_is_one_entry()
    {
        using var temp = new TempFolder();

        Assert.Single(Places.Starred([temp.Root, temp.Root]));
    }

    [Fact]
    public void Blank_entries_are_ignored_rather_than_shown_as_a_nameless_row()
    {
        Assert.Empty(Places.Starred(["", "   "]));
    }

    [Fact]
    public void Order_is_the_order_the_user_starred_them()
    {
        using var temp = new TempFolder();
        var a = temp.AddFile("a.txt");
        var b = temp.AddFile("b.txt");

        var places = Places.Starred([b, a]);

        Assert.Equal(["b.txt", "a.txt"], places.Select(p => p.Name));
    }
}
