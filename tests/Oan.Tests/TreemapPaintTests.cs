using System.Windows;
using Oan.App.Controls;
using Oan.Core.Sizes;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The map used to lay out and repaint every rectangle on every mouse move — on a folder tree
/// that is tens of thousands of them, and the pointer dragged. The tiles are drawn once and kept,
/// so a hover redraws an outline over a picture that already exists.
/// </summary>
[Collection(DiskBound.Name)]
public class TreemapPaintTests
{
    [Fact]
    public void Redrawing_does_not_lay_the_whole_map_out_again()
    {
        var (afterFirst, afterMany, afterResize) = Sta(() =>
        {
            var map = Shown(SizeTree.Read(Repo.Root));
            var first = map.Paints;

            for (var i = 0; i < 10; i++) Render(map, 900, 400);
            var many = map.Paints;

            // A different size is a different picture, and that one does have to be laid out.
            map.Width = 640;
            Render(map, 640, 400);

            return (first, many, map.Paints);
        });

        // The count after settling, not from zero: laying out the element once gives it a size
        // it did not have, and that is a second picture of a different shape, fairly.
        Assert.Equal(afterFirst, afterMany);
        Assert.Equal(afterFirst + 1, afterResize);
    }

    [Fact]
    public void New_contents_are_laid_out_again()
    {
        var (before, after) = Sta(() =>
        {
            var map = Shown(SizeTree.Read(Repo.Root));
            var settled = map.Paints;

            map.Tree = SizeTree.Read(System.IO.Path.Combine(Repo.Root, "src"));
            Render(map, 900, 400);
            return (settled, map.Paints);
        });

        Assert.Equal(before + 1, after);
    }

    private static TreemapView Shown(SizeTree? tree)
    {
        var map = new TreemapView { Width = 900, Height = 400, Tree = tree };

        // Twice: the first render happens before layout has given the element a size, so it
        // paints an empty box and the second paints the real one. Both are honest work.
        Render(map, 900, 400);
        Render(map, 900, 400);
        return map;
    }

    /// <summary>
    /// Forces a real render. Without a window on screen nothing calls OnRender on its own, so
    /// counting paints after an InvalidateVisual alone counts nothing at all.
    /// </summary>
    private static void Render(TreemapView map, int width, int height)
    {
        map.InvalidateVisual();
        map.Measure(new Size(width, height));
        map.Arrange(new Rect(0, 0, width, height));
        map.UpdateLayout();

        new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32).Render(map);
    }

    private static T Sta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException("paint failed", failure);
        return result;
    }
}
