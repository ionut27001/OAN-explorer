using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>
/// Where a press may start a selection rectangle: inside the scrolled content but not the column header row,
/// which WPF puts inside that same content. Empty space is inside it only because the items panels have a
/// background (RowPanel, TilePanel). Pinned by HeaderClickReachTests and RowReachTests.
/// </summary>
internal static class BandArea
{
    public static bool Starts(object? source) =>
        Ancestor.Of<ScrollContentPresenter>(source) is not null &&
        Ancestor.Of<GridViewHeaderRowPresenter>(source) is null;
}
