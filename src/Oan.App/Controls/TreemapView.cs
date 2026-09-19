using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Oan.App.ViewModels;
using Oan.Core.Model;
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

    /// <summary>Under this a label would be clipped to nonsense, so none is drawn.</summary>
    private const double LabelWide = 54;
    private const double LabelTall = 30;

    /// <summary>
    /// A colour per rectangle, cycled by position. Not by file type: in a folder of folders every
    /// type is the same and the picture came out one flat blue, which is what made it unreadable.
    /// Adjacent rectangles differing is the whole job of colour here — the area already says how
    /// big, the label already says what.
    /// </summary>
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x7A, 0xD1, 0x8A), Color.FromRgb(0xFF, 0xB4, 0x5C),
        Color.FromRgb(0xE4, 0x7B, 0xB8), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x5F, 0xD4, 0xC4),
        Color.FromRgb(0xFF, 0x8A, 0x8A), Color.FromRgb(0xC9, 0xD1, 0x62), Color.FromRgb(0x6F, 0xA8, 0xFF),
        Color.FromRgb(0xD9, 0x9A, 0x6C),
    ];

    private static readonly Brush[] Fills = Build();

    /// <summary>
    /// A darker line along the bottom and right of each rectangle. With the cushion above it,
    /// that is what a raised surface looks like, and it means the edges read without a border
    /// drawn around every block — borders at this density eat the small rectangles alive.
    /// </summary>
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1));

    /// <summary>
    /// The whole tree under the folder, when it has been read. With it the map draws what is
    /// inside the folders; without it, only the folders themselves, from the sizes the Size
    /// column measured. The second is instant, the first takes a walk, so the map shows the
    /// shallow picture while the deep one is on its way rather than showing nothing.
    /// </summary>
    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree), typeof(SizeTree), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<EntryViewModel>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRowsChanged));

    private readonly List<(EntryViewModel Row, Box Box)> _placed = [];
    private readonly List<(SizeTree Tree, int Index, Box Box)> _deep = [];
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

    /// <summary>Raised on a right click, with the rectangle it landed on, before the menu opens.</summary>
    public event Action<EntryViewModel>? Picked;

    /// <summary>
    /// The menu a right click opens. The list's own, handed over rather than built again, so a
    /// rectangle can never offer something its row does not or the other way round.
    /// </summary>
    public System.Windows.Controls.ContextMenu? Menu { get; set; }

    public SizeTree? Tree
    {
        get => (SizeTree?)GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

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
        _deep.Clear();

        var area = new Box(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brush("Surface"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (area.W <= 0 || area.H <= 0) return;

        if (Tree is { Count: > 0 } tree)
        {
            Deep(dc, tree, -1, area, hue: -1, depth: 0);
            Outline(dc);
            return;
        }

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

            var rect = Inset(box);
            dc.DrawRectangle(Fills[i % Fills.Length], null, rect);
            Shade(dc, rect);
            Label(dc, known[i], rect);
        }

        Outline(dc);
    }

    /// <summary>
    /// One folder's children, laid out inside the rectangle the folder was given, then the same
    /// again inside each of theirs. The hue is decided at the top level and handed down, so
    /// everything inside a folder shares its colour — that is what makes the picture read as
    /// groups rather than as confetti.
    /// </summary>
    private void Deep(DrawingContext dc, SizeTree tree, int folder, Box area, int hue, int depth)
    {
        if (area.W < Least || area.H < Least || depth > 6) return;

        var kids = tree.ChildrenOf(folder);
        if (kids.Length == 0) return;

        var order = kids.ToArray();
        Array.Sort(order, (a, b) => tree.Size[b].CompareTo(tree.Size[a]));

        var sizes = new long[order.Length];
        for (var i = 0; i < order.Length; i++) sizes[i] = tree.Size[order[i]];

        var boxes = new Box[order.Length];
        Treemap.Lay(sizes, area, boxes);

        for (var i = 0; i < order.Length; i++)
        {
            var box = boxes[i];
            if (box.W < Least || box.H < Least) continue;

            var index = order[i];
            var mine = hue < 0 ? i % Fills.Length : hue;

            if (tree.IsFolder(index) && box.W > 10 && box.H > 10 && depth < 6)
            {
                // The folder itself is the darker surround its children sit on, which is the
                // only thing saying where one folder ends and the next begins.
                dc.DrawRectangle(Group[mine], null, Inset(box));
                Deep(dc, tree, index, Shrink(box), mine, depth + 1);
                continue;
            }

            var rect = Inset(box);
            _deep.Add((tree, index, box));
            dc.DrawRectangle(Fills[mine], null, rect);
            Shade(dc, rect);
            Label(dc, tree.Name(index), Format.Size(tree.Size[index]), rect);
        }
    }

    private static Box Shrink(Box box) =>
        new(box.X + 2, box.Y + 2, Math.Max(0, box.W - 4), Math.Max(0, box.H - 4));

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

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (At(e.GetPosition(this)) is not { } row) return;

        Picked?.Invoke(row);
        if (Menu is null) return;

        Menu.PlacementTarget = this;
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Menu.IsOpen = true;
        e.Handled = true;
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

    /// <summary>The row whose rectangle holds this point, or null. Used by the right click,
    /// which must act on where the pointer is rather than on where it last rested.</summary>
    public EntryViewModel? RowAt(Point at) => At(at.X, at.Y);

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

    /// <summary>Two strokes, bottom and right, which is the cheap half of a lit cushion.</summary>
    private static void Shade(DrawingContext dc, Rect rect)
    {
        if (rect.Width < 4 || rect.Height < 4) return;

        var bottom = rect.Bottom - 0.5;
        var right = rect.Right - 0.5;
        dc.DrawLine(Edge, new Point(rect.X, bottom), new Point(right, bottom));
        dc.DrawLine(Edge, new Point(right, rect.Y), new Point(right, bottom));
    }

    /// <summary>
    /// The name, and the size under it when there is room for both. Drawn dark on the fill rather
    /// than in the theme's foreground: the fills are light in either theme, and a light-on-light
    /// label is the same unreadable picture in a different way.
    /// </summary>
    private void Label(DrawingContext dc, EntryViewModel row, Rect rect) =>
        Label(dc, row.Name, row.SizeText, rect);

    private void Label(DrawingContext dc, string what, string much, Rect rect)
    {
        if (rect.Width < LabelWide || rect.Height < LabelTall) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var name = Text(what, 12, dpi, rect.Width - 8);
        var size = Text(much, 11, dpi, rect.Width - 8);

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(name, new Point(rect.X + 4, rect.Y + 3));
        if (rect.Height >= LabelTall + name.Height) dc.DrawText(size, new Point(rect.X + 4, rect.Y + 3 + name.Height));
        dc.Pop();
    }

    private static FormattedText Text(string text, double size, double dpi, double room)
    {
        var label = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, dpi)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(1, room),
        };
        return label;
    }

    /// <summary>
    /// One cushion per colour, not per rectangle. The gradient is in relative coordinates, so the
    /// same frozen brush stretches to any rectangle it is given — ten brushes for the whole map,
    /// built once, rather than an allocation per block on every redraw.
    /// </summary>
    /// <summary>The surround a folder's children are drawn on: its own hue, much darker.</summary>
    private static readonly Brush[] Group = BuildGroups();

    private static Brush[] BuildGroups()
    {
        var brushes = new Brush[Hues.Length];
        for (var i = 0; i < Hues.Length; i++) brushes[i] = Frozen(new SolidColorBrush(Mix(Hues[i], Colors.Black, 0.62)));
        return brushes;
    }

    private static Brush[] Build()
    {
        var brushes = new Brush[Hues.Length];
        for (var i = 0; i < Hues.Length; i++)
        {
            var cushion = new LinearGradientBrush(
                Mix(Hues[i], Colors.White, 0.26),
                Mix(Hues[i], Colors.Black, 0.30),
                new Point(0.1, 0), new Point(0.9, 1));

            brushes[i] = Frozen(cushion);
        }
        return brushes;
    }

    private static Color Mix(Color colour, Color towards, double amount) => Color.FromRgb(
        (byte)(colour.R + (towards.R - colour.R) * amount),
        (byte)(colour.G + (towards.G - colour.G) * amount),
        (byte)(colour.B + (towards.B - colour.B) * amount));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private Brush Brush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;
}
