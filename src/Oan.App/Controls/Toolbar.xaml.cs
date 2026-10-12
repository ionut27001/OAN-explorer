using System.Windows;
using System.Windows.Controls;

using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>The command row. Reads the window's view model through its own DataContext and holds no state.</summary>
public partial class Toolbar : UserControl
{
    public Toolbar() => InitializeComponent();

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => MenuButton.Open(sender);
}
