using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Oan.App.ViewModels;

namespace Oan.App.Controls;

/// <summary>
/// The address bar's two lives: a row of crumbs you click, and a text box you type a path into.
/// Enter goes; Escape and losing focus put the path back.
/// </summary>
internal sealed class AddressEdit(MainViewModel vm, TextBox box, IInputElement after)
{
    public void Begin()
    {
        vm.EditingAddress = true;

        // After the box has been made visible: focusing something collapsed does nothing.
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    public void Typed(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                var typed = box.Text;
                vm.EditingAddress = false;
                vm.GoToCommand.Execute(typed);
                after.Focus();
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Escape:
                Cancel();
                e.Handled = true;
                break;
        }
    }

    public void Cancel()
    {
        if (!vm.EditingAddress) return;

        vm.EditingAddress = false;
        vm.Address = vm.CurrentPath;
    }
}
