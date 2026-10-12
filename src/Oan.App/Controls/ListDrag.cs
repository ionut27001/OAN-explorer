using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// Files leaving the list and files arriving in it. A drop goes into the folder row under the pointer, and
/// anywhere else, including right of the columns, into the folder on screen.
/// </summary>
internal sealed class ListDrag(ListView list, MainViewModel model)
{
    private ListViewItem? _aimedAt;

    public void Out()
    {
        // Rows inside an archive have a path that names nothing, so there is nothing to hand over.
        if (!model.CanCopyOut) return;

        var paths = model.SelectedPaths();
        if (paths.Count == 0) return;

        var files = new StringCollection();
        foreach (var path in paths) files.Add(path);

        var data = new DataObject();
        data.SetFileDropList(files);

        if (DragDrop.DoDragDrop(list, data, DragDropEffects.Copy | DragDropEffects.Move) == DragDropEffects.Move)
            model.RefreshCommand.Execute(null);
    }

    public void Over(DragEventArgs e)
    {
        e.Effects = EffectFor(e);
        Aim(e.OriginalSource);
        e.Handled = true;
    }

    public void Leave() => Aim(null);

    public void Drop(DragEventArgs e)
    {
        var effect = EffectFor(e);
        var into = FolderAt(e.OriginalSource);
        Aim(null);

        if (effect != DragDropEffects.None && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            model.Drop(paths, move: effect == DragDropEffects.Move, into);

        e.Effects = effect;
        e.Handled = true;
    }

    /// <summary>The folder a drop lands in: the row under the pointer, or the folder on screen.</summary>
    private string? FolderAt(object? source) =>
        model.CanWriteHere && Ancestor.RowOf(source) is { Entry.IsDirectory: true } row
            ? row.Entry.FullPath
            : null;

    /// <summary>Marks the row a drop would land in.</summary>
    private void Aim(object? source)
    {
        var item = FolderAt(source) is null ? null : Ancestor.Of<ListViewItem>(source);
        if (ReferenceEquals(item, _aimedAt)) return;

        if (_aimedAt is not null) DropTarget.SetIsOn(_aimedAt, false);
        _aimedAt = item;
        if (_aimedAt is not null) DropTarget.SetIsOn(_aimedAt, true);
    }

    /// <summary>Shift asks for a move, but a source that only offers a copy (a zip folder) must never be told we moved.</summary>
    private static DragDropEffects EffectFor(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;

        var wanted = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & wanted) != 0) return wanted;

        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
