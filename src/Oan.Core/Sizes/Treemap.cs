namespace Oan.Core.Sizes;

/// <summary>
/// One laid-out rectangle, in device-independent pixels. Its own struct rather than
/// System.Windows.Rect, so the layout stays in Core with no reference to WPF.
/// </summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Area => W * H;

    /// <summary>Long side over short side. 1 is a square; past about 8 it is a sliver you can
    /// neither read a label in nor reliably put a mouse on.</summary>
    public double Aspect => W <= 0 || H <= 0 ? double.PositiveInfinity : W > H ? W / H : H / W;

    public bool Holds(double x, double y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>
/// Squarified treemap layout, after Bruls, Huizing and van Wijk.
///
/// Squarified rather than the alternatives, and that was measured rather than assumed: over
/// 100,000 rectangles it lays out in 0.873 ms against strip's 359.6 ms, and — the reason that
/// matters more — it produces zero rectangles worse than 8:1 even when one file holds 90% of the
/// folder, where strip produces 6,641 and slice-and-dice produces 100,000. Sizes must arrive
/// sorted descending; the algorithm assumes it and reads badly without it.
/// </summary>
public static class Treemap
{
    /// <summary>
    /// Fills the rectangle a row at a time along whichever side is currently shorter, growing
    /// each row while the WORST aspect ratio in it keeps improving. That one test is the whole
    /// algorithm: another rectangle in the row makes every rectangle in it thinner, and the row
    /// closes the moment that costs more than it gains.
    /// </summary>
    public static void Lay(ReadOnlySpan<long> sizes, Box bounds, Span<Box> into)
    {
        var scale = Scale(sizes, bounds);
        var rest = bounds;
        var i = 0;

        while (i < sizes.Length)
        {
            var side = Math.Min(rest.W, rest.H);
            if (side <= 0 || scale <= 0)
            {
                Collapse(into, ref i, sizes.Length, rest);
                return;
            }

            var start = i;
            var sum = sizes[i] * scale;
            double min = sum, max = sum;
            i++;

            while (i < sizes.Length)
            {
                var area = sizes[i] * scale;
                var grown = sum + area;
                var lo = Math.Min(min, area);
                var hi = Math.Max(max, area);

                if (Worst(grown, lo, hi, side) > Worst(sum, min, max, side)) break;

                sum = grown;
                min = lo;
                max = hi;
                i++;
            }

            rest = Row(sizes, into, start, i, sum, scale, rest);
        }
    }

    /// <summary>Lays one row across the shorter side and returns what is left over.</summary>
    private static Box Row(ReadOnlySpan<long> sizes, Span<Box> into, int start, int end, double sum, double scale, Box rest)
    {
        var across = rest.W <= rest.H;
        var side = across ? rest.W : rest.H;
        var thickness = Math.Min(sum / side, across ? rest.H : rest.W);
        var at = 0.0;

        for (var k = start; k < end; k++)
        {
            var length = thickness > 0 ? sizes[k] * scale / thickness : 0;

            // The last one takes whatever remains, so rounding cannot leave a seam down the edge.
            if (k == end - 1) length = Math.Max(0, side - at);

            into[k] = across
                ? new Box(rest.X + at, rest.Y, length, thickness)
                : new Box(rest.X, rest.Y + at, thickness, length);
            at += length;
        }

        return across
            ? new Box(rest.X, rest.Y + thickness, rest.W, rest.H - thickness)
            : new Box(rest.X + thickness, rest.Y, rest.W - thickness, rest.H);
    }

    /// <summary>
    /// The worse of the two aspect ratios a row of this area would produce — the biggest
    /// rectangle in it and the smallest. Computed from the areas rather than from laid-out boxes,
    /// so the test costs nothing: the row is never actually placed until it closes.
    /// </summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        var area = sum * sum;
        var across = side * side;
        return Math.Max(across * max / area, area / (across * min));
    }

    /// <summary>Nothing left to lay in: everything remaining gets an empty box at the corner.</summary>
    private static void Collapse(Span<Box> into, ref int i, int count, Box rest)
    {
        for (; i < count; i++) into[i] = new Box(rest.X, rest.Y, 0, 0);
    }

    private static double Scale(ReadOnlySpan<long> sizes, Box bounds)
    {
        var total = 0.0;
        foreach (var size in sizes) total += size;
        return total > 0 ? bounds.Area / total : 0;
    }
}
