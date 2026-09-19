using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The strip of tabs. It reads the window's view model through its own DataContext, so it knows
/// nothing about the window beyond there being a Tabs on it.
/// </summary>
public partial class TabStrip : UserControl
{
    public TabStrip() => InitializeComponent();

    private TabsViewModel Tabs => ((MainViewModel)DataContext).Tabs;

    /// <summary>
    /// Left picks the tab, middle closes it — the two gestures every tabbed program has, and the
    /// reason the chip handles the mouse itself rather than being a button.
    /// </summary>
    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel tab }) return;

        if (e.ChangedButton == MouseButton.Middle) Tabs.CloseTabCommand.Execute(tab);
        else if (e.ChangedButton == MouseButton.Left) Tabs.Active = tab;
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { DataContext: TabViewModel tab } chip) return;

        DragDrop.DoDragDrop(chip, tab, DragDropEffects.Move);
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel onto }) return;
        if (e.Data.GetData(typeof(TabViewModel)) is not TabViewModel dragged || ReferenceEquals(dragged, onto)) return;

        Tabs.Move(dragged, Tabs.Items.IndexOf(onto));
        e.Handled = true;
    }
}
