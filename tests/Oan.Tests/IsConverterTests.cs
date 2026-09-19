using System.Globalization;
using System.Windows.Data;
using Oan.App.Controls;
using Oan.App.Platform;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The converter behind every radio button and checked menu item. Converting BACK is the half
/// that is easy to get wrong, because it only runs when someone clicks: binding a radio button
/// to an enum crashed the window with "Invalid cast from System.String" the first time anyone
/// opened Settings and picked one.
/// </summary>
public class IsConverterTests
{
    private static readonly IsConverter Converter = new();

    [Theory]
    [InlineData("Simple", true)]
    [InlineData("Windows", false)]
    public void It_says_which_one_is_current(string parameter, bool expected) =>
        Assert.Equal(expected, Converter.Convert(IconStyle.Simple, typeof(bool), parameter, CultureInfo.InvariantCulture));

    [Fact]
    public void Checking_a_radio_button_bound_to_an_enum_gives_that_enum()
    {
        Assert.Equal(IconStyle.Simple, Back(typeof(IconStyle), "Simple"));
        Assert.Equal(Backdrop.MicaAlt, Back(typeof(Backdrop), "MicaAlt"));
    }

    [Fact]
    public void Checking_one_bound_to_a_number_still_gives_a_number()
    {
        Assert.Equal(48, Back(typeof(int), "48"));
    }

    [Fact]
    public void The_one_being_unchecked_leaves_the_source_alone()
    {
        Assert.Equal(Binding.DoNothing, Converter.ConvertBack(false, typeof(IconStyle), "Simple", CultureInfo.InvariantCulture));
        Assert.Equal(Binding.DoNothing, Converter.ConvertBack(true, typeof(IconStyle), null, CultureInfo.InvariantCulture));
    }

    /// <summary>A typo in the markup is a mistake of mine, and must not be a dialog in their face.</summary>
    [Fact]
    public void A_parameter_the_target_cannot_take_changes_nothing_rather_than_throwing()
    {
        Assert.Equal(Binding.DoNothing, Back(typeof(IconStyle), "Nonsense"));
        Assert.Equal(Binding.DoNothing, Back(typeof(int), "not a number"));
    }

    private static object Back(Type target, string parameter) =>
        Converter.ConvertBack(true, target, parameter, CultureInfo.InvariantCulture);
}
