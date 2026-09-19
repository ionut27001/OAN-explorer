using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Oan.App.Controls;

/// <summary>
/// A button whose whole job is to open its own menu. Left click rather than right, and anchored
/// under the button's right edge so the menu lines up with what opened it instead of appearing
/// wherever the pointer happened to be.
/// </summary>
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
}
