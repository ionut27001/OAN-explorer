using System.Windows;
using System.Windows.Media;

namespace Oan.App.Controls;

/// <summary>
/// Finding what a mouse event actually landed on. The source of a click is whatever leaf was
/// under the pointer — a TextBlock, a Border — so every handler has to walk up to the thing it
/// cares about. Both trees, because templated content hangs off the visual one and a control's
/// own content off the logical one.
/// </summary>
internal static class Ancestor
{
    public static T? Of<T>(object? source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
        {
            // The visual parent first, and the logical one when there is no visual parent rather
            // than only when the node is not a Visual: a TextBlock handed to a control as its
            // content is a Visual with no visual parent until that control is templated, and
            // stopping there loses the very thing being looked for.
            var visual = node is Visual ? VisualTreeHelper.GetParent(node) : null;
            node = visual ?? LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
