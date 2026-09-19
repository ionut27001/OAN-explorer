namespace Oan.Tests;

/// <summary>
/// One laid-out rectangle, in device-independent pixels. A plain struct rather than
/// System.Windows.Rect, so the benchmark times the layout and not WPF.
/// </summary>
readonly record struct Box(double X, double Y, double W, double H)
{
    public double Area => W * H;

    /// <summary>Long side over short side. 1 is a square; past about 8 it is a sliver you can
    /// neither read a label in nor reliably put a mouse on.</summary>
    public double Aspect => W <= 0 || H <= 0 ? double.PositiveInfinity : W > H ? W / H : H / W;
}

/// <summary>
/// Sizes to lay out. File sizes are not uniform, and a benchmark over uniform sizes measures the
/// wrong thing: with every rectangle the same, every layout looks good. Lognormal with a median
/// around 4 KB and a long tail is the shape a real folder has — a handful of big files carrying
/// most of the bytes, thousands of small ones carrying the count.
/// </summary>
static class FakeSizes
{
    public static double[] Lognormal(int count, int seed, bool sorted = true)
    {
        var random = new Random(seed);
        var sizes = new double[count];

        for (var i = 0; i < count; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

            // Floored at one byte, because area-proportional has no answer for zero.
            sizes[i] = Math.Max(1, Math.Exp(8.3 + 2.4 * normal));
        }

        if (!sorted) return sizes;

        Array.Sort(sizes);
        Array.Reverse(sizes);
        return sizes;
    }
}

/// <summary>
/// What a layout is worth to look at. Aspect ratios are long side over short side.
/// <paramref name="Drawable"/> is the count that is at least 2x2 — the only ones a person can see
/// or put a mouse on, and so the only ones worth spending a draw call on.
/// </summary>
readonly record struct Quality(double Median, double P95, int Over8, int Slivers, int Drawable, double AreaDrift);

/// <summary>
/// The three treemap layouts, written out so their cost can be measured rather than argued about.
///
/// All three take one level's sizes and fill a span of boxes in the same order, so a caller can
/// swap them and nothing else changes. All three expect sizes sorted descending — slice-and-dice
/// does not need it, but WizTree sorts anyway and an unsorted slice is unreadable.
///
/// Sizes must be positive. Area-proportional has no answer for a zero-byte file, and inventing
/// one inside the layout would hide it: the caller decides whether those get a minimum sliver or
/// a count somewhere else.
/// </summary>
static class TreemapLayout
{
    /// <summary>
    /// Proportional cuts along one axis. O(n) and exact, and in a real treemap the axis flips at
    /// every level, which is what gives it the one property the other two lack: a file that grows
    /// moves its own edge and nothing else's, so the picture is stable between refreshes.
    /// Flat, as measured here, it is a barcode.
    /// </summary>
    public static void SliceAndDice(ReadOnlySpan<double> sizes, Box bounds, Span<Box> into, bool horizontal = true)
    {
        var scale = Scale(sizes, bounds);
        var thickness = horizontal ? bounds.H : bounds.W;
        var end = horizontal ? bounds.X + bounds.W : bounds.Y + bounds.H;
        var at = horizontal ? bounds.X : bounds.Y;

        for (var i = 0; i < sizes.Length; i++)
        {
            var length = thickness <= 0 ? 0 : sizes[i] * scale / thickness;

            // The last one is snapped to the edge: a thousand roundings otherwise leave a gap or
            // an overhang, and a gap in a treemap is read as free space.
            if (i == sizes.Length - 1) length = Math.Max(0, end - at);

            into[i] = horizontal
                ? new Box(at, bounds.Y, length, bounds.H)
                : new Box(bounds.X, at, bounds.W, length);
            at += length;
        }
    }

    /// <summary>
    /// Bruls, Huizing and van Wijk. Fills the rectangle a row at a time along whichever side is
    /// currently shorter, growing each row while the WORST aspect ratio in it keeps improving.
    /// That one test is the whole algorithm: another rectangle in the row makes every rectangle
    /// in it thinner, and the row closes the moment that costs more than it gains.
    /// </summary>
    public static void Squarified(ReadOnlySpan<double> sizes, Box bounds, Span<Box> into)
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

