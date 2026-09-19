using System.IO;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Sorting by the Type column. Folders do not move under it, and that is the data rather than the
/// sort: every folder is the same type, so there is nothing to order them by and the tie-breaker
/// puts them back in name order. Files, which do differ, have to move.
/// </summary>
public class SortByTypeTests
{
    [Fact]
    public void Every_folder_is_the_same_type_so_nothing_can_order_them_by_it()
    {
        var kinds = new[] { "alpha", "zulu", "middle" }
            .Select(n => Format.Kind(new FileEntry
            {
                Name = n,
                FullPath = $@"C:\x\{n}",
                Attributes = FileAttributes.Directory,
            }))
            .Distinct()
            .ToArray();

        Assert.Single(kinds);
    }

    [Fact]
    public void Folders_keep_their_name_order_when_sorted_by_type()
    {
        var rows = new[] { Folder("zulu"), Folder("alpha"), Folder("middle") };

        var sort = new SortViewModel();
        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);

        Assert.Equal(["alpha", "middle", "zulu"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Files_do_move_when_sorted_by_type()
    {
        var rows = new[] { File("b.txt"), File("a.zip"), File("c.png") };

        var sort = new SortViewModel();
        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);

        Assert.Equal(["c.png", "b.txt", "a.zip"], rows.Select(r => r.Name));
    }

    /// <summary>
    /// The reported bug. Every folder is the same type, so the first click cannot move them and
    /// should not — but the second click pointed the column the other way and produced the very
    /// same list, under a header drawing a downward arrow. Whatever the column is ordered by,
    /// pointing it the other way has to turn the rows around.
    /// </summary>
    [Fact]
    public void Pointing_the_type_column_the_other_way_turns_the_folders_around()
    {
        var rows = new[] { Folder("zulu"), Folder("alpha"), Folder("middle") };

        var sort = new SortViewModel();
        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);
        Assert.Equal(["alpha", "middle", "zulu"], rows.Select(r => r.Name));

        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);

        Assert.Equal(["zulu", "middle", "alpha"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Folders_stay_above_files_whichever_way_the_type_column_is_pointed()
    {
        var rows = new[] { File("a.txt"), Folder("zulu") };

        var sort = new SortViewModel();
        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);
        Assert.Equal(["zulu", "a.txt"], rows.Select(r => r.Name));

        sort.By(SortKey.Kind);
        Array.Sort(rows, sort.Compare);
        Assert.Equal(["zulu", "a.txt"], rows.Select(r => r.Name));
    }

    private static EntryViewModel Folder(string name) => Row(name, FileAttributes.Directory);
    private static EntryViewModel File(string name) => Row(name, FileAttributes.Normal);

    private static EntryViewModel Row(string name, FileAttributes attributes) =>
        new(new FileEntry { Name = name, FullPath = $@"C:\x\{name}", Attributes = attributes }, 16);
}
