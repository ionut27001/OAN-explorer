using Oan.Core.Sizes;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The layout the size map is drawn from. Areas have to be proportional or the picture lies, and
/// the rectangles have to stay near square or they cannot be seen, read or clicked.
/// </summary>
public class TreemapTests
{
    [Fact]
    public void Every_area_is_its_share_of_the_whole()
    {
        long[] sizes = [50, 30, 15, 5];
        var bounds = new Box(0, 0, 400, 300);
        var boxes = new Box[sizes.Length];

        Treemap.Lay(sizes, bounds, boxes);

        for (var i = 0; i < sizes.Length; i++)
        {
            var wanted = bounds.Area * sizes[i] / 100.0;
            Assert.True(Math.Abs(boxes[i].Area - wanted) < bounds.Area * 0.01,
                $"{sizes[i]} took {boxes[i].Area:N0} of {bounds.Area:N0}, wanted {wanted:N0}");
        }
    }

    [Fact]
    public void The_rectangles_fill_the_box_and_do_not_overlap()
    {
        var sizes = Sizes(200);
        var bounds = new Box(0, 0, 900, 600);
        var boxes = new Box[sizes.Length];

        Treemap.Lay(sizes, bounds, boxes);

        Assert.All(boxes, b =>
        {
            Assert.True(b.X >= -0.001 && b.Y >= -0.001, "started outside");
            Assert.True(b.X + b.W <= bounds.W + 0.001 && b.Y + b.H <= bounds.H + 0.001, "ran past the edge");
        });

        var covered = boxes.Sum(b => b.Area);
        Assert.True(Math.Abs(covered - bounds.Area) < bounds.Area * 0.001, $"covered {covered:N0} of {bounds.Area:N0}");
    }

    /// <summary>
    /// The case that decides which algorithm this is. One file holding almost everything makes
    /// slice-and-dice draw the rest as a barcode; squarified keeps the leftovers square.
    /// </summary>
    [Fact]
    public void One_huge_file_does_not_turn_the_rest_into_slivers()
    {
        var sizes = new long[500];
        sizes[0] = 900_000;
        for (var i = 1; i < sizes.Length; i++) sizes[i] = 200;

        var bounds = new Box(0, 0, 1000, 700);
        var boxes = new Box[sizes.Length];

        Treemap.Lay(sizes, bounds, boxes);

        var drawable = boxes.Where(b => b.W >= 2 && b.H >= 2).ToArray();
        Assert.NotEmpty(drawable);
        Assert.All(drawable, b => Assert.True(b.Aspect <= 8, $"a {b.W:N1}x{b.H:N1} sliver at {b.Aspect:N1}:1"));
    }

    [Fact]
    public void A_box_with_no_room_in_it_places_everything_empty_rather_than_throwing()
    {
        var sizes = Sizes(50);
        var boxes = new Box[sizes.Length];

        Treemap.Lay(sizes, new Box(0, 0, 0, 0), boxes);

        Assert.All(boxes, b => Assert.Equal(0, b.Area));
    }

    [Fact]
    public void Nothing_to_lay_out_is_not_a_crash()
    {
        Treemap.Lay([], new Box(0, 0, 100, 100), []);
        Treemap.Lay([0, 0], new Box(0, 0, 100, 100), new Box[2]);
    }

    [Fact]
    public void A_point_lands_in_exactly_one_rectangle()
    {
        var sizes = Sizes(120);
        var bounds = new Box(0, 0, 800, 500);
        var boxes = new Box[sizes.Length];

        Treemap.Lay(sizes, bounds, boxes);

        for (var x = 5; x < 800; x += 97)
        {
            for (var y = 5; y < 500; y += 83)
            {
                Assert.Equal(1, boxes.Count(b => b.Holds(x, y)));
            }
        }
    }

    /// <summary>Descending, which is what the algorithm is given and what makes it squarify.</summary>
    private static long[] Sizes(int count)
    {
        var sizes = new long[count];
        for (var i = 0; i < count; i++) sizes[i] = (count - i) * 7L + 3;
        return sizes;
    }
}
