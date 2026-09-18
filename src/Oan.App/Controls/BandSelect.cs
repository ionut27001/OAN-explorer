using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Oan.App.Controls;

/// <summary>
/// Drag a rectangle over empty space to select what it touches.
///
/// Hit-testing runs against the panel's realised children, not the item list: with 50,000 rows
/// only a few dozen containers exist, and asking the generator for the rest would both allocate
/// them and defeat the virtualisation. What that costs is that the band can only select what has
/// been realised — which is why it auto-scrolls at the edges, realising more as it goes.
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
    }

    public bool Dragging => _dragging;

    /// <summary>Call from the list's left-button-down when the press was NOT on a row.</summary>
    public void Begin(MouseButtonEventArgs e)
    {
        _scroll = Descendant<ScrollViewer>(_list);
        _layer = AdornerLayer.GetAdornerLayer(_list);
        if (_layer is null) return;

        _origin = e.GetPosition(_list);
        _dragging = true;

        // Ctrl and Shift add to what was already selected; a plain drag replaces it.
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _kept = additive ? _list.SelectedItems.Cast<object>().ToArray() : [];
        if (!additive) _list.SelectedItems.Clear();

        _band = new RubberBand(_list);
        _layer.Add(_band);
        _list.CaptureMouse();
        e.Handled = true;
    }

    public void Move(MouseEventArgs e)
    {
        if (!_dragging || _band is null) return;

        var now = e.GetPosition(_list);
        _band.Update(new Rect(_origin, now));
        Select(new Rect(_origin, now));

        // Past the top or bottom edge the list scrolls, which realises more containers and lets
        // the band keep growing beyond one screenful.
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
        _list.ReleaseMouseCapture();

        if (_band is not null) _layer?.Remove(_band);
        _band = null;
        _kept = [];
    }

    private void Scroll()
    {
        if (_scroll is null || !_dragging) return;

        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + _autoScroll / EdgeMargin * ScrollStep);

        // Containers that scrolled into view have not been through Move yet, so the band is
        // re-applied against the new set rather than waiting for the next mouse movement.
        var cursor = Mouse.GetPosition(_list);
        Select(new Rect(_origin, cursor));
    }

    private void Select(Rect band)
    {
        var host = ItemsHost(_list);
        if (host is null) return;

        var wanted = new HashSet<object>(_kept);

        foreach (var child in host.Children)
        {
            if (child is not ListViewItem { IsVisible: true } item) continue;

            var topLeft = item.TranslatePoint(new Point(), _list);
            if (band.IntersectsWith(new Rect(topLeft, item.RenderSize)) && item.DataContext is { } data)
                wanted.Add(data);
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
