using Oan.App.Platform;
using Xunit;

namespace Oan.Tests;

public class SettingsTests
{
    [Fact]
    public void A_first_run_has_no_file_and_gets_the_defaults()
    {
        using var temp = new TempFolder();

        var settings = Settings.Load(Path.Combine(temp.Root, "never-written.json"));

        Assert.Equal(16, settings.IconSize);
        Assert.Equal(Backdrop.Mica, settings.Backdrop);
    }

    [Fact]
    public void What_was_chosen_comes_back_on_the_next_run()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "settings.json");

        Assert.Null(new Settings { IconSize = 256, Backdrop = Backdrop.Acrylic }.Save(path));
        var settings = Settings.Load(path);

        Assert.Equal(256, settings.IconSize);
        Assert.Equal(Backdrop.Acrylic, settings.Backdrop);
    }

    [Fact]
    public void Saving_creates_the_folder_it_is_saving_into()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "OAN", "settings.json");

        Assert.Null(new Settings { IconSize = 48 }.Save(path));

        Assert.True(File.Exists(path));
        Assert.Equal(48, Settings.Load(path).IconSize);
    }

    [Fact]
    public void A_size_the_shell_has_no_image_list_for_falls_back_to_the_default()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("settings.json", """{ "IconSize": 24, "Backdrop": "Solid" }""");

        var settings = Settings.Load(path);

        Assert.Equal(16, settings.IconSize);
        Assert.Equal(Backdrop.Solid, settings.Backdrop);
    }

    [Fact]
    public void A_backdrop_outside_the_enum_falls_back_to_mica()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("settings.json", """{ "IconSize": 32, "Backdrop": 9 }""");

        var settings = Settings.Load(path);

        Assert.Equal(Backdrop.Mica, settings.Backdrop);
        Assert.Equal(32, settings.IconSize);
    }

    [Fact]
    public void A_corrupt_file_opens_the_app_with_defaults_rather_than_not_at_all()
    {
        using var temp = new TempFolder();
        var path = temp.AddFile("settings.json", "{ this is not json");

        var settings = Settings.Load(path);

        Assert.Equal(16, settings.IconSize);
        Assert.Equal(Backdrop.Mica, settings.Backdrop);
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_and_not_thrown()
    {
        using var temp = new TempFolder();

        // A file where the folder should be: the write cannot succeed and must not escape.
        var blocked = Path.Combine(temp.AddFile("in-the-way"), "settings.json");

        Assert.NotNull(new Settings().Save(blocked));
    }
}
