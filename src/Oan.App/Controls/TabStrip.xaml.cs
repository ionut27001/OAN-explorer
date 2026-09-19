using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The strip of tabs. It reads the window's view model through its own DataContext, so it knows
/// nothing about the window beyond there being a Tabs on it.
/// </summary>
public partial class TabStrip : UserControl
{
    public TabStrip()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Tabs.PropertyChanged -= OnTabsChanged;
            Tabs.PropertyChanged += OnTabsChanged;
        };
    }

    private TabsViewModel Tabs => ((MainViewModel)DataContext).Tabs;

    /// <summary>
    /// Hands the strip the room that is left once the + has had its share. Both the rail and the
    /// panel need it: inside a StackPanel each is offered infinite width, which would make the
    /// rail size itself to its contents and never scroll, and every tab keep its full width.
    /// </summary>
    private void OnStripResized(object sender, SizeChangedEventArgs e)
    {
        var border = (Border)sender;
        var room = border.ActualWidth - border.Padding.Left - border.Padding.Right
                   - Plus.Width - Plus.Margin.Left;

        Rail.MaxWidth = Math.Max(0, room);
        TabBar.SetRoom(Strip, Math.Max(0, room - Strip.Margin.Left - Strip.Margin.Right));
    }

    /// <summary>A wheel over the strip walks along it, which is the gesture browsers gave it.</summary>
    private void OnRailWheel(object sender, MouseWheelEventArgs e)
    {
        Rail.ScrollToHorizontalOffset(Rail.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// A tab you switched to with the keyboard, or one just opened, can be off the end of a
    /// scrolled strip. Deferred, because the panel has not placed it yet when this fires.
    /// </summary>
    private void OnTabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabsViewModel.Active)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Strip.ItemContainerGenerator.ContainerFromItem(Tabs.Active) is FrameworkElement chip)
                chip.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

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
