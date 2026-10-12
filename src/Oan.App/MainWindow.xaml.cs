using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly AddressEdit _address;
    private readonly BandSelect _band;
    private Point _pressed;
    private bool _mayDrag;
    private readonly ListDrag _drag;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        // After InitializeComponent: before it the named controls are still null.
        _address = new AddressEdit(vm, AddressBox, List);
        _band = new BandSelect(List);
        _drag = new ListDrag(List, vm);
        _ = new DetailsColumns((GridView)FindResource("DetailsView"), vm.Look.Columns);

        DataContext = vm;
        vm.AskName = (title, suggestion) => PromptWindow.Ask(this, title, suggestion);
        vm.AskPassword = archive => PromptWindow.AskSecret(this, $"Password for {archive}");
        vm.Reselect = rows =>
        {
            foreach (var row in rows) List.SelectedItems.Add(row);
        };

        vm.Look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceViewModel.IsDetails)) ApplyViewMode();
        };

        ApplyViewMode();

        // Sizes land seconds after the rows do; without this the map is drawn once, before any are measured.
        _vm.FolderSizes.Settled += Map.InvalidateVisual;

        Map.Menu = List.ContextMenu;
        Map.ChosenPath += path => _ = Reveal(path, menu: false);
        Map.PickedPath += path => _ = Reveal(path, menu: true);
        Map.OpenedPath += (path, folder) => _ = folder ? _vm.NavigateAsync(path) : Open(path);
        _vm.Look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppearanceViewModel.Treemap)) return;

            ApplyMapHeight();
            _vm.FolderSizes.Deepen();
        };
        ApplyMapHeight();

        _ = new ThemeStage(this, vm);
        _ = new WindowGestures(this, vm, () => OnPropertiesClick(this, new RoutedEventArgs()));
        _vm.PropertyChanged += WarmShellMenus;

        Loaded += (_, _) =>
        {
            vm.Start();
            List.Focus();
        };
    }

    /// <summary>
    /// A ListView refuses a GridView that is already in use, and a View set from a style is re-applied
    /// whenever one of its triggers re-evaluates. So this one property is set here instead.
    /// </summary>
    private void ApplyViewMode() => List.View = _vm.Look.IsDetails ? (ViewBase)FindResource("DetailsView") : null;

    /// <summary>Selects in the list what was clicked on the map, so one menu, acting on the selection, serves both.</summary>
    private async Task Reveal(string path, bool menu)
    {
        if (Paths.Holding(path) is not { } folder) return;
        if (!string.Equals(folder, _vm.CurrentPath, StringComparison.OrdinalIgnoreCase))
            await _vm.NavigateAsync(folder);

        var row = _vm.Rows.FirstOrDefault(
            r => string.Equals(r.Entry.FullPath, path, StringComparison.OrdinalIgnoreCase));

        if (row is null) return;

        List.SelectedItems.Clear();
        List.SelectedItems.Add(row);
        if (menu) Map.ShowMenu();
    }

    private async Task Open(string path)
    {
        await Reveal(path, menu: false);
        if (_vm.HasSelection) _vm.OpenSelectionCommand.Execute(null);
    }

    /// <summary>A pixel height: a star height would fight the splitter, which writes pixels back into the row.</summary>
    private void ApplyMapHeight() =>
        MapRow.Height = _vm.Look.Treemap ? new GridLength(Math.Max(160, Body.ActualHeight / 3)) : new GridLength(0);


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _vm.OwnerHandle = WindowEffects.Handle(this);
        _vm.Look.Apply = ApplyBackdrop;
        _vm.Look.Supported = ApplyBackdrop(_vm.Look.Backdrop);

        // After the backdrop: glass replaces it with acrylic, and must have the last word.
        Theme.Choose(_vm.Look.ThemeName, _vm.Look.Glass);
        Theme.Follow(Application.Current, this);
    }

    /// <summary>
    /// A backdrop only shows where WPF paints nothing, so the background goes transparent, and comes back
    /// when Windows refuses: a transparent window with no backdrop draws as untinted white.
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
    /// Keeps the sidebar from pushing the list out of the window. A MinWidth on both columns is the cause:
    /// when neither can yield, WPF satisfies both and lets the grid overflow. So the list's floor is applied here.
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
        ShellContextMenu.ReleaseHeld();
    }

    /// <summary>Once the first rows are on screen, the shell's menus are built once in the background, so the first
    /// right-click finds their DLLs loaded and their surrogates running.</summary>
    private void WarmShellMenus(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Rows) || _vm.Rows.Count == 0 || _vm.CurrentPath.Length == 0) return;

        _vm.PropertyChanged -= WarmShellMenus;
        var (folder, first) = (_vm.CurrentPath, _vm.Rows[0].Entry);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => ShellContextMenu.WarmUp(_vm.OwnerHandle, folder, new ShellItem(first.FullPath, first.IsDirectory)));
    }

    private void OnCloseMapClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.Look.Treemap = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var mods = e.KeyboardDevice.Modifiers;

        // The address bar: Ctrl+L and Alt+D, which Explorer and every browser accept, and F4.
        if ((e.Key == Key.L && mods == ModifierKeys.Control) || (e.Key == Key.F4 && mods == ModifierKeys.None) ||
            (e.SystemKey == Key.D && mods == ModifierKeys.Alt))
        {
            _address.Begin();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.OemComma && mods == ModifierKeys.Control)
        {
            SettingsWindow.Open(this);
            e.Handled = true;
            return;
        }

        if ((e.Key is Key.F or Key.E && mods == ModifierKeys.Control) || (e.Key == Key.F3 && mods == ModifierKeys.None))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnAddressBarClick(object sender, MouseButtonEventArgs e) => _address.Begin();

    private void OnAddressKeyDown(object sender, KeyEventArgs e) => _address.Typed(e);

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _address.Cancel();

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

    /// <summary>A toolbar button whose menu is its content. Right-click already opens it; this is the left.</summary>
    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => MenuButton.Open(sender);


    /// <summary>Middle-click a folder to open it in a new tab, the way a browser opens a link.</summary>
    private void OnListMiddleDown(object sender, MouseButtonEventArgs e)
    {
        // MouseDown carries every button; without this the mouse's "back" button opened a tab.
        if (e.ChangedButton != MouseButton.Middle) return;
        if (RowAt(e.OriginalSource) is not { Entry.IsDirectory: true } row) return;

        _vm.Tabs.Open(row.Entry.FullPath);
        e.Handled = true;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Headers and scroll bars are inside the list: a rubber band started there captures the mouse and
        // swallows the header's click and the scroll bar's drag.
        if (!BandArea.Starts(e.OriginalSource)) return;

        _pressed = e.GetPosition(this);
        _mayDrag = RowAt(e.OriginalSource) is not null;

        // Empty space starts a selection rectangle; a row starts a drag.
        if (!_mayDrag) _band.Begin(e);
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e) => _band.End();

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor.OnHeaders(e.OriginalSource)) return;

        // Empty space clears the selection, as Explorer does, so the menu that opens is the folder's own.
        if (RowAt(e.OriginalSource) is not { } row) List.SelectedItems.Clear();
        else if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(row);
        }

        // The selection is settled and the menu opens at button up: building it from here overlaps the press.
        if (_vm.CurrentPath.Length == 0) return;
        if (_vm.HasSelection) ShellContextMenu.Prepare(_vm.OwnerHandle, _vm.SelectedShellItems(), _vm.OwnMenu.Items());
        else ShellContextMenu.PrepareForFolder(_vm.OwnerHandle, _vm.CurrentPath, _vm.OwnMenu.Items());
    }

    /// <summary>Explorer's own menu, for a selection or for the folder; the app's menu when the shell has none.</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The column headers have their own menu, as in Explorer: the view, and which columns to show.
        if (Ancestor.OnHeaders(e.OriginalSource))
        {
            e.Handled = true;
            var menu = (ContextMenu)FindResource("HeaderMenu");
            menu.DataContext = _vm;
            menu.PlacementTarget = List;
            menu.IsOpen = true;
            return;
        }

        if (_vm.CurrentPath.Length == 0) return;

        e.Handled = true;
        var point = PointToScreen(Mouse.GetPosition(this));

        try
        {
            var own = _vm.OwnMenu.Items();

            var shell = !_vm.HasSelection
                ? ShellContextMenu.ShowForFolder(_vm.OwnerHandle, _vm.CurrentPath,
                    (int)point.X, (int)point.Y, own)
                : ShellContextMenu.Show(_vm.OwnerHandle, _vm.SelectedShellItems(),
                    (int)point.X, (int)point.Y, own);

            if (shell.OwnCommand != 0) { _vm.OwnMenu.Run(shell.OwnCommand); return; }
            if (shell.Ran) _vm.RefreshCommand.Execute(null);

            // The shell had no menu, or an extension never answered: the app's own menu opens.
            if (shell.Refusal is { } refusal)
            {
                e.Handled = false;
                _vm.Report(refusal);
            }
        }
        catch (Exception ex)
        {
            // Third-party shell extensions run inside that call and may throw anything.
            e.Handled = false;
            _vm.Report(ex.Message);
        }
    }

    /// <summary>Not run through the shell menu: this item exists for the case where that menu never appeared.</summary>
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
        _drag.Out();
    }

    private void OnListDragOver(object sender, DragEventArgs e) => _drag.Over(e);

    private void OnListDragLeave(object sender, DragEventArgs e) => _drag.Leave();

    private void OnListDrop(object sender, DragEventArgs e) => _drag.Drop(e);

    private static EntryViewModel? RowAt(object? source) => Ancestor.RowOf(source);
}
