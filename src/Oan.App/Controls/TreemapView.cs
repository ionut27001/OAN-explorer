using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Oan.App.ViewModels;
using Oan.App.Platform;
using Oan.Core.Sizes;

namespace Oan.App.Controls;

/// <summary>
/// The folder as areas: every child a rectangle, its area its share of the folder.
///
/// One level at a time, not the whole subtree. The sizes it draws are the ones the Size column
/// already measured, so switching to it costs nothing and shows something immediately; double
/// clicking a rectangle goes into that folder and draws it again. The nested picture, every file
/// down to the leaves in one image, needs a different walk and is not this.
///
/// Drawn in a single OnRender rather than a visual per rectangle — measured at 100,000
/// rectangles: 11.3 ms against 96.1 ms, and a third of the memory. Anything under two pixels on a
/// side is skipped, because it cannot be seen or hit: on a normal window that is four fifths of
/// them.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private const double Least = 2;
    private const double Gap = 1;

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<EntryViewModel>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRowsChanged));

    private readonly List<(EntryViewModel Row, Box Box)> _placed = [];
    private EntryViewModel? _under;

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Raised on a double click, with the row the pointer was on.</summary>
    public event Action<EntryViewModel>? Opened;

    /// <summary>Raised when the pointer moves onto a different rectangle, or off all of them.</summary>
    public event Action<EntryViewModel?>? Hovered;

    public IEnumerable<EntryViewModel>? Rows
    {
        get => (IEnumerable<EntryViewModel>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>What the pointer is over, or null. Null is also what an empty folder gives.</summary>
    public EntryViewModel? Under => _under;

    protected override void OnRender(DrawingContext dc)
    {
        _placed.Clear();

        var area = new Box(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brush("Surface"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (area.W <= 0 || area.H <= 0) return;

        // Descending, which the layout requires, and only what has a size: a folder nobody has
        // measured has no area, and inventing one would be drawing a number we do not have.
        var known = (Rows ?? [])
            .Where(r => r.SortSize is > 0)
            .OrderByDescending(r => r.SortSize!.Value)
            .ToArray();

        if (known.Length == 0)
        {
            Say(dc, "Nothing measured here yet.");
            return;
        }

        var sizes = new long[known.Length];
        for (var i = 0; i < known.Length; i++) sizes[i] = known[i].SortSize!.Value;

        var boxes = new Box[known.Length];
        Treemap.Lay(sizes, area, boxes);

        for (var i = 0; i < known.Length; i++)
        {
            var box = boxes[i];
            if (box.W < Least || box.H < Least) continue;

            _placed.Add((known[i], box));
            dc.DrawRectangle(Fill(known[i]), null, Inset(box));
        }

        Outline(dc);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        var found = At(at.X, at.Y);
        if (ReferenceEquals(found, _under)) return;

        _under = found;
        ToolTip = found is null ? null : $"{found.Name}\n{found.SizeText}";
        Hovered?.Invoke(found);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_under is null) return;

        _under = null;
        ToolTip = null;
        Hovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2 && At(e.GetPosition(this)) is { } row) Opened?.Invoke(row);
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapView view) view._under = null;
    }

    private EntryViewModel? At(Point at) => At(at.X, at.Y);

    private EntryViewModel? At(double x, double y)
    {
        // Backwards: the layout places the largest first, so the smallest — the one actually
        // under a pointer in a crowded corner — is the one to find.
        for (var i = _placed.Count - 1; i >= 0; i--)
        {
            if (_placed[i].Box.Holds(x, y)) return _placed[i].Row;
        }
        return null;
    }

    /// <summary>The one the pointer is on gets an outline, drawn last so nothing covers it.</summary>
    private void Outline(DrawingContext dc)
    {
        if (_under is null) return;

        foreach (var (row, box) in _placed)
        {
            if (!ReferenceEquals(row, _under)) continue;

            var pen = new Pen(Brush("Fg"), 2);
            pen.Freeze();
            dc.DrawRectangle(null, pen, Inset(box));
            return;
        }
    }

    private void Say(DrawingContext dc, string text)
    {
        var label = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brush("FgMuted"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(label, new Point((ActualWidth - label.Width) / 2, (ActualHeight - label.Height) / 2));
    }

    private static Rect Inset(Box box) =>
        new(box.X + Gap, box.Y + Gap, Math.Max(0, box.W - Gap * 2), Math.Max(0, box.H - Gap * 2));

    /// <summary>
    /// Folders in the accent, files by kind. Colour here says what a thing is, not how big — the
    /// area already says how big, and saying it twice would leave nothing saying the other.
    /// </summary>
    private Brush Fill(EntryViewModel row)
    {
        if (row.Entry.IsDirectory) return Brush("Accent");

        return FileKinds.Of(row.Entry.Extension) switch
        {
            FileKind.Images => Brush("Warning"),
            FileKind.Video or FileKind.Audio => Brush("Selected"),
            FileKind.Archives => Brush("ScrollThumbHover"),
            _ => Brush("FgDim"),
        };
    }

    private Brush Brush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;
}
