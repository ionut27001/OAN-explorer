using System.Globalization;
using System.Windows.Data;

namespace Oan.App.Controls;

/// <summary>
/// True when the bound value equals the parameter — how a radio button or a checked menu item
/// says which one of a set is current. Converting back turns the one that was just checked into
/// that value and ignores the one being unchecked, so the source is never cleared on the way out.
/// </summary>
public sealed class IsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;

        var text = parameter.ToString()!;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            // Enums separately, because Convert.ChangeType cannot make one from a string: it
            // throws InvalidCastException, which reached the user as a crash dialog the first
            // time a radio button was bound to one.
            return type.IsEnum
                ? Enum.Parse(type, text, ignoreCase: true)
                : System.Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException
                                     or ArgumentException or OverflowException)
        {
            // A parameter the target cannot take is a mistake in the markup, not something the
            // person clicking is doing wrong. Leave the source alone rather than take the app out.
            return Binding.DoNothing;
        }
    }
}
