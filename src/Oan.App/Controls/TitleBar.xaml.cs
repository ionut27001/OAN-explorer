using System.Windows;
using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>The caption band, which is the whole title bar: logo, tabs, and the three window buttons.</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Window.StateChanged += OnWindowStateChanged;
            OnWindowStateChanged(this, EventArgs.Empty);
        };
    }

    private Window Window => Window.GetWindow(this)!;

    /// <summary>Under the logo rather than at the pointer, so left and right click open the menu in the same place.</summary>
    private void OnLogoClick(object sender, RoutedEventArgs e) => MenuButton.Under(sender);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsWindow.Open(Window);

    private void OnMinimiseClick(object sender, RoutedEventArgs e) => Window.WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e) =>
        Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Window.Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {

        var maximised = Window.WindowState == WindowState.Maximized;

        // A maximised WPF window under a custom chrome hangs its edges past the work area; the margin puts them back.
        Window.BorderThickness = maximised ? new Thickness(8) : new Thickness(0);
        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }
}
