using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Shell;

namespace Oan.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Point _pressed;
    private bool _mayDrag;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        DataContext = vm;
        vm.AskName = (title, suggestion) => PromptWindow.Ask(this, title, suggestion);
        vm.Reselect = rows =>
        {
            foreach (var row in rows) List.SelectedItems.Add(row);
        };

        Loaded += (_, _) =>
        {
            vm.Start();
            List.Focus();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _vm.OwnerHandle = WindowEffects.Handle(this);
        Theme.Follow(Application.Current, this);

        // Mica only shows through where WPF paints nothing, so the window itself stops
        // painting a background once the backdrop is in place.
        if (WindowEffects.TryMica(this)) Background = Brushes.Transparent;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _vm.RefreshClipboardState();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var mods = e.KeyboardDevice.Modifiers;

        // The address bar: Ctrl+L and Alt+D, both of which Explorer and every browser accept.
        if ((e.Key == Key.L && mods == ModifierKeys.Control) ||
            (e.Key == Key.D && mods == ModifierKeys.Alt))
        {
            BeginAddressEdit();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F && mods == ModifierKeys.Control) || (e.Key == Key.F3 && mods == ModifierKeys.None))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnAddressBarClick(object sender, MouseButtonEventArgs e) => BeginAddressEdit();

    private void BeginAddressEdit()
    {
        _vm.EditingAddress = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        });
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var typed = AddressBox.Text;
                _vm.EditingAddress = false;
                _vm.GoToCommand.Execute(typed);
                List.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                CancelAddressEdit();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CancelAddressEdit();

    private void CancelAddressEdit()
    {
        if (!_vm.EditingAddress) return;

        _vm.EditingAddress = false;
        _vm.Address = _vm.CurrentPath;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.Selection = List.SelectedItems.Cast<EntryViewModel>().ToArray();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row) _vm.Open(row);
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Content: string key } && Enum.TryParse<SortKey>(key, out var sort))
            _vm.SortCommand.Execute(sort);
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is not { } menu) return;

        menu.PlacementTarget = SortButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = e.GetPosition(this);
        _mayDrag = RowAt(e.OriginalSource) is not null;
    }

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Empty space clears the selection, the way Explorer does, so the menu that opens is the
        // folder's own — which is the only place New folder and Paste live.
        if (RowAt(e.OriginalSource) is not { } row)
        {
            List.SelectedItems.Clear();
            return;
        }

        if (List.SelectedItems.Contains(row)) return;

        List.SelectedItems.Clear();
        List.SelectedItems.Add(row);
    }

    /// <summary>
    /// Over a selection we hand off to Explorer's own menu, so shell extensions, Open With and
    /// Properties are all there. On empty space there is nothing to hand off, so the app's own
    /// menu shows instead.
    /// </summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var paths = _vm.SelectedPaths();
        if (paths.Count == 0) return;

        e.Handled = true;
        var point = PointToScreen(Mouse.GetPosition(this));

        try
        {
            if (ShellContextMenu.Show(_vm.OwnerHandle, paths, (int)point.X, (int)point.Y))
                _vm.RefreshCommand.Execute(null);
        }
        catch (Exception ex)
        {
            // Third-party shell extensions run inside that call. Whatever one of them throws,
            // the app's own menu opens instead.
            e.Handled = false;
            _vm.Report(ex.Message);
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mayDrag || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - _pressed;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _mayDrag = false;
        var paths = _vm.SelectedPaths();
        if (paths.Count == 0) return;

        var files = new StringCollection();
        foreach (var path in paths) files.Add(path);

        var data = new DataObject();
        data.SetFileDropList(files);

        var effect = DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move);
        if (effect == DragDropEffects.Move) _vm.RefreshCommand.Execute(null);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = EffectFor(e);
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        var effect = EffectFor(e);
        if (effect != DragDropEffects.None && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _vm.Drop(paths, move: effect == DragDropEffects.Move);

        e.Effects = effect;
        e.Handled = true;
    }

    /// <summary>
    /// What this drop would actually do. Shift asks for a move, but a source that only offers a
    /// copy — a zip folder, a download in progress — must never be told we moved its items.
    /// </summary>
    private DragDropEffects EffectFor(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !_vm.CanWriteHere) return DragDropEffects.None;

        var wanted = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & wanted) != 0) return wanted;

        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static EntryViewModel? RowAt(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null and not ListViewItem)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

        return (node as ListViewItem)?.Content as EntryViewModel;
    }
}
