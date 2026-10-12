using System.Windows;
using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>How much of a tab still fits at the width it was given.</summary>
public enum TabFit
{
    Full,
    NoClose,
    IconOnly,
}

/// <summary>
/// The tab strip's layout, as a browser does it: one width for all, narrowing as they pile up, the selected
/// one keeping room to be read and closed. The room comes from <see cref="RoomProperty"/>, not the measure
/// constraint, because a scroll viewer offers infinite width.
/// </summary>
internal sealed class TabBar : Panel
{
    internal const double Widest = 170;
    internal const double Narrowest = 52;
    internal const double ChosenLeast = 120;
    internal const double KeepsClose = 90;
    internal const double KeepsTitle = 70;

    /// <summary>Set on the strip; inherited down to this panel. NaN until the strip is sized.</summary>
    public static readonly DependencyProperty RoomProperty = DependencyProperty.RegisterAttached(
        "Room", typeof(double), typeof(TabBar),
        new FrameworkPropertyMetadata(double.NaN,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Set by the item container style from the tab's own IsActive.</summary>
    public static readonly DependencyProperty ChosenProperty = DependencyProperty.RegisterAttached(
        "Chosen", typeof(bool), typeof(TabBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>Written by this panel, read by the tab template.</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.RegisterAttached(
        "Fit", typeof(TabFit), typeof(TabBar), new FrameworkPropertyMetadata(TabFit.Full));

    public static double GetRoom(DependencyObject o) => (double)o.GetValue(RoomProperty);
    public static void SetRoom(DependencyObject o, double v) => o.SetValue(RoomProperty, v);
    public static bool GetChosen(DependencyObject o) => (bool)o.GetValue(ChosenProperty);
    public static void SetChosen(DependencyObject o, bool v) => o.SetValue(ChosenProperty, v);
    public static TabFit GetFit(DependencyObject o) => (TabFit)o.GetValue(FitProperty);
    public static void SetFit(DependencyObject o, TabFit v) => o.SetValue(FitProperty, v);

    /// <summary>What each tab gets. Chosen is the selected tab's width, Other everyone else's.</summary>
    internal static (double Chosen, double Other) Share(double room, int count, bool anyChosen)
    {
        if (count <= 0) return (0, 0);
        if (double.IsNaN(room) || double.IsInfinity(room) || room <= 0) return (Widest, Widest);

        var even = room / count;
        if (even >= Widest) return (Widest, Widest);
        if (even >= ChosenLeast || count == 1 || !anyChosen)
        {
            var same = Math.Max(even, Narrowest);
            return (same, same);
        }

        return (ChosenLeast, Math.Max(Narrowest, (room - ChosenLeast) / (count - 1)));
    }

    internal static TabFit FitAt(double width) =>
        width < KeepsTitle ? TabFit.IconOnly : width < KeepsClose ? TabFit.NoClose : TabFit.Full;

    protected override Size MeasureOverride(Size available)
    {
        var (chosen, other) = Shares();
        var total = 0.0;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            var width = GetChosen(child) ? chosen : other;
            SetFit(child, GetChosen(child) ? TabFit.Full : FitAt(width));

            child.Measure(new Size(width, available.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            total += width;
        }
        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (chosen, other) = Shares();
        var x = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            var width = GetChosen(child) ? chosen : other;
            child.Arrange(new Rect(x, 0, width, final.Height));
            x += width;
        }
        return final;
    }

    private (double Chosen, double Other) Shares()
    {
        var anyChosen = false;
        foreach (UIElement child in InternalChildren)
        {
            if (GetChosen(child)) anyChosen = true;
        }
        return Share(GetRoom(this), InternalChildren.Count, anyChosen);
    }
}
