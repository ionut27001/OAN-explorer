using System.Windows;
using System.Windows.Controls;
using Oan.App.Controls;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// How the strip divides itself up. Twelve tabs used to come out identical, unreadable and
/// wearing a close button each; a browser gives the selected one room to stay readable and lets
/// the others shed their close button and then their title.
/// </summary>
public class TabBarTests
{
    [Fact]
    public void Few_tabs_do_not_stretch_across_the_window()
    {
        Assert.Equal((TabBar.Widest, TabBar.Widest), TabBar.Share(1600, 1, true));
        Assert.Equal((TabBar.Widest, TabBar.Widest), TabBar.Share(1600, 4, true));
    }

    [Fact]
    public void They_narrow_as_they_pile_up()
    {
        var four = TabBar.Share(900, 4, true).Other;
        var eight = TabBar.Share(900, 8, true).Other;

        Assert.True(eight < four, $"eight got {eight}, four got {four}");
    }

    [Fact]
    public void While_there_is_room_for_everyone_the_selected_tab_is_no_wider()
    {
        var (chosen, other) = TabBar.Share(900, 6, true);

        Assert.Equal(chosen, other);
    }

    [Fact]
    public void Once_it_is_tight_the_selected_tab_keeps_enough_to_be_read()
    {
        var (chosen, other) = TabBar.Share(892, 12, true);

        Assert.Equal(TabBar.ChosenLeast, chosen);
        Assert.True(other < chosen, $"others got {other}");
        Assert.True(chosen + other * 11 <= 892 + 0.5, "the row still fits");
    }

    [Fact]
    public void With_no_selected_tab_nobody_gets_the_extra_room()
    {
        var (chosen, other) = TabBar.Share(892, 12, anyChosen: false);

        Assert.Equal(chosen, other);
    }

    [Fact]
    public void Past_the_floor_they_stop_shrinking_rather_than_becoming_slivers()
    {
        Assert.Equal(TabBar.Narrowest, TabBar.Share(300, 40, true).Other);
        Assert.Equal(TabBar.Narrowest, TabBar.Share(1, 40, false).Other);
    }

    [Fact]
    public void No_room_named_yet_is_not_a_width_of_zero()
    {
        Assert.Equal(TabBar.Widest, TabBar.Share(double.NaN, 3, true).Other);
        Assert.Equal(TabBar.Widest, TabBar.Share(double.PositiveInfinity, 3, true).Other);
        Assert.Equal((0d, 0d), TabBar.Share(900, 0, false));
    }

    [Theory]
    [InlineData(170, TabFit.Full)]
    [InlineData(120, TabFit.Full)]
    [InlineData(89, TabFit.NoClose)]
    [InlineData(70, TabFit.NoClose)]
    [InlineData(69, TabFit.IconOnly)]
    [InlineData(52, TabFit.IconOnly)]
    public void What_a_tab_still_shows_at_a_given_width(double width, TabFit expected) =>
        Assert.Equal(expected, TabBar.FitAt(width));

    [Fact]
    public void The_panel_lays_the_row_out_and_tells_each_tab_what_it_can_show()
    {
        var (widths, fits) = Sta(() =>
        {
            var bar = new TabBar();
            TabBar.SetRoom(bar, 892);

            for (var i = 0; i < 12; i++)
            {
                var child = new Border();
                TabBar.SetChosen(child, i == 3);
                bar.Children.Add(child);
            }

            bar.Measure(new Size(double.PositiveInfinity, 30));
            bar.Arrange(new Rect(0, 0, bar.DesiredSize.Width, 30));

            return (bar.Children.Cast<Border>().Select(c => c.ActualWidth).ToArray(),
                    bar.Children.Cast<Border>().Select(TabBar.GetFit).ToArray());
        });

        Assert.Equal(TabBar.ChosenLeast, widths[3]);
        Assert.Equal(TabFit.Full, fits[3]);
        Assert.All(widths.Where((_, i) => i != 3), w => Assert.Equal(widths[0], w));
        Assert.All(fits.Where((_, i) => i != 3), f => Assert.NotEqual(TabFit.Full, f));
    }

    /// <summary>
    /// The room is set on the strip, not on the panel - the panel is inside an items control's
    /// template and nobody holds a reference to it. It arrives by property inheritance, and if
    /// that ever stops working every tab silently keeps its full width, which is the bug this
    /// whole thing was about. So: set it two levels up, and check the row actually narrowed.
    /// </summary>
    [Fact]
    public void The_room_reaches_the_panel_from_above_rather_than_being_set_on_it()
    {
        var widths = Sta(() =>
        {
            var bar = new TabBar();
            for (var i = 0; i < 12; i++)
            {
                var child = new Border();
                TabBar.SetChosen(child, i == 3);
                bar.Children.Add(child);
            }

            var strip = new Border { Child = bar };
            var outer = new Border { Child = strip };
            TabBar.SetRoom(outer, 892);

            outer.Measure(new Size(double.PositiveInfinity, 30));
            outer.Arrange(new Rect(0, 0, outer.DesiredSize.Width, 30));

            return bar.Children.Cast<Border>().Select(c => c.ActualWidth).ToArray();
        });

        Assert.Equal(TabBar.ChosenLeast, widths[3]);
        Assert.True(widths[0] < TabBar.Widest, $"the others kept their full width: {widths[0]}");
        Assert.True(widths.Sum() <= 892 + 0.5, $"the row needs {widths.Sum()} of 892");
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

        if (failure is not null) throw new InvalidOperationException("layout failed", failure);
        return result;
    }
}
