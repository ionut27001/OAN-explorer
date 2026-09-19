using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>
/// Where a press may start a selection rectangle.
///
/// Inside the scrolled content, which is where the rows are drawn — and not in the column header
/// row, which WPF puts inside that same scrolled content so the headers scroll sideways with
/// their columns. That is not a guess: HeaderClickReachTests builds a real ListView and asserts
/// it. Getting it wrong cost the header its click, and with it sorting by column, twice.
/// </summary>
internal static class BandArea
{
    public static bool Starts(object? source) =>
        Ancestor.Of<ScrollContentPresenter>(source) is not null &&
        Ancestor.Of<GridViewHeaderRowPresenter>(source) is null;
}
