using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What it costs to put the rectangles on screen, four ways.
///
/// Read the caveat before the numbers. This rasterises through RenderTargetBitmap, which is WPF's
/// SOFTWARE rasteriser — there is no window here, so there is no compositor and no GPU. What it
/// measures honestly is the half that is the same either way: building the visual, the memory the
/// visual holds, and how the cost grows with the number of rectangles. What it does NOT measure is
/// the per-frame GPU cost of the same content in a real window, which is lower. Treat the raster
/// column as an upper bound and a shape, not as a frame time.
///
/// Kept out of the normal pass by Category=Benchmark. Run it on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~TreemapDraw" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class TreemapDrawBenchmark(ITestOutputHelper output)
{
    const int Width = 1360;
    const int Height = 820;

    static readonly Box Canvas = new(0, 0, Width, Height);
    static readonly int[] Counts = [1_000, 10_000, 100_000];

    static readonly uint[] Raw =
    [
        0xFF4C78A8, 0xFFF58518, 0xFFE45756, 0xFF72B7B2, 0xFF54A24B, 0xFFEECA3B, 0xFFB279A2, 0xFFFF9DA6,
        0xFF9D755D, 0xFFBAB0AC, 0xFF3D6D9E, 0xFFC96F14, 0xFFBF4644, 0xFF5E9A96, 0xFF45883E, 0xFFC9A832,
    ];

    /// <summary>
    /// Frozen, and built once. An unfrozen brush hung on 100 000 draw instructions is 100 000
    /// change-notification subscriptions, which is a different benchmark entirely.
    /// Declared after <see cref="Raw"/> and not before: static initialisers run in the order they
    /// are written, and the first version of this file read Raw while it was still null.
    /// </summary>
    static readonly SolidColorBrush[] Palette = Brushes();

    [Fact]
    public void Four_ways_to_put_the_rectangles_on_screen() => Sta(() =>
    {
        foreach (var count in Counts)
        {
            var boxes = Laid(count);
            var drawable = boxes.Where(b => b.W >= 2 && b.H >= 2).ToArray();

            Vector("one_visual", count, boxes.Length, () => OneVisual(boxes), Repeats(count));
            Vector("one_visual_culled", count, drawable.Length, () => OneVisual(drawable), Repeats(count));

            // One visual each is the shape people reach for because it gives per-rectangle hit
            // testing for free. One repeat at the top count: if it needs more than that to be
            // measured, it has already answered the question.
            Vector("visual_per_box", count, boxes.Length, () => VisualPerBox(boxes),
                count >= 100_000 ? 1 : Repeats(count));

            Pixels(count, boxes);
        }
    });

    /// <summary>
    /// Build the visual, then rasterise it, and say how much memory the built visual holds. The
    /// two halves are reported apart because they are paid at different times: building happens
    /// when the folder or the window changes, rasterising happens every frame.
    /// </summary>
    void Vector(string name, int count, int drawn, Func<Visual> build, int repeats)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var before = GC.GetTotalMemory(true);

        var built = Time(() =>
        {
            var visual = build();
            GC.KeepAlive(visual);
        }, repeats);

        var kept = build();
        var held = GC.GetTotalMemory(true) - before;

        var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        var raster = Time(() =>
        {
            target.Clear();
            target.Render(kept);
        }, repeats);

        output.WriteLine(
            $"draw\t{name}\tn={count}\tdrawn={drawn}\tbuild_ms={built:F2}\traster_ms={raster:F2}\t" +
            $"held_bytes={held}\tper_item_bytes={(drawn > 0 ? (double)held / drawn : 0):F0}");

        GC.KeepAlive(kept);
    }

    /// <summary>
    /// The one whose cost is bounded by the window rather than by the folder: every rectangle is
    /// written into a pixel buffer, and the buffer is the same size whether the folder holds a
    /// thousand files or a million.
    /// </summary>
    void Pixels(int count, Box[] boxes)
    {
        var repeats = Repeats(count);
        var pixels = new uint[Width * Height];
        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);

        var fill = Time(() =>
        {
            for (var i = 0; i < boxes.Length; i++) Fill(pixels, boxes[i], Raw[i & 15]);
        }, repeats);

        var blit = Time(
            () => bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0),
            repeats);

        output.WriteLine(
            $"draw\twriteable_bitmap\tn={count}\tdrawn={boxes.Length}\tbuild_ms={fill:F2}\t" +
            $"raster_ms={blit:F2}\theld_bytes={pixels.Length * 4}\tper_item_bytes=0");
    }

    static Visual OneVisual(Box[] boxes)
    {
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            for (var i = 0; i < boxes.Length; i++)
            {
                var box = boxes[i];
                dc.DrawRectangle(Palette[i & 15], null, new Rect(box.X, box.Y, box.W, box.H));
            }
        }

        return visual;
    }

    static Visual VisualPerBox(Box[] boxes)
    {
        var host = new DrawingVisual();

        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            var child = new DrawingVisual();
            using (var dc = child.RenderOpen())
            {
                dc.DrawRectangle(Palette[i & 15], null, new Rect(box.X, box.Y, box.W, box.H));
            }
            host.Children.Add(child);
        }

        return host;
    }

    /// <summary>
    /// A rectangle smaller than a pixel still has to leave a mark — rounding it away is how a
    /// treemap ends up with holes in it. Rounded out rather than down, so every box paints at
    /// least one pixel, which is also why the pixel count written exceeds the canvas.
    /// </summary>
    static void Fill(uint[] pixels, Box box, uint colour)
    {
        var x0 = Math.Clamp((int)box.X, 0, Width - 1);
        var y0 = Math.Clamp((int)box.Y, 0, Height - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(box.X + box.W), x0 + 1, Width);
        var y1 = Math.Clamp((int)Math.Ceiling(box.Y + box.H), y0 + 1, Height);

        for (var y = y0; y < y1; y++)
        {
            var row = y * Width;
            for (var x = x0; x < x1; x++) pixels[row + x] = colour;
        }
    }

    static Box[] Laid(int count)
    {
        var sizes = FakeSizes.Lognormal(count, seed: 12345);
        var boxes = new Box[count];
        TreemapLayout.Squarified(sizes, Canvas, boxes);
        return boxes;
    }

    static int Repeats(int count) => count >= 100_000 ? 3 : count >= 10_000 ? 7 : 15;

    static double Time(Action work, int repeats)
    {
        work();

        var times = new double[repeats];
        for (var r = 0; r < repeats; r++)
        {
            var clock = Stopwatch.GetTimestamp();
            work();
            times[r] = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        }

        Array.Sort(times);
        return times[repeats / 2];
    }

    /// <summary>WPF visuals want a single-threaded apartment, and xunit does not give one.</summary>
    static void Sta(Action work)
    {
        ExceptionDispatchInfo? failed = null;

        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { failed = ExceptionDispatchInfo.Capture(e); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failed?.Throw();
    }

    static SolidColorBrush[] Brushes()
    {
        var brushes = new SolidColorBrush[Raw.Length];

        for (var i = 0; i < Raw.Length; i++)
        {
            var value = Raw[i];
            brushes[i] = new SolidColorBrush(Color.FromArgb(
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
            brushes[i].Freeze();
        }

        return brushes;
    }
}
