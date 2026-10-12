using System.Windows.Controls;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The details view shows the columns switched on, in the order the XAML gives them. A GridViewColumn has no
/// visibility, so a hidden one is taken out of the view and kept here, width and all, to go back in.
/// </summary>
internal sealed class DetailsColumns
{
    private readonly GridView _view;
    private readonly ColumnSet _shown;
    private readonly GridViewColumn[] _all;

    public DetailsColumns(GridView view, ColumnSet shown)
    {
        (_view, _shown, _all) = (view, shown, [.. view.Columns]);
        shown.Changed += Apply;
        Apply();
    }

    private void Apply()
    {
        _view.Columns.Clear();
        foreach (var column in _all)
            if (column.Header is string key && _shown[key]) _view.Columns.Add(column);
    }
}
