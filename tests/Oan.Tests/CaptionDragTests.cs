using System.Xml.Linq;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The caption band drags the window except where something opts out with
/// IsHitTestVisibleInChrome. Put that on a container and you lose every empty pixel inside it:
/// the strip is as wide as the window, so the window could only be dragged by its logo.
/// These read the markup because the property is what decides it, and nothing at runtime
/// reports "this pixel drags the window".
/// </summary>
public class CaptionDragTests
{
    // The attached property's XML name is the whole "WindowChrome.IsHitTestVisibleInChrome",
    // not just the property: read only the second half and every one of these looks unset.
    private static readonly XName Opt =
        XNamespace.Get("clr-namespace:System.Windows.Shell;assembly=PresentationFramework")
        + "WindowChrome.IsHitTestVisibleInChrome";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_tab_strip_as_a_whole_still_drags_the_window()
    {
        var bar = Load("src/Oan.App/Controls/TitleBar.xaml");
        var strip = bar.Descendants().Single(e => e.Name.LocalName == "TabStrip");

        Assert.Null(strip.Attribute(Opt));

        // The same file, so a typo in the name above cannot make the check pass by finding nothing.
        var buttons = bar.Descendants().Single(e => e.Name.LocalName == "StackPanel");
        Assert.Equal("True", (string?)buttons.Attribute(Opt));
    }

    [Fact]
    public void The_tabs_and_the_new_tab_button_take_their_own_clicks()
    {
        var strip = Load("src/Oan.App/Controls/TabStrip.xaml");

        var chip = strip.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "Chip");
        var plus = strip.Descendants().Single(e =>
            e.Name.LocalName == "Button" && ((string?)e.Attribute("ToolTip"))?.StartsWith("New tab") == true);

        Assert.Equal("True", (string?)chip.Attribute(Opt));
        Assert.Equal("True", (string?)plus.Attribute(Opt));
    }

    private static XElement Load(string relative) =>
        XDocument.Load(Path.Combine(Repo.Root, relative)).Root!;
}
