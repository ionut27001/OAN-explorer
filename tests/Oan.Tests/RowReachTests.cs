using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Oan.App.Controls;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// Where a row ends, asked of WPF itself: a real ListView with the app's own styles, hit-tested at real points.
/// Adding up column widths is not enough; what matters is what the press lands on. Past the end is empty space,
/// and a press there has to reach the rubber band.
/// </summary>
[Collection(DiskBound.Name)]
public class RowReachTests
{
    [Fact]
    public void A_click_right_of_the_last_column_lands_on_empty_space()
    {
        var rows = Rows();
        var (reach, stripAt, strip, below, blank, name) = Sta(() => InDetails(rows, list =>
        {
            var first = Container(list, rows[0]);
            var last = Container(list, rows[^1]);

            var middle = first.TranslatePoint(new Point(0, first.ActualHeight / 2), list).Y;
            var under = last.TranslatePoint(new Point(0, last.ActualHeight + 30), list).Y;
            var columns = ((GridView)list.View).Columns;
            var nameEnds = first.TranslatePoint(new Point(columns[0].ActualWidth, 0), list).X;
            var nameText = Descendants<TextBlock>(first).First(t => t.Text == rows[0].Name);

            return (first.TranslatePoint(new Point(columns.Sum(c => c.ActualWidth), 0), list).X,
                    list.ActualWidth - 40,
                    Land(list, new Point(list.ActualWidth - 40, middle)),
                    Land(list, new Point(100, under)),
                    Land(list, new Point(nameEnds - 20, middle)),
                    Land(list, nameText.TranslatePoint(new Point(nameText.ActualWidth / 2, nameText.ActualHeight / 2), list)));
        }));

        Assert.True(reach + 40 < stripAt, $"the columns reach {reach} of a list {stripAt + 40} wide: there is no strip to test");
        Empty(strip, "right of the last column");
        Empty(below, "below the last row");

        Assert.False(blank.OnText, "the point meant to be blank is on the name, so this proves nothing");
        Assert.True(ReferenceEquals(rows[0], blank.Row), "a click in the blank part of a cell has to land on its row");

        Assert.True(name.OnText, "the point meant to be on the name missed it, so this proves nothing");
        Assert.True(ReferenceEquals(rows[0], name.Row), "a click on the name has to land on its row");
    }

    [Fact]
    public void The_header_row_reaches_right_of_the_last_column_and_no_row_is_part_of_it()
    {
        var rows = Rows();
        var (beside, onName, onRow) = Sta(() => InDetails(rows, list =>
        {
            var headers = Descendants<GridViewHeaderRowPresenter>(list).First();
            var y = headers.TranslatePoint(new Point(0, headers.ActualHeight / 2), list).Y;
            var row = Container(list, rows[0]).TranslatePoint(new Point(60, 10), list);

            bool OnHeaders(Point at) => Ancestor.OnHeaders(list.InputHitTest(at));
            return (OnHeaders(new Point(list.ActualWidth - 40, y)), OnHeaders(new Point(60, y)), OnHeaders(row));
        }));

        Assert.True(beside, "a right-click on the header row right of the last column missed the headers");
        Assert.True(onName, "a right-click on the Name header missed the headers");
        Assert.False(onRow, "a right-click on a row counted as the headers");
    }

    [Fact]
    public void A_press_between_beside_or_below_the_tiles_lands_on_empty_space()
    {
        var rows = Rows();
        var (between, beside, below, tile) = Sta(() => InTiles(rows, list =>
        {
            var first = Container(list, rows[0]);
            var last = Container(list, rows[^1]);
            var middle = first.ActualHeight / 2;

            return (Land(list, first.TranslatePoint(new Point(first.ActualWidth - 1, middle), list)),
                    Land(list, last.TranslatePoint(new Point(last.ActualWidth + 40, middle), list)),
                    Land(list, first.TranslatePoint(new Point(first.ActualWidth / 2, first.ActualHeight + 30), list)),
                    Land(list, first.TranslatePoint(new Point(first.ActualWidth / 2, middle), list)));
        }));

        Assert.True(ReferenceEquals(rows[0], tile.Row), "the middle of a tile missed it, so this proves nothing");
        Empty(between, "in the gap between two tiles");
        Empty(beside, "right of the last tile");
        Empty(below, "below the tiles");
    }

