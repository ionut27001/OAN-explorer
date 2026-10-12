using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Oan.App.Controls;
using Oan.Core.Model;
using Oan.Core.Sizes;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// A paint taken apart. TreemapPaintBenchmark says what the whole thing costs; this says which
/// part of it costs that — the walk down the tree, the three arrays per folder, the strings, the
/// rectangles, and the labels — and then puts a pooled and an iterative walk against the one the
/// map uses.
///
/// The walk is a copy of TreemapView.Nested with the same rules, and a copy can drift. It reads
/// the map's own Least, MaxDepth and Room off the class, and the rectangles and labels are drawn
/// by the map's own Cushions, so only Worth's minimum side is written out twice. The tile count
/// printed here is the check: TreemapPaintBenchmark counts the tiles the real control actually
/// drew, and if the two disagree this copy is measuring something else.
///
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~TreemapLayoutBenchmark" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class TreemapLayoutBenchmark(ITestOutputHelper output)
{
    private static readonly Box Window = new(0, 0, 1400, 900);

    /// <summary>Where the bytes go: the buffers alone, then the strings, then the hit testing list.</summary>
    [Fact]
    public void What_one_layout_allocates()
    {
        output.WriteLine(Trees.Stamp());
        output.WriteLine(Walk.Rules());

        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            foreach (var detail in new[] { Detail.Bare, Detail.Text, Detail.Text | Detail.Deep })
            {
                var run = Sta.Run(() => new Walk(tree, Way.Arrays, detail).Repeat(Window, 9));
                output.WriteLine($"layout\t{root}\t{detail}\t{run}");
            }

            var counts = Sta.Run(() => new Walk(tree, Way.Arrays, Detail.Text).Repeat(Window, 1));
            output.WriteLine($"arrays\t{root}\t{Walk.Arithmetic(counts)}");
        }
    }

    /// <summary>
    /// The same walk, recording into a DrawingVisual through the map's own Cushions: first the
    /// layout alone, then the rectangles and cushions on top, then the labels on top of that.
    /// The last one is a whole paint and should land on what the real control measures.
    /// </summary>
    [Fact]
    public void What_the_drawing_costs()
    {
        Detail[] steps = [Detail.Text | Detail.Deep,
                          Detail.Text | Detail.Deep | Detail.Draw,
                          Detail.Text | Detail.Deep | Detail.Draw | Detail.Label];

        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            foreach (var detail in steps)
            {
                var run = Sta.Run(() => new Walk(tree, Way.Arrays, detail).Repeat(Window, 7));
                output.WriteLine($"draw\t{root}\t{detail}\t{run}");
            }
        }
    }

    /// <summary>Renting the buffers, and walking with a stack instead of the call stack.</summary>
    [Fact]
    public void Pooling_the_buffers_and_walking_iteratively()
    {
        const Detail Same = Detail.Text | Detail.Deep;

        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            foreach (var way in new[] { Way.Arrays, Way.Pooled, Way.Iterative })
            {
                var run = Sta.Run(() => new Walk(tree, way, Same).Repeat(Window, 9));
                output.WriteLine($"way\t{root}\t{way}\t{run}");
            }
        }
    }
}

internal enum Way { Arrays, Pooled, Iterative }

/// <summary>How much of what a paint does to reproduce. Drawing is only wired to Arrays.</summary>
[Flags]
internal enum Detail
{
    Bare = 0,
    Text = 1,
    Deep = 2,
    Draw = 4,
    Label = 8,
}

internal sealed record Walked(
    double[] Rounds, long Bytes, int Tiles, int Folders, int Labels, int Levels, long Children, double Checksum)
{
    public override string ToString() =>
        $"ms={Spread.Middle(Rounds):F2}\tmin_ms={Rounds.Min():F2}\tmax_ms={Rounds.Max():F2}\t" +
        $"kb={Bytes / 1024.0:F0}\ttiles={Tiles}\tfolders={Folders}\tlabels={Labels}\t" +
        $"levels={Levels}\tchildren={Children}\tsum={Checksum:F0}";
}

/// <summary>TreemapView.Nested with three ways of holding the buffers and the drawing optional.</summary>
internal sealed class Walk(SizeTree tree, Way way, Detail detail)
{
    private static readonly double Least = Const("Least", 2);
    private static readonly int MaxDepth = (int)Const("MaxDepth", 8);
    private static readonly double Room = Const("Room", 100);

    /// <summary>Worth's own minimum side. A literal in the map, so it cannot be read off it —
    /// the tile count printed here against the real one is what catches it drifting.</summary>
    private const double Seen = 20;

    private readonly Comparison<int> _descending = (a, b) => tree.Size[b].CompareTo(tree.Size[a]);
    private readonly List<(SizeTree Tree, int Index, Box Box)> _deep = [];
    private readonly DrawingVisual _visual = new();
    private readonly double _dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