    /// <summary>
    /// Strips of fixed width, closed when the AVERAGE aspect ratio in the strip stops improving.
    /// Squarified asks about the worst rectangle in a row and reorders freely to get it; strip
    /// asks about the mean and keeps the order it was given, so the result still reads left to
    /// right, top to bottom. Worse rectangles, a picture you can scan.
    /// </summary>
    public static void Strip(ReadOnlySpan<double> sizes, Box bounds, Span<Box> into)
    {
        var scale = Scale(sizes, bounds);
        var bottom = bounds.Y + bounds.H;
        var y = bounds.Y;
        var i = 0;

        while (i < sizes.Length)
        {
            var start = i;
            var best = double.MaxValue;
            double sum = 0;

            while (i < sizes.Length)
            {
                var grown = sum + sizes[i] * scale;
                var mean = MeanAspect(sizes, start, i + 1, grown, scale, bounds.W);

                if (mean > best) break;

                best = mean;
                sum = grown;
                i++;
            }

            var height = bounds.W > 0 ? sum / bounds.W : 0;
            if (y + height > bottom) height = Math.Max(0, bottom - y);

            Place(sizes, into, start, i, height, scale, bounds, y);
            y += height;
        }
    }

    /// <summary>
    /// How far off proportional the boxes came out, and how many of them are unusable. These are
    /// the numbers a picture is chosen on: a median aspect ratio and a count of slivers say more
    /// about a treemap than its running time does.
    /// </summary>
    public static Quality Grade(ReadOnlySpan<double> sizes, Box bounds, ReadOnlySpan<Box> boxes)
    {
        var scale = Scale(sizes, bounds);
        var aspects = new double[boxes.Length];
        var slivers = 0;
        var drawable = 0;
        var over8 = 0;
        var drift = 0.0;

        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            aspects[i] = box.Aspect;

            if (box.Aspect > 8) over8++;
            if (box.W < 1 || box.H < 1) slivers++;
            if (box.W >= 2 && box.H >= 2) drawable++;

            var wanted = sizes[i] * scale;
            drift = Math.Max(drift, Math.Abs(box.Area - wanted) / bounds.Area);
        }

        Array.Sort(aspects);
        return new Quality(At(aspects, 0.50), At(aspects, 0.95), over8, slivers, drawable, drift);
    }

    static double At(double[] sorted, double fraction) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)(sorted.Length * fraction), 0, sorted.Length - 1)];

    /// <summary>Lays one squarified row across the shorter side and returns what is left.</summary>
    static Box Row(ReadOnlySpan<double> sizes, Span<Box> into, int start, int end, double sum, double scale, Box rest)
    {
        var across = rest.W <= rest.H;
        var side = across ? rest.W : rest.H;
        var thickness = Math.Min(sum / side, across ? rest.H : rest.W);
        var at = 0.0;

        for (var k = start; k < end; k++)
        {
            var length = thickness > 0 ? sizes[k] * scale / thickness : 0;
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

    static void Place(ReadOnlySpan<double> sizes, Span<Box> into, int start, int end, double height, double scale, Box bounds, double y)
    {
        var x = bounds.X;
        var right = bounds.X + bounds.W;

        for (var k = start; k < end; k++)
        {
            var width = height > 0 ? sizes[k] * scale / height : 0;
            if (k == end - 1) width = Math.Max(0, right - x);

            into[k] = new Box(x, y, width, height);
            x += width;
        }
    }

    /// <summary>
    /// The worse of the two aspect ratios a row of this area would produce — the biggest
    /// rectangle in it and the smallest. Computed from the areas rather than from laid-out boxes,
    /// so the test costs nothing: the row is never actually placed until it closes.
    /// </summary>
    static double Worst(double sum, double min, double max, double side)
    {
        var area = sum * sum;
        var across = side * side;
        return Math.Max(across * max / area, area / (across * min));
    }

    /// <summary>
    /// O(k) per candidate, so O(k²) for a strip of k. Strips are tens of items wide, not
    /// thousands — but this is why strip loses to squarified on the clock as well as on the
    /// picture.
    /// </summary>
    static double MeanAspect(ReadOnlySpan<double> sizes, int start, int end, double sum, double scale, double width)
    {
        if (width <= 0 || sum <= 0) return double.MaxValue;

        var height = sum / width;
        var total = 0.0;

        for (var k = start; k < end; k++)
        {
            var w = sizes[k] * scale / height;
            total += w > height ? w / height : height / w;
        }

        return total / (end - start);
    }

    static void Collapse(Span<Box> into, ref int i, int count, Box rest)
    {
        for (; i < count; i++) into[i] = new Box(rest.X, rest.Y, 0, 0);
    }

    static double Scale(ReadOnlySpan<double> sizes, Box bounds)
    {
        var total = 0.0;
        foreach (var size in sizes) total += size;
        return total > 0 ? bounds.Area / total : 0;
    }
}
