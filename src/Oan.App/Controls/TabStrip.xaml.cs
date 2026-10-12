using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>The strip of tabs. Reads the window's view model through its own DataContext.</summary>
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
    /// Hands the rail and the panel the room left once the + has had its share. Inside a StackPanel each is
    /// offered infinite width, so the rail would never scroll and every tab would keep its full width.
    /// </summary>
    private void OnStripResized(object sender, SizeChangedEventArgs e)
    {
        var border = (Border)sender;
        var room = border.ActualWidth - border.Padding.Left - border.Padding.Right
                   - Plus.Width - Plus.Margin.Left;

        Rail.MaxWidth = Math.Max(0, room);
        TabBar.SetRoom(Strip, Math.Max(0, room - Strip.Margin.Left - Strip.Margin.Right));
    }

    private void OnRailWheel(object sender, MouseWheelEventArgs e)
    {
        Rail.ScrollToHorizontalOffset(Rail.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Scrolls the active tab into view. Deferred, because the panel has not placed it yet when this fires.</summary>
    private void OnTabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabsViewModel.Active)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Strip.ItemContainerGenerator.ContainerFromItem(Tabs.Active) is FrameworkElement chip)
                chip.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Left picks the tab, middle closes it, which is why the chip is not a button.</summary>
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
