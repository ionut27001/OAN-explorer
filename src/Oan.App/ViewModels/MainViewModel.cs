using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Model;
using Oan.Core.Navigation;
using Oan.Core.Operations;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

public enum SortKey { Name, Size, Kind, Modified }

public sealed record Crumb(string Name, string Path);

/// <summary>
/// An entry of the New submenu; a null template is the folder. It carries the command because a
/// menu inside a popup has no ancestor to reach the view model through.
/// </summary>
public sealed record NewItem(string Label, string? Gesture, ICommand Command, NewFileTemplate? Template);

public sealed class MainViewModel : Observable
{
    private readonly IFileOperations _ops;
    private readonly Settings _settings;
    private readonly History _history = new();
    private readonly SizeQueue _folderSizes;
    private CancellationTokenSource? _load;

    private DirectoryListing _listing = new() { Path = "", Entries = [] };
    private IReadOnlyList<EntryViewModel> _rows = [];
    private IReadOnlyList<EntryViewModel> _selection = [];
    private IReadOnlyList<Crumb> _crumbs = [];
    private IReadOnlyList<PlaceViewModel> _quick = [];
    private IReadOnlyList<PlaceViewModel> _drives = [];
    private IReadOnlyList<NewItem> _newItems;
    private string _address = "";
    private string _filter = "";
    private string _freeText = "";
    private string? _notice;
    private bool _busy;
    private bool _showHidden;
    private bool _canPaste;
    private bool _editingAddress;
    private bool _backdropSupported = true;
    private FileKind _kind = FileKind.All;
    private SortKey _sort = SortKey.Name;
    private bool _descending;

    public MainViewModel(IFileOperations ops, Settings settings)
    {
        _ops = ops;
        _settings = settings;
        _folderSizes = new SizeQueue(Measured);

        BackCommand = new RelayCommand(() => Go(_history.Back()), () => _history.CanGoBack);
        ForwardCommand = new RelayCommand(() => Go(_history.Forward()), () => _history.CanGoForward);
        UpCommand = new RelayCommand(() => Go(History.Parent(CurrentPath)), () => ParentPath is not null);
        RefreshCommand = new RelayCommand(() => { Notice = null; Run(ReloadAsync); }, () => CurrentPath.Length > 0);

        OpenPlaceCommand = new RelayCommand(p => Run(() => NavigateAsync((string)p!)));
        GoToCommand = new RelayCommand(p => Run(() => NavigateAsync(p as string ?? Address)));
        SortCommand = new RelayCommand(p => SortBy((SortKey)p!));

        CopyCommand = new RelayCommand(() => PutOnClipboard(cut: false), () => HasSelection);
        CutCommand = new RelayCommand(() => PutOnClipboard(cut: true), () => HasSelection);
        PasteCommand = new RelayCommand(() => Run(PasteAsync), () => CanPaste && CanWriteHere);
        RenameCommand = new RelayCommand(() => Run(RenameAsync), () => _selection.Count == 1);
        DeleteCommand = new RelayCommand(() => Run(() => DeleteAsync(permanent: false)), () => HasSelection);
        DeletePermanentlyCommand = new RelayCommand(() => Run(() => DeleteAsync(permanent: true)), () => HasSelection);
        NewFolderCommand = new RelayCommand(() => Run(NewFolderAsync), () => CanWriteHere);
        NewItemCommand = new RelayCommand(p => Run(() => NewAsync((NewItem)p!)), _ => CanWriteHere);
        OpenSelectionCommand = new RelayCommand(OpenSelection, () => HasSelection);
        BackdropCommand = new RelayCommand(p => Backdrop = (Backdrop)p!);
        KindCommand = new RelayCommand(p => KindFilter = (FileKind)p!);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => Filtering);
        MeasureSizesCommand = new RelayCommand(MeasureQuick, () => Quick.Count > 0);

