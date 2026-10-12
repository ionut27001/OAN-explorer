using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>
/// What OAN puts at the top of the system menu. Mount and the archive entries are here because the
/// shell's own belong to whatever handler got there first, and that one may hang. View is here because
/// Explorer adds its own from the folder window, which OAN does not have.
/// </summary>
public sealed class OwnMenu(MountsViewModel mounts, ArchiveOps archives, ViewMenu view,
    Func<IReadOnlyList<EntryViewModel>> selection, Action<Func<Task>> run)
{
    // Ours, kept below the shell's own numbering so a pick says whose it is.
    private const int MountId = 1, EjectId = 2;

    public IReadOnlyList<OwnMenuItem> Items()
    {
        // Nothing selected: the menu for empty space in the folder.
        if (selection().Count == 0) return [view.Item()];

        var images = Images();
        if (images.Length == 0) return archives.MenuItems();

        var mounted = images.All(mounts.IsMounted);
        return [new OwnMenuItem(mounted ? EjectId : MountId, mounted ? "Eject" : "Mount"), .. archives.MenuItems()];
    }

    public void Run(int id)
    {
        if (id >= ViewMenu.FirstId)
        {
            view.Run(id);
            return;
        }

        if (id >= ArchiveOps.ExtractToFolderId)
        {
            run(() => archives.RunAsync(id));
            return;
        }

        foreach (var image in Images())
        {
            if (id == MountId) run(() => mounts.OpenAsync(image));
            else if (id == EjectId) mounts.UnmountCommand.Execute(image);
        }
    }

    private string[] Images() =>
        [.. selection().Select(r => r.Entry.FullPath).Where(MountsViewModel.IsImage)];
}
