using System.Windows;
using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>
/// Lays tabs out the way a browser does: all the same width, wide while there are few and
/// narrower as they pile up, down to a floor. A StackPanel let every tab keep its width, so past
/// five of them the strip ran off its own end, under the window buttons, taking the + with it.
/// </summary>
internal sealed class TabBar : Panel
{
    internal const double Widest = 170;
    internal const double Narrowest = 80;

    /// <summary>
    /// Infinity means nobody has said how much room there is. The strip's width is put on the
    /// items control by the code behind, so this is only the pass before that lands.
    /// </summary>
    internal static double Each(double room, int count) =>
        count <= 0 ? 0
        : double.IsInfinity(room) || double.IsNaN(room) ? Widest
        : Math.Clamp(room / count, Narrowest, Widest);

    protected override Size MeasureOverride(Size available)
    {
        var each = Each(available.Width, InternalChildren.Count);
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(each, available.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(each * InternalChildren.Count, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var each = Each(final.Width, InternalChildren.Count);
        var x = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, each, final.Height));
            x += each;
        }
        return final;
    }
}
