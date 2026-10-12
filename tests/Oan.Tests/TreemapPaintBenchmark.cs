using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Oan.App.Controls;
using Oan.Core.Sizes;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What the size map costs to put on screen, on real folders: how long one full paint takes, how
/// many tiles survive the pruning in Worth, how much the paint allocates, and whether dragging the
/// window edge keeps up.
///
/// The paint is called directly rather than through a render pass. OnRender is only reached by
/// rendering into a bitmap, and rasterising a map of a few thousand gradient-filled rectangles in
/// software costs thirty to ninety milliseconds here — an order of magnitude more than the thing
/// being measured, and noisy enough to bury it. In the app that rasterising happens on the
/// composition thread; what the UI thread pays is the layout and the recording, which is what
/// Paint is.
///
/// Kept out of the normal pass by Category=Benchmark. Run it on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~TreemapPaintBenchmark" --logger "console;verbosity=detailed"
/// Point it elsewhere with OAN_MAP_BENCH_ROOTS (semicolon-separated).
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class TreemapPaintBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void One_full_paint_of_each_tree()
    {
        output.WriteLine(Trees.Stamp());

        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            var paint = Sta.Run(() => Painting.Measure(tree, 1400, 900, rounds: 9));
            output.WriteLine($"paint\t{root}\tentries={tree.Count}\t1400x900\t{paint}");
        }
    }

    /// <summary>
    /// The same tree in four windows. The pruning is measured in pixels, so a bigger window is
    /// not the same picture bigger — it is more tiles, and that is where the cost comes from.
    /// </summary>
    [Fact]
    public void The_same_tree_in_four_window_sizes()
    {
        (int W, int H)[] windows = [(900, 600), (1400, 900), (1920, 1080), (2560, 1440)];

        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            foreach (var (w, h) in windows)
            {
                var paint = Sta.Run(() => Painting.Measure(tree, w, h, rounds: 7));
                output.WriteLine($"window\t{root}\tentries={tree.Count}\t{w}x{h}\t{paint}");
            }
        }
    }

    /// <summary>
    /// A drag, frame by frame: sixty widths, each one a full layout because OnRenderSizeChanged
    /// throws the picture away. 16.7 ms is one frame at 60 Hz — past that the edge stops
    /// following the mouse and starts catching up with it.
    /// </summary>
    [Fact]
    public void Dragging_the_window_edge()
    {
        foreach (var root in Trees.Roots)
        {
            if (Trees.For(root, output) is not { } tree) continue;

            var drag = Sta.Run(() => Painting.Drag(tree, 1400, 900, to: 800, step: 10));
            output.WriteLine($"drag\t{root}\tentries={tree.Count}\t{drag}");
        }
    }
}

/// <summary>One measured paint, with what was in it.</summary>
internal sealed record Painted(
    double[] Rounds, long PaintBytes, int Paints, double RasterMs,
    int Tiles, int Folders, int Glyphs, int Primitives)
{
    private double PaintMs => Spread.Middle(Rounds);

    public override string ToString() =>
        $"paint_ms={PaintMs:F2}\tpaint_kb={PaintBytes / 1024.0:F0}\tpaints={Paints}\t" +
        $"raster_ms={RasterMs:F2}\ttiles={Tiles}\tfolders={Folders}\tglyphs={Glyphs}\tprims={Primitives}\t" +
        $"min_ms={Rounds.Min():F2}\tmax_ms={Rounds.Max():F2}\t" +
        $"each=[{string.Join(' ', Rounds.Select(ms => ms.ToString("F2")))}]";
}

/// <summary>The middle of a handful of runs.</summary>
internal static class Spread
{
    public static double Middle(double[] values) => At(values, 0.5);

    public static double At(double[] values, double share)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)(share * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }
}

/// <summary>Drives a real <see cref="TreemapView"/> and times it.</summary>
internal static class Painting
{
    public static Painted Measure(SizeTree tree, int w, int h, int rounds)
    {
        var map = new TreemapView { Tree = tree };
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var size = new Size(w, h);

        Layout(map, w, h);
        for (var i = 0; i < 4; i++) Repaint(map, size);

        var times = new double[rounds];
        long bytes = 0;
        var before = map.Paints;

        for (var i = 0; i < rounds; i++)
        {
            var run = Repaint(map, size);
            times[i] = run.Ms;
            bytes += run.Bytes;
        }

        var painted = map.Paints - before;

        // What rasterising the same picture costs here, for scale only: in the app that happens
        // on the composition thread, not on this one.
        var raster = Draw(map, target);

        var (tiles, folders, glyphs, prims) = Count(map);
        return new Painted(times, bytes / rounds, painted, raster.Ms,
                           tiles, folders, glyphs, prims);
    }

    public static Dragged Drag(SizeTree tree, int w, int h, int to, int step)
    {
        var map = new TreemapView { Tree = tree };

        Layout(map, w, h);
        for (var i = 0; i < 4; i++) Repaint(map, new Size(w, h));

        var paints = new List<double>();
        var kb = new List<double>();

        for (var width = w; width >= to; width -= step)
        {
            // A new size is what a drag is, and it is what throws the picture away.
            Layout(map, width, h);
            var one = Repaint(map, new Size(width, h));

            paints.Add(one.Ms);
            kb.Add(one.Bytes / 1024.0);
        }

        return new Dragged([.. paints], [.. kb]);
    }

