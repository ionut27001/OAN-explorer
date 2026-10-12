using Oan.App.Platform;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>
/// The column headers' menu, as the View submenu of the shell's menu for empty space: only the details view has
/// headers, and without this the columns could not be chosen from any other.
/// </summary>
public sealed class ViewMenu(AppearanceViewModel look)
{
    public const int FirstId = 20;
    private const int FirstColumnId = FirstId + 10;

    private static readonly (int View, string Label)[] Views =
    [
        (Settings.Views_Details, "Details"), (Settings.Views_List, "List"), (32, "Small icons"),
        (48, "Medium icons"), (96, "Large icons"), (256, "Extra large icons"),
    ];

    private static readonly (SortKey Key, string Label)[] Columns =
    [
        (SortKey.Name, "Name"), (SortKey.Size, "Size"), (SortKey.Kind, "Type"),
        (SortKey.Modified, "Date modified"), (SortKey.Created, "Date created"), (SortKey.Attributes, "Attributes"),
    ];

    public OwnMenuItem Item() => new(0, "View", Items:
    [
        .. Views.Select((view, i) => new OwnMenuItem(FirstId + i, view.Label, Checked: look.View == view.View)),
        OwnMenuItem.Separator,
        .. Columns.Select((column, i) => new OwnMenuItem(FirstColumnId + i, column.Label,
            Checked: look.Columns[column.Key.ToString()], Enabled: column.Key != SortKey.Name)),
    ]);

    public void Run(int id)
    {
        if (id < FirstColumnId)
        {
            look.View = Views[id - FirstId].View;
            return;
        }

        var key = Columns[id - FirstColumnId].Key.ToString();
        look.Columns[key] = !look.Columns[key];
    }
}
