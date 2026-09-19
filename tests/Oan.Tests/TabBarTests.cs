using Oan.App.Controls;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The width the strip gives each tab. Six tabs used to keep their full width and run under the
/// window buttons, taking the + with them, which is the case the first test pins.
/// </summary>
public class TabBarTests
{
    [Fact]
    public void Tabs_narrow_as_they_pile_up_instead_of_leaving_the_strip()
    {
        const double room = 900;

        var two = TabBar.Each(room, 2);
        var six = TabBar.Each(room, 6);

        Assert.True(six < two, $"six tabs got {six}, two got {two}");
        Assert.True(six * 6 <= room, $"six tabs at {six} need {six * 6} of {room}");
    }

    [Fact]
    public void A_lone_tab_does_not_stretch_across_the_window()
    {
        Assert.Equal(TabBar.Widest, TabBar.Each(1600, 1));
        Assert.Equal(TabBar.Widest, TabBar.Each(1600, 4));
    }

    [Fact]
    public void Past_the_floor_they_stop_shrinking_rather_than_becoming_slivers()
    {
        Assert.Equal(TabBar.Narrowest, TabBar.Each(300, 40));
        Assert.Equal(TabBar.Narrowest, TabBar.Each(0, 3));
    }

    [Fact]
    public void No_room_named_yet_is_not_a_width_of_zero()
    {
        Assert.Equal(TabBar.Widest, TabBar.Each(double.PositiveInfinity, 3));
        Assert.Equal(TabBar.Widest, TabBar.Each(double.NaN, 3));
    }

    [Fact]
    public void An_empty_strip_asks_for_nothing()
    {
        Assert.Equal(0, TabBar.Each(900, 0));
    }
}
