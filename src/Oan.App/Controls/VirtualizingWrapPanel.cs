using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Oan.App.Controls;

/// <summary>
/// A wrapping panel of uniform tiles that only realizes the ones on screen. WPF ships no such
/// panel: WrapPanel does not virtualize and VirtualizingStackPanel does not wrap, so a folder of
/// 50 000 items in icon view would otherwise build 50 000 containers before it drew anything.
/// </summary>
/// <remarks>
/// <para>IScrollInfo is not optional here. A panel that leaves scrolling to the ScrollViewer is
/// measured with infinite height, and then every item is realized no matter what MeasureOverride
/// does. The offsets below are pixels, not items.</para>
/// <para>Containers are released with Remove, which is an error for a recycled container: the
/// host must leave VirtualizationMode at Standard.</para>
/// </remarks>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(92.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private double _offset;
    private int _columns = 1;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = InternalChildren; // Touching the children is what creates the generator.

        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;

        _columns = WrapMetrics.Columns(width, ItemWidth);
        _extent = new Size(_columns * ItemWidth, WrapMetrics.Rows(count, _columns) * ItemHeight);
        _viewport = new Size(width, availableSize.Height);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();

        var (first, last) = WrapMetrics.VisibleRange(count, _columns, _offset, _viewport.Height, ItemHeight);
        Realize(first, last);
        Release(first, last);

        return new Size(width, double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;

            var x = index % _columns * ItemWidth;
            var y = index / _columns * ItemHeight - _offset;
            children[i].Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            // A new listing: the generator has dropped its containers already, and the ones left
            // here would be arranged against indexes that now mean different items.
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = 0;
                break;
        }
    }

    /// <summary>Keyboard navigation onto an item that has no container yet lands here.</summary>
    protected override void BringIndexIntoView(int index)
    {
        ScrollTo(index / Math.Max(1, _columns) * ItemHeight);
        UpdateLayout();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = ChildContaining(visual);
        if (child is null) return rectangle;

        var index = ItemContainerGenerator.IndexFromGeneratorPosition(
            new GeneratorPosition(InternalChildren.IndexOf(child), 0));

        if (index >= 0) ScrollTo(index / Math.Max(1, _columns) * ItemHeight);
        return rectangle;
    }

    public void LineUp() => SetVerticalOffset(_offset - ItemHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset + ItemHeight / 3);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - ItemHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset + ItemHeight);

    // The tiles are laid out to the viewport width, so there is never anything to the side.
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset) < 0.5) return;

        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <summary>Scrolls the least that brings the row starting at <paramref name="top"/> fully in.</summary>
    private void ScrollTo(double top)
    {
        if (top < _offset) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    private void Realize(int first, int last)
    {
        if (last < first) return;

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child) break;

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
    }

    private void Release(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private UIElement? ChildContaining(Visual visual)
    {
        for (DependencyObject? node = visual; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && InternalChildren.Contains(element)) return element;

        return null;
    }
}
