using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Oan.App.Controls;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.Shell;

namespace Oan.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly BandSelect _band;
    private Point _pressed;
    private bool _mayDrag;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _band = new BandSelect(List);

        DataContext = vm;
        vm.AskName = (title, suggestion) => PromptWindow.Ask(this, title, suggestion);
        vm.Reselect = rows =>
        {
            foreach (var row in rows) List.SelectedItems.Add(row);
        };

        vm.Look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceViewModel.IsDetails)) ApplyViewMode();
        };

        ApplyViewMode();

        Loaded += (_, _) =>
        {
            vm.Start();
            List.Focus();
        };
    }

    /// <summary>
    /// A ListView refuses a GridView that is already in use, and a View that comes from a style is
    /// re-applied every time one of its triggers re-evaluates — which the ListView reads as the
    /// same view being handed to it twice. So this one property is set here instead.
    /// </summary>
    private void ApplyViewMode() => List.View = _vm.Look.IsDetails ? (ViewBase)FindResource("DetailsView") : null;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _vm.OwnerHandle = WindowEffects.Handle(this);
        Theme.Follow(Application.Current, this);

        _vm.Look.Apply = ApplyBackdrop;
        _vm.Look.Supported = ApplyBackdrop(_vm.Look.Backdrop);
    }

    /// <summary>
    /// A backdrop only shows through where WPF paints nothing, so the window stops painting a
    /// background once one is in place — and starts again the moment Windows refuses, because a
    /// transparent window with no backdrop behind it draws as untinted white.
    /// </summary>
    private bool ApplyBackdrop(Backdrop choice)
    {
        var applied = WindowEffects.TryBackdrop(this, choice);

        if (applied && choice != Backdrop.Solid) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "WindowBg");

        return applied;
    }


    private const double ListFloor = 320;

    /// <summary>
    /// Keeps the sidebar from pushing the list out of the window.
    ///
    /// A MinWidth on both columns is not enough and is in fact the cause: when neither side can
    /// yield, WPF satisfies both and lets the grid overflow, so the list ends up drawn past the
    /// window edge. The list's floor is applied here instead, against the width the window
    /// actually has, and it gives way when there is not enough room for it either.
    /// </summary>
    private void ClampSidebar()
    {
        var total = Body.ActualWidth;
        if (total <= 0) return;

        var ceiling = Math.Max(SidebarColumn.MinWidth, total - ListFloor);
        var width = SidebarColumn.ActualWidth;

        if (width > ceiling) SidebarColumn.Width = new GridLength(ceiling);
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => ClampSidebar();

    private void OnSplitterDragged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        ClampSidebar();


    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _vm.RefreshClipboardState();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.Shutdown();
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

    /// <summary>
    /// A toolbar button whose menu is its content. Right-click already opens it; this is the left.
    /// The menu is hung from the button's right edge, because these buttons live at the right edge
    /// of the window and a menu dropped from their left corner opens off the side of it.
    /// </summary>
    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => MenuButton.Open(sender);


    /// <summary>Middle-click a folder to open it in a new tab, the way a browser opens a link.</summary>
    private void OnListMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { Entry.IsDirectory: true } row) return;

        _vm.Tabs.Open(row.Entry.FullPath);
        e.Handled = true;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The headers and the scroll bars are inside the list, so a press on either tunnels
        // through here first. Starting a rubber band there captures the mouse and marks the event
        // handled, so the header never raises its own click — which is why sorting by clicking a
        // column did nothing, and why dragging the scroll bar drew a selection box.
        if (!BandArea.Starts(e.OriginalSource)) return;

        _pressed = e.GetPosition(this);
        _mayDrag = RowAt(e.OriginalSource) is not null;

        // Empty space starts a selection rectangle; a row starts a drag. The two cannot both
        // own the gesture, and pressing on a row is how you drag files out.
        if (!_mayDrag) _band.Begin(e);
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e) => _band.End();

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
        if (_vm.CurrentPath.Length == 0) return;

        e.Handled = true;
        var point = PointToScreen(Mouse.GetPosition(this));

        try
        {
            var own = _vm.OwnMenuItems();

            // Empty space gets the folder's own menu — New, Paste, Properties — from the same
            // system that draws the one for a selection. Two menus owned by two different
            // painters look like two different programs, which is what this replaces.
            var shell = _vm.SelectedPaths().Count == 0
                ? ShellContextMenu.ShowForFolder(_vm.OwnerHandle, _vm.CurrentPath,
                    (int)point.X, (int)point.Y, own)
                : ShellContextMenu.Show(_vm.OwnerHandle, _vm.SelectedShellItems(),
                    (int)point.X, (int)point.Y, own);

            if (shell.OwnCommand != 0) { _vm.RunOwnMenuItem(shell.OwnCommand); return; }
            if (shell.Ran) _vm.RefreshCommand.Execute(null);

            // The shell had no menu for us, or the extension that owns these files never
            // answered. Either way the app's own menu opens, and the notice names what stalled.
            if (shell.Refusal is { } refusal)
            {
                e.Handled = false;
                _vm.Report(refusal);
            }
        }
        catch (Exception ex)
        {
            // Third-party shell extensions run inside that call. Whatever one of them throws,
            // the app's own menu opens instead.
            e.Handled = false;
            _vm.Report(ex.Message);
        }
    }

    /// <summary>
    /// The system property sheet. It is not run through the shell menu because that menu may
    /// never have appeared — this item exists for exactly the case where it did not.
    /// </summary>
    private void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        var paths = _vm.SelectedPaths();
        if (paths.Count > 0 && ShellProperties.Show(paths[0]) is { } refusal) _vm.Report(refusal);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_band.Dragging)
        {
            _band.Move(e);
            return;
        }

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

    private static EntryViewModel? RowAt(object? source) =>
        Ancestor.Of<ListViewItem>(source)?.Content as EntryViewModel;
}
