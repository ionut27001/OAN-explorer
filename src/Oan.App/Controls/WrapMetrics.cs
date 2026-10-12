namespace Oan.App.Controls;

/// <summary>The arithmetic of a uniform wrapping grid, kept apart from the panel so it can be tested without a window.</summary>
internal static class WrapMetrics
{
    /// <summary>At least one, so a window narrower than a tile still draws it and never divides by zero.</summary>
    public static int Columns(double viewportWidth, double itemWidth) =>
        itemWidth <= 0 ? 1 : Math.Max(1, (int)(viewportWidth / itemWidth));

    public static int Rows(int count, int columns) =>
        count <= 0 || columns <= 0 ? 0 : (count + columns - 1) / columns;

    /// <summary>
    /// The first and last item index the viewport touches. An empty grid comes back as Last below First;
    /// everything else is clamped to a row that exists, including an infinite viewport.
    /// </summary>
    public static (int First, int Last) VisibleRange(
        int count, int columns, double offset, double viewportHeight, double itemHeight)
    {
        if (count <= 0 || columns <= 0 || itemHeight <= 0) return (0, -1);

        var rows = Rows(count, columns);
        var firstRow = RowAt(offset / itemHeight, rows);
        var lastRow = Math.Max(firstRow, RowAt(Math.Ceiling((offset + viewportHeight) / itemHeight) - 1, rows));

        return (firstRow * columns, Math.Min(count - 1, (lastRow + 1) * columns - 1));
    }

    private static int RowAt(double row, int rows) => (int)Math.Clamp(row, 0, rows - 1);
}
