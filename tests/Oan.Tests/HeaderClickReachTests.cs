using System.Windows;
using System.Windows.Controls;
using Oan.App.Controls;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Built against a real ListView, because the rule that decides whether a press starts a rubber
/// band is written in terms of WPF's own template — and that template is not what it looks like:
/// the column header row lives INSIDE the scrolled content, so "only where the rows are drawn"
/// covers the headers too. Guessing at it cost the header its click twice.
/// </summary>
public class HeaderClickReachTests
{
    [Fact]
    public void A_press_on_a_column_header_does_not_start_a_rubber_band()
    {
        var (foundHeader, headerInRows, itemInRows) = Sta(() =>
        {
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Size", Width = 100 });

            var list = new ListView { View = view, ItemsSource = new[] { "a", "b" }, Width = 300, Height = 200 };

            var host = new Border { Child = list };
            host.Measure(new Size(300, 200));
            host.Arrange(new Rect(0, 0, 300, 200));
            list.UpdateLayout();

            var header = Descendant<GridViewColumnHeader>(list);
            var item = Descendant<ListViewItem>(list);

            return (header is not null,
                    header is not null && BandArea.Starts(header),
                    item is not null && BandArea.Starts(item));
        });

        Assert.True(foundHeader, "the template never built a column header, so this proves nothing");
        Assert.False(headerInRows, "a press on the header must not start a rubber band");
        Assert.True(itemInRows, "a press on a row has to still start one");
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static T Sta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException("failed on the STA thread", failure);
        return result;
    }
}