    [Fact]
    public void A_band_right_of_the_columns_selects_nothing()
    {
        var rows = Rows();
        var (beside, across) = Sta(() => InDetails(rows, list =>
        {
            list.SelectionMode = SelectionMode.Extended;
            var band = new BandSelect(list);

            var top = Container(list, rows[0]).TranslatePoint(new Point(), list).Y;
            var second = Container(list, rows[1]);
            var secondEnds = second.TranslatePoint(new Point(0, second.ActualHeight - 2), list).Y;
            var bottom = list.ActualHeight - 20;

            band.Select(new Rect(new Point(list.ActualWidth - 60, top), new Point(list.ActualWidth - 20, bottom)));
            var beside = list.SelectedItems.Count;

            band.Select(new Rect(new Point(20, top + 2), new Point(60, secondEnds)));
            return (beside, list.SelectedItems.Cast<EntryViewModel>().OrderBy(r => r.Name).ToArray());
        }));

        Assert.True(beside == 0, $"a band right of the columns selected {beside} rows it never touched");
        Assert.Equal(rows[..2], across);
    }

    private readonly record struct Landing(bool Hit, EntryViewModel? Row, bool OnText, bool Band);

    private static Landing Land(ListView list, Point at)
    {
        var hit = list.InputHitTest(at) as DependencyObject;
        return new(hit is not null, Ancestor.RowOf(hit), Ancestor.Of<TextBlock>(hit) is not null, BandArea.Starts(hit));
    }

    private static void Empty(Landing landed, string where)
    {
        Assert.True(landed.Hit, $"nothing was hit {where}, so this proves nothing");
        Assert.True(landed.Row is null, $"a press {where} landed on an item");
        Assert.True(landed.Band, $"a press {where} cannot start a rubber band");
    }

    /// <summary>The details list with what the window's FileList style gives it, wider than its columns reach.</summary>
    private static T InDetails<T>(EntryViewModel[] rows, Func<ListView, T> look)
    {
        var styles = Styles();
        var list = new ListView
        {
            View = (ViewBase)styles["DetailsView"],
            ItemContainerStyle = (Style)styles["Row"],
            ItemsPanel = (ItemsPanelTemplate)styles["RowPanel"],
            ItemsSource = rows,
        };
        return Shown(list, styles, look);
    }

    /// <summary>The icon grid as FileList switches to it, with the panel's default tile size.</summary>
    private static T InTiles<T>(EntryViewModel[] rows, Func<ListView, T> look)
    {
        var styles = Styles();
        var list = new ListView
        {
            ItemContainerStyle = (Style)styles["Tile"],
            ItemTemplate = (DataTemplate)styles["TileTemplate"],
            ItemsPanel = (ItemsPanelTemplate)styles["TilePanel"],
            ItemsSource = rows,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return Shown(list, styles, look);
    }

    private static T Shown<T>(ListView list, ResourceDictionary styles, Func<ListView, T> look)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        ScrollViewer.SetCanContentScroll(list, true);

        // In a window of its own, hidden: outside one no element counts as visible, and every hit test comes back empty.
        using var window = new HwndSource(new HwndSourceParameters("RowReachTests") { WindowStyle = 0 })
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            RootVisual = new Border { Resources = styles, Child = list, Width = 1500, Height = 300 },
        };
        list.UpdateLayout();
        return look(list);
    }

    private static ResourceDictionary Styles()
    {
        // Registers the pack: scheme, which a process with no Application has not done yet.
        _ = PackUriHelper.UriSchemePack;

        // What UserThemes adds at startup; the Row style asks for it by name.
        var styles = new ResourceDictionary { ["RowHeight"] = 28.0 };
        foreach (var name in new[] { "Controls", "FileColumns", "FileTiles" })
            styles.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/Oan;component/Themes/{name}.xaml", UriKind.Relative)));
        return styles;
    }

    private static EntryViewModel[] Rows() =>
    [
        .. new[] { "notes.txt", "photo.jpg", "report.pdf" }.Select(name => new EntryViewModel(new FileEntry
        {
            Name = name,
            FullPath = $@"C:\OanRowReach\{name}",
            Attributes = FileAttributes.Normal,
            Size = 1200,
            Modified = new DateTime(2026, 9, 1),
        }, 16)),
    ];

    private static ListViewItem Container(ListView list, EntryViewModel row) =>
        (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(row);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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
