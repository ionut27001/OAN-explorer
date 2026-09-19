using System.IO;
using Oan.Core.Sizes;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The whole tree under a folder, which is what the map needs to draw what is inside folders
/// rather than only the folders themselves. Its totals have to agree with the ones the Size
/// column produces, or the two views of the same folder disagree in front of the user.
/// </summary>
[Collection(DiskBound.Name)]
public class SizeTreeTests
{
    [Fact]
    public void A_folder_carries_everything_beneath_it()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.AddFolder("a"), "one.bin"), new string('x', 100));
        var deep = Path.Combine(temp.Root, "a", "b");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "two.bin"), new string('x', 400));
        File.WriteAllText(Path.Combine(temp.Root, "loose.bin"), new string('x', 7));

        var tree = SizeTree.Read(temp.Root)!;

        Assert.Equal(500, Find(tree, "a"));
        Assert.Equal(400, Find(tree, "b"));
        Assert.Equal(7, Find(tree, "loose.bin"));
    }

    [Fact]
    public void Children_are_the_ones_directly_inside_and_nothing_deeper()
    {
        using var temp = new TempFolder();
        var a = temp.AddFolder("a");
        Directory.CreateDirectory(Path.Combine(a, "b"));
        temp.AddFile("loose.txt", "x");

        var tree = SizeTree.Read(temp.Root)!;

        var top = tree.ChildrenOf(-1).ToArray().Select(tree.Name).OrderBy(n => n);
        Assert.Equal(["a", "loose.txt"], top);

        var inA = Enumerable.Range(0, tree.Count).First(i => tree.Name(i) == "a");
        Assert.Equal(["b"], tree.ChildrenOf(inA).ToArray().Select(tree.Name));
    }

    [Fact]
    public void Its_total_agrees_with_what_the_size_column_measures()
    {
        using var temp = new TempFolder();
        var inner = temp.AddFolder("inner");
        File.WriteAllText(Path.Combine(inner, "a.bin"), new string('x', 1234));
        File.WriteAllText(Path.Combine(inner, "b.bin"), new string('x', 99));

        var tree = SizeTree.Read(temp.Root)!;
        var column = new Oan.Core.FileSystem.FolderSize().Measure(inner);

        Assert.Equal(column.Bytes, Find(tree, "inner"));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_null_rather_than_empty()
    {
        Assert.Null(SizeTree.Read(@"C:\no-such-folder-9f3c"));
        Assert.Null(SizeTree.Read(""));
    }

    [Fact]
    public void An_empty_folder_reads_as_a_tree_with_nothing_in_it()
    {
        using var temp = new TempFolder();

        var tree = SizeTree.Read(temp.Root)!;

        Assert.Equal(0, tree.Count);
        Assert.Empty(tree.ChildrenOf(-1).ToArray());
    }

    private static long Find(SizeTree tree, string name)
    {
        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.Name(i) == name) return tree.Size[i];
        }
        throw new InvalidOperationException($"no {name} in the tree");
    }
}
