using System.Windows;
using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>
/// The caption band: logo, tabs, and the three window buttons.
///
/// It is the whole title bar, because the tab strip belongs in it — below it the strip had to
/// appear and disappear with the tab count and the page jumped when it did.
/// </summary>
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

    private void OnMinimiseClick(object sender, RoutedEventArgs e) => Window.WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e) =>
        Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Window.Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {

        var maximised = Window.WindowState == WindowState.Maximized;

        // A maximised WPF window under a custom chrome hangs its edges past the work area; the
        // margin puts them back. Set here rather than bound, because the window is not a view
        // model and inventing a notification on it to serve two glyphs is not worth the class.
        Window.BorderThickness = maximised ? new Thickness(8) : new Thickness(0);
        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }
}
