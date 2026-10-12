using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Oan.App.ViewModels;
using Oan.Core.Model;
using Oan.Core.Sizes;

namespace Oan.App.Controls;

/// <summary>
/// The folder as areas. Draws the nested <see cref="Tree"/> when it has one, else one level from
/// <see cref="Rows"/>. One drawing, not a visual per rectangle: at 100,000 rectangles, 11.3 ms against 96.1 ms.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    /// <summary>Smaller than this cannot be seen or hit, so it is not drawn.</summary>
    private const double Least = 2;

    /// <summary>How deep the picture goes, and the area each child needs on average for its level to be drawn.</summary>
    private const int MaxDepth = 8;
    private const double Room = 100;

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree), typeof(SizeTree), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<EntryViewModel>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    private readonly TreemapHits _hits = new();
    private EntryViewModel? _row;
    private int _tile = -1;

    /// <summary>Two visuals: sharing one meant every mouse move re-submitted all the tiles to move one outline.</summary>
    private readonly DrawingVisual _tiles = new();
    private readonly DrawingVisual _hover = new();

    private Size _drawnAt;
    private double _dpi = 1;

    /// <summary>While the panel is resized the picture is stretched; it is laid out again once the dragging stops.</summary>
    private readonly DispatcherTimer _settled = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _stretching;

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;

        AddVisualChild(_tiles);
        AddVisualChild(_hover);

        _settled.Tick += (_, _) =>
        {
            _settled.Stop();
            _stretching = false;
            _tiles.Transform = null;
            _drawnAt = default;
            InvalidateVisual();
        };
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _tiles : _hover;

    /// <summary>A tile was clicked.</summary>
    public event Action<string>? ChosenPath;

    /// <summary>A tile was double-clicked, with whether it is a folder.</summary>
    public event Action<string, bool>? OpenedPath;

    /// <summary>Right-clicked. The menu acts on the list's selection, so the listener selects the path, then opens it.</summary>
    public event Action<string>? PickedPath;

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
        var size = new Size(ActualWidth, ActualHeight);
        if (_drawnAt != size) Paint(size);
    }

    /// <summary>How many times the tiles have been laid out. A test pins that hovering is not one of them.</summary>
    internal int Paints { get; private set; }

    private void Paint(Size size)
    {
        Paints++;
        _hits.Clear();
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using (var dc = _tiles.RenderOpen())
        {
            dc.DrawRectangle(Brush("Surface"), null, new Rect(0, 0, size.Width, size.Height));

            var area = new Box(0, 0, size.Width, size.Height);
            if (area.W > 0 && area.H > 0)
            {
                if (Tree is { Count: > 0 } tree) Nested(dc, tree, -1, area, hue: -1, depth: 0);
                else Shallow(dc, area);
            }
        }

        _drawnAt = size;
        DrawHover();
    }

    private void DrawHover()
    {
        using var dc = _hover.RenderOpen();

        var box =
            Tree is not null ? _hits.BoxOf(_tile) :
            _row is null ? null :
            _hits.BoxOf(_row);

        if (box is not { W: > 0 } found) return;

        var pen = new Pen(Brush("Fg"), 2);
        pen.Freeze();
        dc.DrawRectangle(null, pen, Rect(found));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // Nothing drawn yet, so there is nothing to stretch.
        if (_drawnAt.Width <= 0 || _drawnAt.Height <= 0)
        {
            _drawnAt = default;
            InvalidateVisual();
            return;
        }

        _stretching = true;
        _tiles.Transform = new ScaleTransform(
            ActualWidth / _drawnAt.Width, ActualHeight / _drawnAt.Height);

        // The boxes belong to the old size, so nothing under the pointer is where it says.
        _tile = -1;
        _row = null;
        ToolTip = null;
        DrawHover();

        _settled.Stop();
        _settled.Start();
    }

    /// <summary>One folder's children inside the folder's rectangle, then the same inside each of theirs.</summary>
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
            var name = tree.Name(index);
            var much = Format.Size(tree.Size[index]);

            if (tree.IsFolder(index) && Worth(tree, index, box, depth))
            {
                // The children fill the whole rectangle and the folder is drawn over them: a margin per level adds up.
                Nested(dc, tree, index, Below(box, Cushions.Header(Rect(box))), hue, depth + 1);
                Cushions.Group(dc, Rect(box), name, much, _dpi);
                continue;
            }

            _hits.Add(tree, index, box);
            Tile(dc, box, Cushions.HueOf(name), name, much);
        }
    }

    /// <summary>Whether to draw a folder's contents, or the folder itself as one tile.</summary>
    private static bool Worth(SizeTree tree, int index, Box box, int depth)
    {
        if (depth >= MaxDepth || box.W < 20 || box.H < 20) return false;

        var kids = tree.ChildrenOf(index).Length;
        return kids > 0 && box.W * box.H / kids >= Room;
    }

    private void Shallow(DrawingContext dc, Box area)
    {
        // Descending, which the layout requires, and only what has been measured.
        var known = (Rows ?? [])
            .Where(r => r.SortSize is > 0)
            .OrderByDescending(r => r.SortSize!.Value)
            .ToArray();

        if (known.Length == 0)
        {
            Cushions.Middle(dc, "Nothing measured here yet.",
                new Rect(0, 0, ActualWidth, ActualHeight), _dpi, Brush("FgMuted"));
            return;
        }

        var sizes = new long[known.Length];
        for (var i = 0; i < known.Length; i++) sizes[i] = known[i].SortSize!.Value;

        var boxes = new Box[known.Length];
        Treemap.Lay(sizes, area, boxes);

        for (var i = 0; i < known.Length; i++)
        {
            if (boxes[i].W < Least || boxes[i].H < Least) continue;

            _hits.Add(known[i], boxes[i]);
            Tile(dc, boxes[i], i, known[i].Name, known[i].SizeText);
        }
    }

    private void Tile(DrawingContext dc, Box box, int hue, string what, string much)
    {
        var rect = Rect(box);
        dc.DrawRectangle(Cushions.Fill(hue), null, rect);
        Cushions.Shade(dc, rect);
        Cushions.Label(dc, what, much, rect, _dpi);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_stretching) return;

        var at = e.GetPosition(this);

        if (Tree is not null)
        {
            var tile = _hits.Deepest(at);
            if (tile == _tile) return;

            _tile = tile;
            ToolTip = tile < 0 ? null : _hits.Told(tile);
            DrawHover();
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
        DrawHover();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_stretching || At(e.GetPosition(this)) is not { } hit) return;

        PickedPath?.Invoke(hit.Path);
        e.Handled = true;
    }

    /// <summary>Opens the shared menu where the pointer is. Called once the item is selected.</summary>
    public void ShowMenu()
    {
        if (Menu is null) return;

        Menu.PlacementTarget = this;
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (_stretching || At(e.GetPosition(this)) is not { } hit) return;

        if (e.ClickCount == 2) OpenedPath?.Invoke(hit.Path, hit.Folder);
        else ChosenPath?.Invoke(hit.Path);
    }

    /// <summary>What a point names, against whichever of the two maps was drawn.</summary>
    private (string Path, bool Folder)? At(Point at)
    {
        if (Tree is null)
            return RowAt(at) is { } row ? (row.Entry.FullPath, row.Entry.IsDirectory) : null;

        var tile = _hits.Deepest(at);
        return tile < 0 ? null : (_hits.PathOf(tile), _hits.IsFolder(tile));
    }

    /// <summary>The row whose tile holds this point, or null. Null for anything deeper.</summary>
    public EntryViewModel? RowAt(Point at) => _hits.RowAt(at);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreemapView view) return;

        view._drawnAt = default;
        view._row = null;
        view._tile = -1;
        view.ToolTip = null;
    }

    /// <summary>The box with the folder's name band taken off the top.</summary>
    private static Box Below(Box box, double band) =>
        band <= 0 ? box : new Box(box.X, box.Y + band, box.W, Math.Max(0, box.H - band));

    private static Rect Rect(Box box) => new(box.X, box.Y, box.W, box.H);

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
