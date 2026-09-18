using System.Windows;

namespace Oan.App;

public partial class PromptWindow : Window
{
    private PromptWindow() => InitializeComponent();

    public static string? Ask(Window owner, string title, string suggestion)
    {
        var dialog = new PromptWindow { Owner = owner, Title = title };
        dialog.Input.Text = suggestion;
        dialog.Loaded += (_, _) =>
        {
            dialog.Input.Focus();
            dialog.Input.SelectAll();
        };

        return dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
