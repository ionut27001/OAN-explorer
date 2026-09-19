using System.Windows;
using System.Windows.Controls;

using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The command row: new folder, the clipboard, rename and delete, the view switch and sort.
/// It reads the window's view model through its own DataContext and holds no state of its own.
/// </summary>
public partial class Toolbar : UserControl
{
    public Toolbar() => InitializeComponent();

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => MenuButton.Open(sender);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        new SettingsWindow(model, Window.GetWindow(this) ?? Application.Current.MainWindow).ShowDialog();
    }
}
