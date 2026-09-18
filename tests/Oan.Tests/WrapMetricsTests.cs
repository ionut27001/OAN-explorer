using Oan.App.Controls;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The index range the icon grid realizes. Everything here is one tile 100 wide and 100 tall, so
/// a wrong answer reads as a row count rather than as arithmetic.
/// </summary>
public class WrapMetricsTests
{
    [Fact]
    public void Columns_is_how_many_whole_tiles_fit()
    {
        Assert.Equal(4, WrapMetrics.Columns(450, 100));
        Assert.Equal(4, WrapMetrics.Columns(400, 100));
    }

    [Fact]
    public void A_viewport_narrower_than_one_tile_still_has_a_column()
    {
        Assert.Equal(1, WrapMetrics.Columns(60, 100));
        Assert.Equal(1, WrapMetrics.Columns(0, 100));
    }

    [Fact]
    public void A_tile_with_no_width_does_not_divide_the_viewport_by_zero()
    {
        Assert.Equal(1, WrapMetrics.Columns(400, 0));
    }

    [Fact]
    public void The_last_partial_row_is_still_a_row()
    {
        Assert.Equal(3, WrapMetrics.Rows(10, 4));
        Assert.Equal(2, WrapMetrics.Rows(8, 4));
        Assert.Equal(0, WrapMetrics.Rows(0, 4));
    }

    [Fact]
    public void An_empty_folder_is_an_empty_range_and_not_item_zero()
    {
        var (first, last) = WrapMetrics.VisibleRange(0, 4, 0, 500, 100);

        Assert.True(last < first);
    }

    [Fact]
    public void A_viewport_of_whole_rows_realizes_exactly_those_rows()
    {
        var (first, last) = WrapMetrics.VisibleRange(1000, 4, 0, 300, 100);

        Assert.Equal(0, first);
        Assert.Equal(11, last); // three rows of four, and not a fourth row nobody can see
    }

    [Fact]
    public void A_row_scrolled_half_out_of_sight_is_still_realized()
    {
        var (first, last) = WrapMetrics.VisibleRange(1000, 4, 150, 300, 100);

        Assert.Equal(4, first);  // row 1, the one the top edge cuts through
        Assert.Equal(19, last);  // row 4, the one the bottom edge cuts through
    }

    [Fact]
    public void The_range_stops_at_the_last_item()
    {
        var (first, last) = WrapMetrics.VisibleRange(10, 4, 0, 5000, 100);

        Assert.Equal(0, first);
        Assert.Equal(9, last);
    }

    [Fact]
    public void Scrolled_past_the_last_row_still_lands_on_rows_that_exist()
    {
        var (first, last) = WrapMetrics.VisibleRange(10, 4, 4000, 300, 100);

        Assert.Equal(8, first); // the last row of ten items in fours, not an index past the end
        Assert.Equal(9, last);
    }

    [Fact]
    public void An_unbounded_viewport_realizes_everything()
    {
        var (first, last) = WrapMetrics.VisibleRange(10, 4, 0, double.PositiveInfinity, 100);

        Assert.Equal(0, first);
        Assert.Equal(9, last);
    }
}
