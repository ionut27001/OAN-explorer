using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What a treemap costs to lay out, and what it is worth looking at afterwards.
///
/// Layout only — where each rectangle goes, and whether the picture is worth looking at once they
/// are there. What it costs to put them on screen is a separate question and a separate file,
/// TreemapDrawBenchmark, because the two have different answers and different limits.
///
/// Kept out of the normal pass by Category=Benchmark, which tests/Oan.Tests/Oan.Tests.csproj
/// excludes. Run it on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~Treemap" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class TreemapLayoutBenchmark(ITestOutputHelper output)
{
    /// <summary>1k is a folder, 10k is a project, 100k is a drive. 50k, the number asked about,
    /// sits between the last two and can be read off the line they make.</summary>
    static readonly int[] Counts = [1_000, 10_000, 100_000];

    /// <summary>A 1400x900 window with the chrome taken off. The canvas matters: it is the ratio
    /// of area to item count that decides how many rectangles come out sub-pixel.</summary>
    static readonly Box Canvas = new(0, 0, 1360, 820);

    [Fact]
    public void One_level_of_a_real_looking_folder()
    {
        foreach (var count in Counts)
        {
            var sizes = FakeSizes.Lognormal(count, seed: 12345);
            Run("real", count, sizes);
        }
    }

    /// <summary>
    /// The case the layout is chosen for: one file is 90% of the folder and everything else has
    /// to share the remaining tenth. Squarified is supposed to keep the rest readable; this is
    /// where that claim is either true or it is not.
    /// </summary>
    [Fact]
    public void When_one_file_is_nine_tenths_of_the_folder()
    {
        foreach (var count in Counts)
        {
            var sizes = FakeSizes.Lognormal(count, seed: 12345);
            var rest = sizes.Sum();

            // One item at nine times everything else put together, so it holds 90% of the total.
            var skewed = new double[count];
            skewed[0] = rest * 9;
            Array.Copy(sizes, 0, skewed, 1, count - 1);

            Run("skewed", count, skewed);
        }
    }

    /// <summary>
    /// Sorting is not free and is not optional — all three layouts want the sizes descending, and
    /// a treemap that re-sorts on every resize pays this on every mouse move. Measured apart from
    /// the layout so the two can be budgeted separately: sort once when the folder is read, lay
    /// out again on every resize.
    /// </summary>
    [Fact]
    public void Sorting_is_a_separate_bill_from_laying_out()
    {
        foreach (var count in Counts)
        {
            // Unsorted, because that is how a folder arrives: the directory hands back name
            // order, which is random with respect to size.
            var source = FakeSizes.Lognormal(count, seed: 999, sorted: false);
            var scratch = new double[count];
            var boxes = new Box[count];
            var ordered = FakeSizes.Lognormal(count, seed: 999);

            var sort = Time(() =>
            {
                Array.Copy(source, scratch, count);
                Array.Sort(scratch);
                Array.Reverse(scratch);
            }, Repeats(count));

            var layout = Time(() => TreemapLayout.Squarified(ordered, Canvas, boxes), Repeats(count));

            output.WriteLine(
                $"split\tn={count}\tsort_ms={sort.Ms:F3}\tsquarified_ms={layout.Ms:F3}\t" +
                $"sort_share={sort.Ms / (sort.Ms + layout.Ms):P0}");
        }
    }

    void Run(string shape, int count, double[] sizes)
    {
        var boxes = new Box[count];
        var repeats = Repeats(count);

        Report(shape, "squarified", count, sizes, boxes,
            Time(() => TreemapLayout.Squarified(sizes, Canvas, boxes), repeats));

        Report(shape, "strip", count, sizes, boxes,
            Time(() => TreemapLayout.Strip(sizes, Canvas, boxes), repeats));

        Report(shape, "slice", count, sizes, boxes,
            Time(() => TreemapLayout.SliceAndDice(sizes, Canvas, boxes), repeats));
    }

    /// <summary>
    /// One line per variant, and the line carries the check as well as the clock: every layout's
    /// boxes must cover the canvas and be proportional to the sizes they were given. A fast
    /// layout that lost area is not a faster layout, it is a broken one — and that is exactly the
    /// failure that hides behind a pretty picture.
    /// </summary>
    void Report(string shape, string name, int count, double[] sizes, Box[] boxes, (double Ms, long Alloc) run)
    {
        var quality = TreemapLayout.Grade(sizes, Canvas, boxes);
        var covered = boxes.Sum(b => b.Area) / Canvas.Area;

        output.WriteLine(
            $"layout\t{shape}\t{name}\tn={count}\tms={run.Ms:F3}\tper_item_ns={run.Ms * 1_000_000 / count:F0}\t" +
            $"alloc_bytes={run.Alloc}\tmedian_aspect={quality.Median:F2}\tp95_aspect={quality.P95:F2}\t" +
            $"over8={quality.Over8}\tover8_pct={(double)quality.Over8 / count:P1}\t" +
            $"slivers={quality.Slivers}\tslivers_pct={(double)quality.Slivers / count:P1}\t" +
            $"drawable={quality.Drawable}\tdrawable_pct={(double)quality.Drawable / count:P1}\t" +
            $"covered={covered:F6}\tarea_drift={quality.AreaDrift:E2}");

        Assert.InRange(covered, 0.999999, 1.000001);
        Assert.True(quality.AreaDrift < 1e-9, $"{name} lost area: drift {quality.AreaDrift:E2}");
        Assert.All(boxes, b => Assert.True(
            b.X >= Canvas.X - 1e-6 && b.Y >= Canvas.Y - 1e-6 &&
            b.X + b.W <= Canvas.X + Canvas.W + 1e-6 && b.Y + b.H <= Canvas.Y + Canvas.H + 1e-6,
            $"{name} put a box outside the canvas: {b}"));
    }

    /// <summary>
    /// Enough repeats to get past the noise, few enough that the whole file stays under a minute.
    /// The median of the runs is reported rather than the mean: one scheduling hiccup should not
    /// decide which algorithm looks faster.
    /// </summary>
    static int Repeats(int count) => count >= 100_000 ? 21 : count >= 10_000 ? 51 : 101;

    static (double Ms, long Alloc) Time(Action work, int repeats)
    {
        // Warm twice: once for the JIT, once for the arrays. A first run that includes tiering
        // is not the run a resize will do.
        work();
        work();

        var times = new double[repeats];
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var r = 0; r < repeats; r++)
        {
            var clock = Stopwatch.GetTimestamp();
            work();
            times[r] = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / repeats;
        Array.Sort(times);
        return (times[repeats / 2], allocated);
    }
}