    private DrawingContext? _dc;
    private int _tiles;
    private int _folders;
    private int _labels;
    private int _levels;
    private long _children;

    /// <summary>Everything the walk worked out and threw away, added up: without somewhere for
    /// it to go the compiler is free to decide the walk was never needed.</summary>
    private double Sum { get; set; }

    public static string Rules() => $"rules\tleast={Least}\tmaxdepth={MaxDepth}\troom={Room}\tseen={Seen}\t" +
                                    $"label={Cushions.LabelWide}x{Cushions.LabelTall}";

    /// <summary>The three arrays, counted rather than measured: what they must weigh given how
    /// many folders were opened and how many children they held between them.</summary>
    public static string Arithmetic(Walked run)
    {
        var order = run.Levels * 24 + run.Children * 4;
        var sizes = run.Levels * 24 + run.Children * 8;
        var boxes = run.Levels * 24 + run.Children * 32;
        var sort = run.Levels * (24 + 64);

        return $"levels={run.Levels}\tchildren={run.Children}\torder_kb={order / 1024.0:F0}\t" +
               $"sizes_kb={sizes / 1024.0:F0}\tboxes_kb={boxes / 1024.0:F0}\t" +
               $"three_kb={(order + sizes + boxes) / 1024.0:F0}\tsort_closure_kb={sort / 1024.0:F0}";
    }

    public Walked Repeat(Box area, int rounds)
    {
        for (var i = 0; i < 3; i++) Once(area);

        var times = new double[rounds];
        long bytes = 0;

        for (var i = 0; i < rounds; i++)
        {
            var run = Once(area);
            times[i] = run.Ms;
            bytes += run.Bytes;
        }

        return new Walked(times, bytes / rounds, _tiles, _folders, _labels, _levels, _children, Sum);
    }