    /// <summary>
    /// One layout and one recording of every rectangle, called where it is rather than through a
    /// bitmap. The invoke costs about a microsecond and the paint costs milliseconds.
    /// </summary>
    private static (double Ms, long Bytes) Repaint(TreemapView map, Size size)
    {
        var paint = typeof(TreemapView).GetMethod("Paint", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingMethodException("TreemapView.Paint is gone; this measures nothing");

        object[] args = [size];
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        paint.Invoke(map, args);

        clock.Stop();
        return (clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Layout(TreemapView map, int w, int h)
    {
        map.Width = w;
        map.Height = h;
        map.Measure(new Size(w, h));
        map.Arrange(new Rect(0, 0, w, h));
        map.UpdateLayout();
    }

    private static (double Ms, long Bytes) Draw(TreemapView map, RenderTargetBitmap target)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        target.Clear();
        target.Render(map);

        clock.Stop();
        return (clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// What was really drawn, read back off the visuals the map fills rather than counted by a
    /// copy of its rules — a copy can drift, and then the count is of the copy.
    /// </summary>
    private static (int Tiles, int Folders, int Glyphs, int Prims) Count(TreemapView map)
    {
        var tally = new Tally();

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(map); i++)
        {
            if (VisualTreeHelper.GetChild(map, i) is Visual child &&
                VisualTreeHelper.GetDrawing(child) is { } drawing) tally.Walk(drawing);
        }

        return (tally.Tiles, tally.Folders, tally.Glyphs, tally.Prims);
    }

    private sealed class Tally
    {
        /// <summary>The map's own tile brushes, by reference: one of these is a tile and
        /// nothing else is.</summary>
        private static readonly Brush[] Fills = [.. Enumerable.Range(0, Cushions.Count).Select(Cushions.Fill)];

        public int Tiles;
        public int Folders;
        public int Glyphs;
        public int Prims;

        public void Walk(Drawing drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    return;

                case GlyphRunDrawing:
                    Glyphs++;
                    Prims++;
                    return;

                case GeometryDrawing shape:
                    Prims++;
                    if (shape.Brush is { } brush && Array.IndexOf(Fills, brush) >= 0) Tiles++;
                    else if (shape.Brush is null && shape.Geometry is RectangleGeometry) Folders++;
                    return;

                default:
                    Prims++;
                    return;
            }
        }
    }
}

/// <summary>Sixty frames of a drag.</summary>
internal sealed record Dragged(double[] Paint, double[] Kb)
{
    public override string ToString() =>
        $"frames={Paint.Length}	paint_p50={Spread.At(Paint, 0.50):F2}	paint_p95={Spread.At(Paint, 0.95):F2}	" +
        $"paint_min={Paint.Min():F2}	paint_max={Paint.Max():F2}	kb_p50={Spread.At(Kb, 0.50):F0}	" +
        $"over_16.7ms={Paint.Count(ms => ms > 16.7)}	over_33ms={Paint.Count(ms => ms > 33.3)}	" +
        $"first10=[{string.Join(' ', Paint.Take(10).Select(ms => ms.ToString("F2")))}]	" +
        $"last10=[{string.Join(' ', Paint.TakeLast(10).Select(ms => ms.ToString("F2")))}]";
}

/// <summary>
/// The trees, read once. A user profile takes tens of seconds to walk and three benchmarks want
/// the same one; reading it per test would measure the disk, repeatedly.
/// </summary>
internal static class Trees
{
    private static readonly Dictionary<string, SizeTree?> Read = [];

    public static string[] Roots =>
        (Environment.GetEnvironmentVariable("OAN_MAP_BENCH_ROOTS")
         ?? $@"{Repo.Root};D:\dev;{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What was measured, so a number can be tied to the code that produced it — the
    /// map is being changed while this runs.</summary>
    public static string Stamp()
    {
        var files = new[]
        {
            "src/Oan.App/Controls/TreemapView.cs", "src/Oan.App/Controls/Cushions.cs",
            "src/Oan.Core/Sizes/Treemap.cs", "src/Oan.Core/Sizes/SizeTree.cs",
        };

        var stamps = files.Select(f =>
        {
            var path = Path.Combine(Repo.Root, f);
            if (!File.Exists(path)) return $"{f}=missing";
            var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(path)));
            return $"{f}={hash[..8]}";
        });

        return $"source\t{string.Join('\t', stamps)}";
    }

    public static SizeTree? For(string root, ITestOutputHelper output)
    {
        if (Read.TryGetValue(root, out var known)) return known;

        if (!Directory.Exists(root))
        {
            output.WriteLine($"missing\t{root}");
            return Read[root] = null;
        }

        var clock = Stopwatch.StartNew();
        var tree = SizeTree.Read(root);
        clock.Stop();

        output.WriteLine($"tree\t{root}\tentries={tree?.Count ?? 0}\tread_ms={clock.ElapsedMilliseconds}\t" +
                         $"skipped={tree?.Skipped ?? 0}\tnotfollowed={tree?.NotFollowed ?? 0}");

        return Read[root] = tree is { Count: > 0 } ? tree : null;
    }
}

/// <summary>WPF wants one thread and it wants it apartment-threaded.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        }, 8 * 1024 * 1024);

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException("benchmark failed", failure);
        return result;
    }
}
