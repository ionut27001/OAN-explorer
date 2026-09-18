using System.Windows;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;

namespace Oan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last line of defence, mostly for the shell code we host: a misbehaving shell
        // extension should cost the user a message, not the window and its place in it.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "OAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        new MainWindow(new MainViewModel(new ShellFileOperations(), Settings.Load(Settings.DefaultPath))).Show();
    }
}