    private (double Ms, long Bytes) Once(Box area)
    {
        _tiles = _folders = _labels = _levels = 0;
        _children = 0;
        _deep.Clear();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        _dc = (detail & Detail.Draw) != 0 ? _visual.RenderOpen() : null;
        switch (way)
        {
            case Way.Arrays: Arrays(tree, -1, area, 0); break;
            case Way.Pooled: Pooled(-1, area, 0); break;
            default: Iterative(area); break;
        }
        _dc?.Close();

        clock.Stop();
        return (clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Exactly what the map does today, down to capturing the tree in the comparison.</summary>
    private void Arrays(SizeTree owner, int folder, Box area, int depth)
    {
        var kids = owner.ChildrenOf(folder);
        if (kids.Length == 0 || area.W < Least || area.H < Least) return;

        _levels++;
        _children += kids.Length;

        var order = kids.ToArray();
        Array.Sort(order, (a, b) => owner.Size[b].CompareTo(owner.Size[a]));

        var sizes = new long[order.Length];
        for (var i = 0; i < order.Length; i++) sizes[i] = owner.Size[order[i]];

        var boxes = new Box[order.Length];
        Treemap.Lay(sizes, area, boxes);

        for (var i = 0; i < order.Length; i++)
        {
            var box = boxes[i];
            if (box.W < Least || box.H < Least) continue;

            var index = order[i];
            var (name, much) = Named(index);

            if (owner.IsFolder(index) && Worth(index, box, depth))
            {
                _folders++;
                Arrays(owner, index, Below(box, Header(box)), depth + 1);
                Group(box, name, much);
                continue;
            }

            Tile(index, box, name, much);
        }
    }

    /// <summary>The same walk with the three buffers rented and the comparison built once.</summary>
    private void Pooled(int folder, Box area, int depth)
    {
        var kids = tree.ChildrenOf(folder);
        if (kids.Length == 0 || area.W < Least || area.H < Least) return;

        var count = kids.Length;
        _levels++;
        _children += count;

        var order = ArrayPool<int>.Shared.Rent(count);
        var sizes = ArrayPool<long>.Shared.Rent(count);
        var boxes = ArrayPool<Box>.Shared.Rent(count);

        try
        {
            kids.CopyTo(order);
            order.AsSpan(0, count).Sort(_descending);
            for (var i = 0; i < count; i++) sizes[i] = tree.Size[order[i]];
            Treemap.Lay(sizes.AsSpan(0, count), area, boxes.AsSpan(0, count));

            for (var i = 0; i < count; i++)
            {
                var box = boxes[i];
                if (box.W < Least || box.H < Least) continue;

                var index = order[i];
                var (name, much) = Named(index);

                if (tree.IsFolder(index) && Worth(index, box, depth))
                {
                    _folders++;
                    Pooled(index, Below(box, Header(box)), depth + 1);
                    continue;
                }

                Tile(index, box, name, much);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
            ArrayPool<long>.Shared.Return(sizes);
            ArrayPool<Box>.Shared.Return(boxes);
        }
    }

    /// <summary>
    /// The same walk again with an explicit stack. A level is only closed once its children are
    /// done, which is where the folder's own frame and name would be drawn — the one thing the
    /// recursion was buying, and the reason a flat loop over tiles would not do.
    /// </summary>
    private void Iterative(Box area)
    {
        var stack = new Level[MaxDepth + 2];
        var top = 0;
        stack[0] = Open(-1, area, 0);

        while (top >= 0)
        {
            ref var level = ref stack[top];
            if (level.At >= level.Count)
            {
                // Where Cushions.Group would go.
                Close(ref level);
                top--;
                continue;
            }

            var box = level.Boxes[level.At];
            var index = level.Order[level.At++];
            if (box.W < Least || box.H < Least) continue;

            var (name, much) = Named(index);
            if (!tree.IsFolder(index) || !Worth(index, box, level.Depth) || top + 2 >= stack.Length)
            {
                Tile(index, box, name, much);
                continue;
            }

            _folders++;
            var child = Open(index, Below(box, Header(box)), level.Depth + 1);
            if (child.Count == 0) Close(ref child);
            else stack[++top] = child;
        }
    }

    private Level Open(int folder, Box area, int depth)
    {
        var kids = tree.ChildrenOf(folder);
        if (kids.Length == 0 || area.W < Least || area.H < Least)
            return new Level { Order = [], Sizes = [], Boxes = [] };

        var count = kids.Length;
        _levels++;
        _children += count;

        var level = new Level
        {
            Depth = depth,
            Count = count,
            Order = ArrayPool<int>.Shared.Rent(count),
            Sizes = ArrayPool<long>.Shared.Rent(count),
            Boxes = ArrayPool<Box>.Shared.Rent(count),
        };

        kids.CopyTo(level.Order);
        level.Order.AsSpan(0, count).Sort(_descending);
        for (var i = 0; i < count; i++) level.Sizes[i] = tree.Size[level.Order[i]];
        Treemap.Lay(level.Sizes.AsSpan(0, count), area, level.Boxes.AsSpan(0, count));

        return level;
    }

    private static void Close(ref Level level)
    {
        if (level.Count == 0) return;

        ArrayPool<int>.Shared.Return(level.Order);
        ArrayPool<long>.Shared.Return(level.Sizes);
        ArrayPool<Box>.Shared.Return(level.Boxes);
        level.Count = 0;
    }

    /// <summary>The name and the size, which Nested builds for every child it looks at.</summary>
    private (string Name, string Much) Named(int index)
    {
        if ((detail & Detail.Text) == 0) return (string.Empty, string.Empty);

        var name = tree.Name(index);
        var much = Format.Size(tree.Size[index]);
        Sum += much.Length;
        return (name, much);
    }

    private void Tile(int index, Box box, string name, string much)
    {
        _tiles++;
        if ((detail & Detail.Deep) != 0) _deep.Add((tree, index, box));
        if (box.W >= Cushions.LabelWide && box.H >= Cushions.LabelTall) _labels++;
        if (_dc is null) return;

        var rect = new Rect(box.X, box.Y, box.W, box.H);
        _dc.DrawRectangle(Cushions.Fill(Cushions.HueOf(name)), null, rect);
        Cushions.Shade(_dc, rect);
        if ((detail & Detail.Label) != 0) Cushions.Label(_dc, name, much, rect, _dpi);
    }

    private void Group(Box box, string name, string much)
    {
        if (_dc is null) return;

        Cushions.Group(_dc, new Rect(box.X, box.Y, box.W, box.H), name, much, _dpi);
    }

    private bool Worth(int index, Box box, int depth)
    {
        if (depth >= MaxDepth || box.W < Seen || box.H < Seen) return false;

        var kids = tree.ChildrenOf(index).Length;
        return kids > 0 && box.W * box.H / kids >= Room;
    }

    private static Box Below(Box box, double band) =>
        band <= 0 ? box : new Box(box.X, box.Y + band, box.W, Math.Max(0, box.H - band));

    private static double Header(Box box) => Cushions.Header(new Rect(box.X, box.Y, box.W, box.H));

    /// <summary>The map's own constant, read off the class so this copy cannot use a stale one.</summary>
    private static double Const(string name, double fallback) =>
        typeof(TreemapView).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?
            .GetRawConstantValue() is IConvertible value
            ? value.ToDouble(CultureInfo.InvariantCulture)
            : fallback;

    private struct Level
    {
        public int Depth;
        public int Count;
        public int At;
        public int[] Order;
        public long[] Sizes;
        public Box[] Boxes;
    }
}