        _newItems = [new NewItem("Folder", "Ctrl+Shift+N", NewItemCommand, null)];
        _ = LoadTemplatesAsync();
    }

    /// <summary>
    /// The registry scan walks every class the machine has registered, so it happens once, off the
    /// UI thread, and the menu grows the moment it lands rather than making the first click wait.
    /// </summary>
    private async Task LoadTemplatesAsync()
    {
        var templates = await Task.Run(ShellNew.Templates);
        NewItems = [.. _newItems, .. templates.Select(t => new NewItem(t.Label, null, NewItemCommand, t))];
    }

    /// <summary>Parents the shell's progress and conflict dialogs. Set by the window.</summary>
    public nint OwnerHandle { get; set; }

    /// <summary>Asks the user for a name (title, suggestion) and returns null if they cancel. Set by the window.</summary>
    public Func<string, string, string?>? AskName { get; set; }

    /// <summary>Re-applies the selection to the rows a rebuild replaced. Set by the window.</summary>
    public Action<IReadOnlyList<EntryViewModel>>? Reselect { get; set; }

    /// <summary>Puts the backdrop on the window and answers whether Windows took it. Set by the window.</summary>
    public Func<Backdrop, bool>? ApplyBackdrop { get; set; }

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenPlaceCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeletePermanentlyCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewItemCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    public ICommand BackdropCommand { get; }
    public ICommand KindCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand MeasureSizesCommand { get; }

    public IReadOnlyList<EntryViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }
    public IReadOnlyList<Crumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }
    public IReadOnlyList<PlaceViewModel> Quick { get => _quick; private set => Set(ref _quick, value); }
    public IReadOnlyList<PlaceViewModel> Drives { get => _drives; private set => Set(ref _drives, value); }
    public IReadOnlyList<NewItem> NewItems { get => _newItems; private set => Set(ref _newItems, value); }

    public string CurrentPath => _listing.Path;
    public string? ParentPath => CurrentPath.Length > 0 ? History.Parent(CurrentPath) : null;
    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) OnChanged(nameof(StatusText)); }
    }

    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }

    public string Address { get => _address; set => Set(ref _address, value); }

    public bool EditingAddress { get => _editingAddress; set => Set(ref _editingAddress, value); }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) Rebuild(); }
    }

    /// <summary>The second filter, and it stacks with the name: both set means both applied.</summary>
    public FileKind KindFilter
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;

            OnChanged(nameof(KindFilterText));
            OnChanged(nameof(KindFiltered));
            Rebuild();
        }
    }

    /// <summary>Empty when every kind is shown, so the toolbar button is a bare funnel.</summary>
    public string KindFilterText => _kind == FileKind.All ? "" : _kind.ToString();

    public bool KindFiltered => _kind != FileKind.All;

    /// <summary>True when something is being hidden — which the status bar has to admit to.</summary>
    public bool Filtering => _filter.Length > 0 || _kind != FileKind.All;

    public bool ShowHidden
    {
        get => _showHidden;
        set { if (Set(ref _showHidden, value)) Run(ReloadAsync); }
    }

    /// <summary>
    /// One of the four sizes the shell keeps an image list for. 16 is the details view; anything
    /// larger switches to the tile grid, because a details row with a 48px icon is mostly gap.
    /// </summary>
    public int IconSize
    {
        get => _settings.IconSize;
        set
        {
            if (_settings.IconSize == value) return;

            _settings.IconSize = value;

            // Each size is its own cache entry and a 256px bitmap is a quarter of a megabyte, so
            // the size being left behind is dropped rather than kept for a switch back.
            ShellIcons.Clear();
            foreach (var row in Rows) row.SetIconSize(value);

            OnChanged();
            OnChanged(nameof(IsDetails));
            OnChanged(nameof(TileWidth));
            OnChanged(nameof(TileHeight));
            Remember();
        }
    }

    public bool IsDetails => IconSize == 16;

    /// <summary>The tile a grid item is laid out in: the icon, then room for two lines of name.</summary>
    public double TileWidth => IconSize + 48;

    public double TileHeight => IconSize + 60;

    public Backdrop Backdrop
    {
        get => _settings.Backdrop;
        set
        {
            if (_settings.Backdrop == value) return;

            _settings.Backdrop = value;
            BackdropSupported = ApplyBackdrop?.Invoke(value) ?? false;

            OnChanged();
            OnChanged(nameof(ChromeOverAcrylic));
            Remember();
        }
    }

    /// <summary>False when Windows refused the attribute; the window is solid whatever is chosen.</summary>
    public bool BackdropSupported
    {
        get => _backdropSupported;
        set
        {
            if (!Set(ref _backdropSupported, value)) return;

            OnChanged(nameof(BackdropNote));
            OnChanged(nameof(ChromeOverAcrylic));
        }
    }

    /// <summary>
    /// Acrylic blurs whatever window is behind this one, so the chrome cannot know how bright its
    /// own background is: measured over a white window, the muted status text came out at 1.31:1.
    /// In that one mode the bars that carry text are given a scrim. Mica tints from the wallpaper
    /// alone and measured the same over a white window as over the desktop, so it is left clear.
    /// </summary>
    public bool ChromeOverAcrylic => BackdropSupported && Backdrop == Backdrop.Acrylic;

    public string? BackdropNote =>
        BackdropSupported ? null : "Not supported on this Windows build — the window stays solid.";

    public bool CanPaste { get => _canPaste; private set => Set(ref _canPaste, value); }

    public IReadOnlyList<EntryViewModel> Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            OnChanged();
            OnChanged(nameof(StatusText));
        }
    }

    public string NameHeader => Header("Name", SortKey.Name);
    public string SizeHeader => Header("Size", SortKey.Size);
    public string KindHeader => Header("Type", SortKey.Kind);
    public string ModifiedHeader => Header("Date modified", SortKey.Modified);

    public string StatusText
    {
        get
        {
            if (Busy) return "Reading folder…";
            if (CurrentPath.Length == 0) return "";

            var text = Filtering
                ? $"{Rows.Count} of {_listing.Entries.Count} items"
                : $"{Rows.Count} item{(Rows.Count == 1 ? "" : "s")}";

            if (_selection.Count > 0) text += $"  ·  {_selection.Count} selected";
            return text;
        }
    }

    /// <summary>Set whenever the listing was partial or an operation went wrong. Never silent.</summary>
    public string? WarningText
    {
        get
        {
            if (_notice is not null) return _notice;
            if (_listing.Error is not null) return _listing.Error;
            return _listing.Skipped > 0 ? $"{_listing.Skipped} item(s) in this folder could not be read" : null;
        }
    }

    /// <summary>What the empty list area says. A failed listing never reads as an empty folder.</summary>
    public string? ListMessage
    {
        get
        {
            if (_listing.Error is not null) return _listing.Error;
            if (Rows.Count > 0 || CurrentPath.Length == 0) return null;
            return Filtering ? "No items match the filter." : "This folder is empty.";
        }
    }

    public void Start() =>
        Run(() => NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public async Task NavigateAsync(string path)
    {
        var target = Resolve(path);
        if (target is null)
        {
            Notice = $"\"{path}\" is not a folder we can open.";
            return;
        }

        Notice = null;
        _history.Visit(target);
        await LoadAsync(target);
    }

    public void RefreshClipboardState() => CanPaste = ClipboardFiles.HasFiles();

    public void Open(EntryViewModel row)
    {
        if (row.Entry.IsDirectory) Run(() => NavigateAsync(row.Entry.FullPath));
        else Launch(row.Entry.FullPath);
    }

    public IReadOnlyList<string> SelectedPaths() => _selection.Select(r => r.Entry.FullPath).ToArray();

    public void Drop(IReadOnlyList<string> paths, bool move) => Run(() => DropAsync(paths, move));

    public bool CanWriteHere => CurrentPath.Length > 0 && _listing.Error is null;

    /// <summary>Puts a message in the status bar. For failures the window itself detects.</summary>
    public void Report(string message) => Notice = message;

    /// <summary>Called when the window closes: nothing may be left walking the disk afterwards.</summary>
    public void Shutdown()
    {
        _load?.Cancel();
        _folderSizes.Dispose();
    }

    private Task DropAsync(IReadOnlyList<string> paths, bool move)
    {
        if (!CanWriteHere)
        {
            Notice = "This folder cannot be written to.";
            return Task.CompletedTask;
        }

        var incoming = paths
            .Where(p => !string.Equals(Path.GetDirectoryName(p), CurrentPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (incoming.Length == 0)
        {
            Notice = "Those items are already in this folder.";
            return Task.CompletedTask;
        }

        return RunOperationAsync(new OperationRequest
        {
            Kind = move ? OperationKind.Move : OperationKind.Copy,
            Sources = incoming,
            Destination = CurrentPath,
        });
    }

    private string? Notice
    {
        get => _notice;
        set { _notice = value; OnChanged(nameof(WarningText)); }
    }

    private bool HasSelection => _selection.Count > 0;

    /// <summary>A setting that could not be written is said out loud. It still applies this run.</summary>
    private void Remember()
    {
        if (_settings.Save(Settings.DefaultPath) is { } error) Notice = $"Settings not saved: {error}";
    }

    private void Go(string? path)
    {
        if (path is null) return;

        Notice = null;
        Run(() => LoadAsync(path));
    }

    private async Task ReloadAsync()
    {
        if (CurrentPath.Length > 0) await LoadAsync(CurrentPath);
    }

    private async Task LoadAsync(string path)
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        Busy = true;

        try
        {
            var options = new ListOptions { ShowHidden = _showHidden };
            var listing = await DirectoryLister.ListAsync(path, options, cts.Token);

            // The quick-access folders are read once: they are the profile's own folders, they do
            // not come and go while the window is open, and rebuilding them would throw away every
            // size measured so far.
            var sidebar = await Task.Run(
                () => (Free: FreeSpace(path), Quick: _quick.Count == 0 ? Places.Quick() : null, Drives: Places.Drives()),
                cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            _listing = listing;
            if (sidebar.Quick is { } quick) ShowQuick(quick);
            Drives = [.. sidebar.Drives.Select(d => new PlaceViewModel(d))];
            FreeText = sidebar.Free is null ? "" : $"{Format.Size(sidebar.Free)} free";
            Address = path;
            EditingAddress = false;
            Crumbs = BuildCrumbs(path);
            Rebuild();

            OnChanged(nameof(CurrentPath));
            OnChanged(nameof(ParentPath));
            OnChanged(nameof(WarningText));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_load == cts)
            {
                _load = null;
                Busy = false;
            }
            cts.Dispose();
        }
    }

    private void Rebuild()
    {
        var keep = _selection.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<FileEntry> source = _listing.Entries;
        if (_kind != FileKind.All)
            source = source.Where(e => FileKinds.Matches(e, _kind));
        if (_filter.Length > 0)
            source = source.Where(e => e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        var rows = source.Select(e => new EntryViewModel(e, IconSize)).ToArray();
        Array.Sort(rows, Compare);

        Selection = [];
        Rows = rows;

        // Replacing the rows drops the list's selection; put back whatever survived, so a sort,
        // a refresh or a finished operation does not make the user pick the same items again.
        if (keep.Count > 0)
            Reselect?.Invoke(Array.FindAll(rows, r => keep.Contains(r.Entry.FullPath)));

        OnChanged(nameof(Filtering));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ListMessage));
    }

    private int Compare(EntryViewModel x, EntryViewModel y)
    {
        var (a, b) = (x.Entry, y.Entry);
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        var order = _sort switch
        {
            SortKey.Size => Nullable.Compare(a.Size, b.Size),
            SortKey.Kind => string.Compare(x.KindText, y.KindText, StringComparison.CurrentCultureIgnoreCase),
            SortKey.Modified => Nullable.Compare(a.Modified, b.Modified),
            _ => NaturalSort.Instance.Compare(a.Name, b.Name),
        };

        if (_descending) order = -order;
        return order != 0 ? order : NaturalSort.Instance.Compare(a.Name, b.Name);
    }

    private void SortBy(SortKey key)
    {
        if (_sort == key) _descending = !_descending;
        else (_sort, _descending) = (key, false);

        Rebuild();
        OnChanged(nameof(NameHeader));
        OnChanged(nameof(SizeHeader));
        OnChanged(nameof(KindHeader));
        OnChanged(nameof(ModifiedHeader));
    }

    private string Header(string text, SortKey key) =>
        _sort == key ? $"{text}  {(_descending ? '▾' : '▴')}" : text;

    /// <summary>Both filters at once: two ways to be hiding things, one way to stop.</summary>
    private void ClearFilters()
    {
        _filter = "";
        _kind = FileKind.All;

        OnChanged(nameof(Filter));
        OnChanged(nameof(KindFilter));
        OnChanged(nameof(KindFilterText));
        OnChanged(nameof(KindFiltered));
        Rebuild();
    }

    private void ShowQuick(IReadOnlyList<Place> places)
    {
        Quick = [.. places.Select(p => new PlaceViewModel(p))];

        // Everything but the profile itself. Measured on this machine it is 1.1 million files and
        // 36 seconds, nearly all of it AppData, which is not a price for opening a window — so it
        // stays blank until the user asks for it.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var place in Quick.Where(p => p.Available && !Same(p.Path, home)))
            Measure(place);
    }

    private void MeasureQuick()
    {
        foreach (var place in Quick.Where(p => p.Available)) Measure(place);
    }

    private void Measure(PlaceViewModel place)
    {
        place.Measuring();
        _folderSizes.Add(place.Path);
    }

    private void Measured(FolderSizeResult result)
    {
        if (Quick.FirstOrDefault(p => Same(p.Path, result.Path)) is { } place) place.Measured(result);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void PutOnClipboard(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        Notice = ClipboardFiles.Put(paths, cut) ? null : "The clipboard is in use by another program.";
        RefreshClipboardState();
    }

    private Task PasteAsync()
    {
        var (paths, cut) = ClipboardFiles.Get();
        if (paths.Count == 0) return Task.CompletedTask;

        return RunOperationAsync(new OperationRequest
        {
            Kind = cut ? OperationKind.Move : OperationKind.Copy,
            Sources = paths,
            Destination = CurrentPath,
        }, clearClipboard: cut);
    }

    private Task DeleteAsync(bool permanent) =>
        RunOperationAsync(new OperationRequest
        {
            Kind = OperationKind.Delete,
            Sources = SelectedPaths(),
            Permanent = permanent,
        });

    private Task RenameAsync()
    {
        var row = _selection[0];
        var name = AskName?.Invoke("Rename", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return Task.CompletedTask;

        return RunOperationAsync(new OperationRequest
        {
            Kind = OperationKind.Rename,
            Sources = [row.Entry.FullPath],
            NewName = name,
        });
    }

    private Task NewFolderAsync()
    {
        var name = AskName?.Invoke("New folder", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;

        return RunOperationAsync(new OperationRequest
        {
            Kind = OperationKind.CreateFolder,
            Destination = CurrentPath,
            NewName = name,
        });
    }

    private Task NewAsync(NewItem item) =>
        item.Template is null ? NewFolderAsync() : NewFileAsync(item.Template);

    /// <summary>
    /// Explorer's flow: the file appears under the name its type advertises and the name is the
    /// next thing you are asked for, rather than before there is anything to name.
    /// </summary>
    private async Task NewFileAsync(NewFileTemplate template)
    {
        string path;
        try
        {
            path = ShellNew.Create(CurrentPath, template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            Notice = ex.Message;
            return;
        }

        await ReloadAsync();

        var row = Rows.FirstOrDefault(r => Same(r.Entry.FullPath, path));
        if (row is null)
        {
            Notice = $"\"{Path.GetFileName(path)}\" was created — the filter is hiding it.";
            return;
        }

        Reselect?.Invoke([row]);
        if (_selection.Count == 1) await RenameAsync();
    }

    private async Task RunOperationAsync(OperationRequest request, bool clearClipboard = false)
    {
        var result = await _ops.RunAsync(request, OwnerHandle);

        var notice =
            result.Error is not null ? result.Error :
            result.Failed.Count > 0 ? $"{result.Failed.Count} item(s) failed: {result.Failed[0].Message}" :
            result.Cancelled ? Cancellation(result.Succeeded) : null;

        if (clearClipboard && result.Ok)
        {
            ClipboardFiles.Clear();
            RefreshClipboardState();
        }

        // Message last, so the outcome of the operation is what the user is left looking at.
        await ReloadAsync();
        Notice = notice;
    }

    private static string Cancellation(int succeeded) =>
        succeeded > 0 ? $"Cancelled after {succeeded} item(s)." : "Cancelled.";

    private void OpenSelection()
    {
        if (_selection.Count == 1)
        {
            Open(_selection[0]);
            return;
        }

        foreach (var row in _selection.Where(r => !r.Entry.IsDirectory))
            Launch(row.Entry.FullPath);
    }

    private void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notice = ex.Message;
        }
    }

    private void Run(Func<Task> work) => _ = Guarded(work);

    private async Task Guarded(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Notice = ex.Message; }
    }

    private static IReadOnlyList<Crumb> BuildCrumbs(string path)
    {
        var crumbs = new List<Crumb>();
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            crumbs.Insert(0, new Crumb(dir.Parent is null ? dir.FullName.TrimEnd(Path.DirectorySeparatorChar) : dir.Name, dir.FullName));

        return crumbs;
    }

    private static string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            var full = Path.GetFullPath(expanded);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
