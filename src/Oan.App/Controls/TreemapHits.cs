using System.Windows;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Oan.Core.Sizes;

namespace Oan.App.Controls;

/// <summary>What is under the pointer on the size map, answered against whichever of the two maps was drawn.</summary>
internal sealed class TreemapHits
{
    private readonly List<(EntryViewModel Row, Box Box)> _shallow = [];
    private readonly List<(SizeTree Tree, int Index, Box Box)> _deep = [];

    public void Clear()
    {
        _shallow.Clear();
        _deep.Clear();
    }

    public void Add(EntryViewModel row, Box box) => _shallow.Add((row, box));

    public void Add(SizeTree tree, int index, Box box) => _deep.Add((tree, index, box));

    /// <summary>The row whose tile holds this point, or null. Null for anything deeper.</summary>
    public EntryViewModel? RowAt(Point at)
    {
        // Backwards: the layout places the largest first, and the pointer means the smallest.
        for (var i = _shallow.Count - 1; i >= 0; i--)
        {
            if (_shallow[i].Box.Holds(at.X, at.Y)) return _shallow[i].Row;
        }
        return null;
    }

    /// <summary>The last tile drawn that holds this point: the deeper a tile is, the later it was drawn.</summary>
    public int Deepest(Point at)
    {
        for (var i = _deep.Count - 1; i >= 0; i--)
        {
            if (_deep[i].Box.Holds(at.X, at.Y)) return i;
        }
        return -1;
    }

    public string PathOf(int tile) => _deep[tile].Tree.PathOf(_deep[tile].Index);

    public bool IsFolder(int tile) => _deep[tile].Tree.IsFolder(_deep[tile].Index);

    public Box? BoxOf(int tile) => tile >= 0 && tile < _deep.Count ? _deep[tile].Box : null;

    public Box BoxOf(EntryViewModel row) =>
        _shallow.FirstOrDefault(p => ReferenceEquals(p.Row, row)).Box;

    public string Told(int tile)
    {
        var (tree, index, _) = _deep[tile];
        return $"{tree.Name(index)}\n{Format.Size(tree.Size[index])}\n{tree.PathOf(index)}";
    }
}
