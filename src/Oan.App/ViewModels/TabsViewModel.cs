using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Oan.App.Platform;
using Oan.Core.Navigation;

namespace Oan.App.ViewModels;

/// <summary>
/// One tab: where it is, and how it got there.
///
/// The history is per tab and not shared, which is the whole point — Back in one tab must not
/// walk through folders another tab visited.
/// </summary>
public sealed class TabViewModel : Observable
{
    private string _path = "";
    private bool _isActive;
    private ImageSource? _icon;

    public History History { get; } = new();

    /// <summary>Kept by TabsViewModel, so the strip can style the current tab with one trigger.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;

            OnChanged(nameof(Title));
            Request();
        }
    }

    /// <summary>
    /// The folder's own icon, which is what Explorer puts on a tab. Null until the shell answers
    /// and for a tab with nowhere to point yet; the chip shows a plain folder glyph until then.
    /// </summary>
    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    /// <summary>
    /// Asked again on every navigation, unlike a sidebar place whose path never moves. An answer
    /// for a folder the tab has already left is dropped — both the request and the reply land on
    /// the dispatcher, so reading the path back cannot race the write that changed it.
    /// </summary>
    private void Request()
    {
        var wanted = _path;
        if (wanted.Length == 0)
        {
            Icon = null;
            return;
        }

        IconLoader.Load(wanted, isDirectory: true, 16, icon =>
        {
            if (_path == wanted) Icon = icon;
        });
    }

    /// <summary>
    /// The folder's own name. A drive root has none — <c>GetFileName("C:\")</c> is empty — so it
    /// keeps the root itself rather than showing a tab with a blank label.
    /// </summary>
    public string Title
    {
        get
        {
            if (_path.Length == 0) return "New tab";

            var name = System.IO.Path.GetFileName(_path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return name.Length > 0 ? name : _path;
        }
    }
}

/// <summary>
/// The strip of tabs, and which one the window is showing.
///
/// Opening, closing and switching are all here; the folder itself is still loaded by
/// MainViewModel, which asks this object where the active tab is pointing.
/// </summary>
public sealed class TabsViewModel : Observable
{
    private TabViewModel _active;

    public TabsViewModel()
    {
        _active = new TabViewModel { IsActive = true };
        Items.Add(_active);

        NewTabCommand = new RelayCommand(() => Open(Active.Path));
        CloseTabCommand = new RelayCommand(p => Close(p as TabViewModel ?? Active), _ => Items.Count > 1);
        NextTabCommand = new RelayCommand(() => Step(1));
        PreviousTabCommand = new RelayCommand(() => Step(-1));
    }

    /// <summary>Raised when the window should load what the active tab points at.</summary>
    public event Action<string>? Switched;

    public ObservableCollection<TabViewModel> Items { get; } = [];

    public TabViewModel Active
    {
        get => _active;
        set
        {
            if (!Items.Contains(value) || ReferenceEquals(_active, value)) return;

            foreach (var tab in Items) tab.IsActive = ReferenceEquals(tab, value);
            _active = value;
            OnChanged();
            OnChanged(nameof(CanClose));
            if (value.Path.Length > 0) Switched?.Invoke(value.Path);
        }
    }

    /// <summary>The last tab is not closable: a window with no tab has nothing to show.</summary>
    public bool CanClose => Items.Count > 1;

    public bool Many => Items.Count > 1;

    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand NextTabCommand { get; }
    public ICommand PreviousTabCommand { get; }

    /// <summary>A new tab opens beside the active one, the way a browser does, not at the end.</summary>
    public TabViewModel Open(string path)
    {
        var tab = new TabViewModel { Path = path };
        Items.Insert(Items.IndexOf(_active) + 1, tab);
        Announce();
        Active = tab;

        // Active only raises Switched when the path is set; a blank new tab has nowhere to go.
        return tab;
    }

    public void Close(TabViewModel tab)
    {
        if (Items.Count <= 1 || !Items.Contains(tab)) return;

        var index = Items.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, _active);
        Items.Remove(tab);
        Announce();

        // Closing the one you are looking at hands you the one on its right, then its left —
        // the same place a browser leaves you.
        if (wasActive) Active = Items[Math.Min(index, Items.Count - 1)];
    }

    /// <summary>Reorders by dragging. Out-of-range targets clamp rather than throw.</summary>
    public void Move(TabViewModel tab, int to)
    {
        var from = Items.IndexOf(tab);
        if (from < 0) return;

        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from != to) Items.Move(from, to);
    }

    /// <summary>Remembers where the active tab is, so switching back returns to it.</summary>
    public void Visited(string path) => Active.Path = path;

    private void Step(int by)
    {
        var index = Items.IndexOf(_active);
        if (index < 0 || Items.Count < 2) return;

        // Wraps: Ctrl+Tab on the last tab goes to the first, as every tabbed program does.
        Active = Items[(index + by + Items.Count) % Items.Count];
    }

    private void Announce()
    {
        OnChanged(nameof(CanClose));
        OnChanged(nameof(Many));
    }
}
