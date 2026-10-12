using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// Finding what a mouse event landed on: the source is whatever leaf was under the pointer. Walks both trees,
/// because templated content hangs off the visual one and a control's own content off the logical one.
/// </summary>
internal static class Ancestor
{
    /// <summary>The row a click landed on, or null for the header, the scroll bar or empty space.</summary>
    public static EntryViewModel? RowOf(object? source) => Of<ListViewItem>(source)?.Content as EntryViewModel;

    /// <summary>The details view's column header row, the stretch right of the last column included.</summary>
    public static bool OnHeaders(object? source) => Of<GridViewHeaderRowPresenter>(source) is not null;

    public static T? Of<T>(object? source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
        {
            // The logical parent whenever there is no visual one: a TextBlock given to a control as its content
            // is a Visual with no visual parent until that control is templated.
            var visual = node is Visual ? VisualTreeHelper.GetParent(node) : null;
            node = visual ?? LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
