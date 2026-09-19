using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Oan.Core.Sizes;

namespace Oan.App.Controls;

/// <summary>
/// The folder as areas: every file and folder a rectangle, its area its share of the whole.
///
/// It draws whichever of two things it has. Given a <see cref="Tree"/> it draws the nested
/// picture — folders holding their own contents, all the way down. Given only <see cref="Rows"/>
/// it draws one level, from the sizes the Size column already measured. The second is instant and
/// the first takes a walk, so the map shows the shallow picture while the deep one is on its way
/// rather than showing nothing at all.
///
/// Drawn in a single OnRender rather than a visual per rectangle — measured at 100,000
/// rectangles: 11.3 ms against 96.1 ms, and a third of the memory. Hit testing is a loop over the
/// boxes, which is the only thing a visual per rectangle would have bought.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    /// <summary>Smaller than this cannot be seen or hit, so it is not drawn.</summary>
    private const double Least = 2;

    /// <summary>
    /// How deep the picture goes, and how much room each child needs for its level to be drawn at
    /// all — about eight pixels square on average.
    /// </summary>
    private const int MaxDepth = 8;
    private const double Room = 64;

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree), typeof(SizeTree), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<EntryViewModel>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    private readonly List<(EntryViewModel Row, Box Box)> _shallow = [];
    private readonly List<(SizeTree Tree, int Index, Box Box)> _deep = [];
    private EntryViewModel? _row;
    private int _tile = -1;

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>A double click on one of this listing's own rows.</summary>
    public event Action<EntryViewModel>? Opened;

    /// <summary>A double click on a tile from deeper in the tree, which has no row here.</summary>
    public event Action<string>? OpenedPath;

    /// <summary>A right click on one of this listing's own rows, before the menu opens.</summary>
    public event Action<EntryViewModel>? Picked;

    /// <summary>
    /// The menu a right click opens. The list's own, handed over rather than built again, so a
    /// tile can never offer something its row does not or the other way round.
    /// </summary>
    public ContextMenu? Menu { get; set; }

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

    protected override void OnRender(DrawingContext dc)
    {
        _shallow.Clear();
        _deep.Clear();

        dc.DrawRectangle(Brush("Surface"), null, new Rect(0, 0, ActualWidth, ActualHeight));

        var area = new Box(0, 0, ActualWidth, ActualHeight);
        if (area.W <= 0 || area.H <= 0) return;

        if (Tree is { Count: > 0 } tree) Nested(dc, tree, -1, area, hue: -1, depth: 0);
        else Shallow(dc, area);

        Outline(dc);
    }

    /// <summary>
    /// One folder's children inside the rectangle the folder was given, then the same again
    /// inside each of theirs. The hue is decided at the top level and handed down, so everything
    /// inside a folder shares its colour — that is what makes the picture read as groups rather
    /// than as confetti.
    /// </summary>
    private void Nested(DrawingContext dc, SizeTree tree, int folder, Box area, int hue, int depth)
    {
        var kids = tree.ChildrenOf(folder);
        if (kids.Length == 0 || area.W < Least || area.H < Least) return;

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
            var mine = hue < 0 ? i : hue;

            if (tree.IsFolder(index) && Worth(tree, index, box, depth))
            {
                // The folder is the darker surround its children sit on, which is the only thing
                // saying where one folder ends and the next begins.
                dc.DrawRectangle(Cushions.Surround(mine), null, Rect(box));
                Nested(dc, tree, index, Shrink(box), mine, depth + 1);
                continue;
            }

            _deep.Add((tree, index, box));
            Tile(dc, box, mine, tree.Name(index), Format.Size(tree.Size[index]));
        }
    }

    /// <summary>
    /// Whether to draw a folder's contents or the folder itself. A folder holding thousands of
    /// things inside a small rectangle would draw thousands of specks, which say nothing and cost
    /// everything — so it is drawn as one tile, with its own name and total on it.
    /// </summary>
    private static bool Worth(SizeTree tree, int index, Box box, int depth)
    {
        if (depth >= MaxDepth || box.W <= 12 || box.H <= 12) return false;

        var kids = tree.ChildrenOf(index).Length;
        return kids > 0 && box.W * box.H / kids >= Room;
    }

    private void Shallow(DrawingContext dc, Box area)
    {
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
            if (boxes[i].W < Least || boxes[i].H < Least) continue;

            _shallow.Add((known[i], boxes[i]));
            Tile(dc, boxes[i], i, known[i].Name, known[i].SizeText);
        }
    }

    private void Tile(DrawingContext dc, Box box, int hue, string what, string much)
    {
        var rect = Rect(box);
        dc.DrawRectangle(Cushions.Fill(hue), null, rect);
        Cushions.Shade(dc, rect);
        Cushions.Label(dc, what, much, rect, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);

        if (Tree is not null)
        {
            var tile = Deepest(at);
            if (tile == _tile) return;

            _tile = tile;
            ToolTip = tile < 0 ? null : Told(tile);
            InvalidateVisual();
            return;
        }

        var found = RowAt(at);
        if (ReferenceEquals(found, _row)) return;

        _row = found;
        ToolTip = found is null ? null : $"{found.Name}\n{found.SizeText}";
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_row is null && _tile < 0) return;

        _row = null;
        _tile = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // Only this listing's own rows. A tile from deeper down is not in the list, and the menu
        // acts on the list's selection — offering Delete there would delete the wrong thing.
        if (RowAt(e.GetPosition(this)) is not { } row) return;

        Picked?.Invoke(row);
        if (Menu is null) return;

        Menu.PlacementTarget = this;
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount != 2) return;

        var at = e.GetPosition(this);

        if (Tree is { } tree && Deepest(at) is var tile && tile >= 0)
        {
            OpenedPath?.Invoke(tree.PathOf(_deep[tile].Index));
            return;
        }

        if (RowAt(at) is { } row) Opened?.Invoke(row);
    }

    /// <summary>The row whose tile holds this point, or null. Null for anything deeper.</summary>
    public EntryViewModel? RowAt(Point at)
    {
        // Backwards: the layout places the largest first, so the smallest — the one actually
        // under a pointer in a crowded corner — is the one to find.
        for (var i = _shallow.Count - 1; i >= 0; i--)
        {
            if (_shallow[i].Box.Holds(at.X, at.Y)) return _shallow[i].Row;
        }
        return null;
    }

    /// <summary>
    /// The last tile drawn that holds this point. Last, because the deeper a tile is the later it
    /// was drawn and the smaller it is — which is the one the pointer meant.
    /// </summary>
    private int Deepest(Point at)
    {
        for (var i = _deep.Count - 1; i >= 0; i--)
        {
            if (_deep[i].Box.Holds(at.X, at.Y)) return i;
        }
        return -1;
    }

    /// <summary>Name, size and where it is — the three things a rectangle cannot say itself.</summary>
    private string Told(int tile)
    {
        var (tree, index, _) = _deep[tile];
        return $"{tree.Name(index)}\n{Format.Size(tree.Size[index])}\n{tree.PathOf(index)}";
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreemapView view) return;

        view._row = null;
        view._tile = -1;
        view.ToolTip = null;
    }

    /// <summary>The one the pointer is on gets an outline, drawn last so nothing covers it.</summary>
    private void Outline(DrawingContext dc)
    {
        var box =
            Tree is not null ? _tile >= 0 && _tile < _deep.Count ? _deep[_tile].Box : (Box?)null :
            _row is null ? null :
            _shallow.FirstOrDefault(p => ReferenceEquals(p.Row, _row)).Box;

        if (box is not { W: > 0 } found) return;

        var pen = new Pen(Brush("Fg"), 2);
        pen.Freeze();
        dc.DrawRectangle(null, pen, Rect(found));
    }

    private void Say(DrawingContext dc, string text)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Brush("FgMuted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(label, new Point((ActualWidth - label.Width) / 2, (ActualHeight - label.Height) / 2));
    }

    /// <summary>
    /// The frame a folder keeps around its children: one pixel, so it reads as holding them
    /// without a dark band round everything.
    /// </summary>
    private static Box Shrink(Box box) =>
        new(box.X + 1, box.Y + 1, Math.Max(0, box.W - 2), Math.Max(0, box.H - 2));

    private static Rect Rect(Box box) => new(box.X, box.Y, box.W, box.H);

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
