using System.Windows;

namespace Oan.App;

public partial class PromptWindow : Window
{
    private PromptWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Platform.WindowEffects.UseDarkTitleBar(this, Platform.Theme.IsDark);
    }

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

    /// <summary>The same prompt, typed unseen. Null when cancelled or left empty.</summary>
    public static string? AskSecret(Window owner, string title)
    {
        var dialog = new PromptWindow { Owner = owner, Title = title };
        dialog.Input.Visibility = Visibility.Collapsed;
        dialog.Secret.Visibility = Visibility.Visible;
        dialog.Loaded += (_, _) => dialog.Secret.Focus();

        return dialog.ShowDialog() == true && dialog.Secret.Password.Length > 0 ? dialog.Secret.Password : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
