using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oan.App.Controls;

/// <summary>A button that opens its own menu on left click, anchored under the button's right edge.</summary>
internal static class MenuButton
{
    public static void Open(object sender)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    /// <summary>Under the left edge, for a button at the left of the window, where the right edge would cover the button.</summary>
    public static void Under(object sender)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
