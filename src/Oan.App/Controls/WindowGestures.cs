using System.Windows;
using System.Windows.Input;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Operations;

namespace Oan.App.Controls;

/// <summary>
/// Gestures that belong to the window rather than one control: the mouse's back and forward buttons,
/// Ctrl+wheel for icon size, Alt+Enter for properties, Ctrl+N for a second window.
/// </summary>
internal sealed class WindowGestures
{
    public WindowGestures(Window window, MainViewModel vm, Action properties)
    {
        window.PreviewMouseDown += (_, e) =>
        {
            var command = e.ChangedButton switch
            {
                MouseButton.XButton1 => vm.BackCommand,
                MouseButton.XButton2 => vm.ForwardCommand,
                _ => null,
            };
            if (command is null) return;

            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        };

        window.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            vm.Look.Step(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };

        window.PreviewKeyDown += (_, e) =>
        {
            // With Alt down WPF reports the key as System and keeps the real one in SystemKey.
            if (e.SystemKey == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt && vm.HasSelection)
            {
                properties();
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var model = new MainViewModel(new ShellFileOperations(), Settings.Load(Settings.DefaultPath))
                {
                    StartAt = vm.CurrentPath.Length > 0 ? vm.CurrentPath : null,
                };
                new MainWindow(model).Show();
                e.Handled = true;
            }
        };
    }
}
