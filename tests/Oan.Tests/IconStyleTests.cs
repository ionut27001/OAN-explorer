using Oan.App.Platform;
using Oan.App.ViewModels;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The choice between the shell's icons and the flat glyphs. It is a setting, so it has to
/// survive a restart — a preference that resets is a preference nobody trusts.
/// </summary>
public class IconStyleTests
{
    [Fact]
    public void Windows_icons_are_what_a_fresh_install_gets()
    {
        Assert.Equal(IconStyle.Windows, new Settings().Icons);
        Assert.False(Look(new Settings()).PlainIcons);
    }

    [Fact]
    public void Choosing_simple_is_remembered_and_told_to_the_markup()
    {
        var settings = new Settings();
        var saved = 0;
        var look = Look(settings, () => saved++);

        look.Icons = IconStyle.Simple;

        Assert.True(look.PlainIcons);
        Assert.Equal(IconStyle.Simple, settings.Icons);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void Choosing_what_is_already_chosen_does_not_write_the_file_again()
    {
        var saved = 0;
        var look = Look(new Settings(), () => saved++);

        look.Icons = IconStyle.Windows;

        Assert.Equal(0, saved);
    }

    [Fact]
    public void A_style_the_file_does_not_know_falls_back_rather_than_drawing_nothing()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("settings.json", """{ "Icons": 7 }""");

        Assert.Equal(IconStyle.Windows, Settings.Load(path).Icons);
    }

    [Fact]
    public void It_comes_back_after_a_restart()
    {
        using var temp = new TempFolder();
        var path = System.IO.Path.Combine(temp.Root, "settings.json");

        Assert.Null(new Settings { Icons = IconStyle.Simple }.Save(path));

        Assert.Equal(IconStyle.Simple, Settings.Load(path).Icons);
    }

    private static AppearanceViewModel Look(Settings settings, Action? remember = null) =>
        new(settings, remember ?? (() => { }));
}
