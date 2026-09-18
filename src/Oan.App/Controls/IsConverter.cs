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

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? System.Convert.ChangeType(parameter.ToString(), targetType, CultureInfo.InvariantCulture)!
            : Binding.DoNothing;
}
