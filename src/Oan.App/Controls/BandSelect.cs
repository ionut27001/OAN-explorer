using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Oan.App.Controls;

/// <summary>
/// Drag a rectangle over empty space to select what it touches. Hit-tests the panel's realised children only,
/// to keep virtualisation intact, which is why it auto-scrolls at the edges, realising more as it goes.
/// </summary>
public sealed class BandSelect
{
    private const double EdgeMargin = 24;
    private const double ScrollStep = 1.6;

    private readonly ListView _list;
    private readonly DispatcherTimer _scroller;

    private AdornerLayer? _layer;
    private RubberBand? _band;
    private ScrollViewer? _scroll;
    private Point _origin;
    private object[] _kept = [];
    private double _autoScroll;
    private bool _dragging;

    public BandSelect(ListView list)
    {
        _list = list;
        _scroller = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _scroller.Tick += (_, _) => Scroll();

        // Alt+Tab or a dialog takes the capture without a button-up ever arriving.
        _list.LostMouseCapture += (_, _) => End();
    }

    public bool Dragging => _dragging;

    /// <summary>Call from the list's left-button-down when the press was NOT on a row.</summary>
    public void Begin(MouseButtonEventArgs e)
    {
        _scroll = Descendant<ScrollViewer>(_list);
        _layer = AdornerLayer.GetAdornerLayer(_list);
        if (_scroll is null || _layer is null) return;

        _origin = e.GetPosition(_list);
        _dragging = true;

        // Ctrl and Shift add to what was already selected; a plain drag replaces it.
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _kept = additive ? _list.SelectedItems.Cast<object>().ToArray() : [];
        if (!additive) _list.SelectedItems.Clear();

        _band = new RubberBand(_list);
        _layer.Add(_band);

        // Not the list: a ListBox holding the capture runs its own auto-scroll past the edges, selecting one row.
        _scroll.CaptureMouse();
        e.Handled = true;
    }

    public void Move(MouseEventArgs e)
    {
        if (!_dragging || _band is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            End();
            return;
        }

        var now = e.GetPosition(_list);
        _band.Update(new Rect(_origin, now));
        Select(new Rect(_origin, now));

        _autoScroll = now.Y < EdgeMargin ? now.Y - EdgeMargin
            : now.Y > _list.ActualHeight - EdgeMargin ? now.Y - (_list.ActualHeight - EdgeMargin)
            : 0;

        if (_autoScroll != 0 && _scroll is not null) _scroller.Start();
        else _scroller.Stop();
    }

    public void End()
    {
        if (!_dragging) return;

        _dragging = false;
        _scroller.Stop();
        _scroll?.ReleaseMouseCapture();

        if (_band is not null) _layer?.Remove(_band);
        _band = null;
        _kept = [];
    }

    private void Scroll()
    {
        if (_scroll is null || !_dragging) return;

        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + _autoScroll / EdgeMargin * ScrollStep);

        // Containers that scrolled into view have not been through Move yet, so the band is re-applied now.
        var cursor = Mouse.GetPosition(_list);
        Select(new Rect(_origin, cursor));
    }

    internal void Select(Rect band)
    {
        var host = ItemsHost(_list);
        if (host is null) return;

        var wanted = new HashSet<object>(_kept);

        foreach (var child in host.Children)
        {
            if (child is ListViewItem { IsVisible: true, DataContext: { } data } item &&
                band.IntersectsWith(Drawn(item))) wanted.Add(data);
        }

        var selected = _list.SelectedItems;
        for (var i = selected.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(selected[i]!)) selected.RemoveAt(i);
        }

        foreach (var data in wanted)
        {
            if (!selected.Contains(data)) selected.Add(data);
        }
    }

    /// <summary>The item as drawn: a details row's container spans the list, its template only the columns.</summary>
    private Rect Drawn(ListViewItem item)
    {
        var drawn = VisualTreeHelper.GetChildrenCount(item) == 1 &&
                    VisualTreeHelper.GetChild(item, 0) is UIElement root ? root : item;
        return new Rect(drawn.TranslatePoint(new Point(), _list), drawn.RenderSize);
    }

    private static Panel? ItemsHost(DependencyObject root)
    {
        if (root is Panel { IsItemsHost: true } panel) return panel;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (ItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
