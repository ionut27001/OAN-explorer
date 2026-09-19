using System.Windows;
using System.Windows.Controls;

namespace Oan.App.Controls;

/// <summary>
/// The command row: new folder, the clipboard, rename and delete, the view switch and sort.
/// It reads the window's view model through its own DataContext and holds no state of its own.
/// </summary>
public partial class Toolbar : UserControl
{
    public Toolbar() => InitializeComponent();

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => MenuButton.Open(sender);
}
