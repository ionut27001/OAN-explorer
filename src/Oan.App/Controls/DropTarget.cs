using System.Windows;

namespace Oan.App.Controls;

/// <summary>The folder a drop would go into, marked on the row so it can be seen.</summary>
internal static class DropTarget
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(DropTarget), new PropertyMetadata(false));

    public static bool GetIsOn(DependencyObject item) => (bool)item.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject item, bool value) => item.SetValue(IsOnProperty, value);
}
