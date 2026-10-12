using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Archives;
using Oan.Core.Model;
using Oan.Core.Search;
using Oan.Core.Navigation;
using Oan.Core.Operations;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

public enum SortKey { Name, Size, Kind, Modified, Created, Attributes }

/// <summary>An entry of the New submenu; a null template is the folder. A menu in a popup cannot reach the view model, so it carries its command.</summary>
public sealed record NewItem(string Label, string? Gesture, ICommand Command, NewFileTemplate? Template);

public sealed class MainViewModel : Observable, IFileOpsHost
{
    private readonly IFileOperations _ops;
    private readonly Settings _settings;
    private readonly FileOps _files;
    private readonly ArchiveOps _archives;
    private CancellationTokenSource? _load;
    /// <summary>Hits arrive in batches: one notification per hit spends the search in layout.</summary>

    private DirectoryListing _listing = new() { Path = "", Entries = [] };

    private readonly SearchRuns _searches;
    private IReadOnlyList<EntryViewModel> _rows = [];
    private IReadOnlyList<EntryViewModel> _selection = [];
    private IReadOnlyList<Crumb> _crumbs = [];
    private IReadOnlyList<NewItem> _newItems;
    private string _address = "";
    private string _filter = "";
    private string _freeText = "";
    private string? _notice;
    private string? _info;
    private bool _busy;
    private bool _showHidden;
    private bool _canPaste;
    private bool _editingAddress;
    private FileKind _kind = FileKind.All;

    public MainViewModel(IFileOperations ops, Settings settings, MountsViewModel? mounts = null)
    {
        _ops = ops;
        _settings = settings;
        _files = new FileOps(ops, this);
        _archives = new ArchiveOps(this, Archive);
        Archive.AskPassword = name => AskPassword?.Invoke(name);
        var look = new AppearanceViewModel(settings, Remember);
        FolderSizes = new FolderSizesViewModel(settings, Remember);
        look.IconSizeChanged = () => { foreach (var row in Rows) row.SetIconSize(look.IconSize); };
        Look = look;
        Mounts = mounts ?? new MountsViewModel();
        Mounts.Bind(path => Run(() => NavigateAsync(path)), Report, () => CurrentPath);
        OwnMenu = new OwnMenu(Mounts, _archives, new ViewMenu(look), () => _selection, Run);

        BackCommand = new RelayCommand(() => Go(Tabs.Active.History.Back()), () => Tabs.Active.History.CanGoBack);
        ForwardCommand = new RelayCommand(() => Go(Tabs.Active.History.Forward()), () => Tabs.Active.History.CanGoForward);

        // Switching tabs loads without recording: that folder is already in this tab's history.
        Tabs.Switched += path => Run(() => LoadAsync(path));
        UpCommand = new RelayCommand(GoUp, () => Archive.Active || ParentPath is not null);
        RefreshCommand = new RelayCommand(() => { Notice = null; Run(ReloadAsync); }, () => CurrentPath.Length > 0);

        OpenPlaceCommand = new RelayCommand(p => Run(() => NavigateAsync((string)p!)));
        GoToCommand = new RelayCommand(p => Run(() => NavigateAsync(p as string ?? Address)));
        SortCommand = new RelayCommand(p => Sort.By((SortKey)p!));
        Sort.Changed += Reorder;
        FolderSizes.Settled += () =>
        {
            if (Sort.Key == SortKey.Size && Sort.Reorder(Rows) is { } bySize) Rows = bySize;
        };

        CopyCommand = new RelayCommand(() => _files.PutOnClipboard(cut: false), () => CanCopyOut);
        CopyPathCommand = new RelayCommand(_files.CopyPaths, () => CanCopyOut);
        CutCommand = new RelayCommand(() => _files.PutOnClipboard(cut: true), () => HasSelection && CanWriteHere);
        PasteCommand = new RelayCommand(() => Run(_files.PasteAsync), () => CanPaste && CanWriteHere);
        RenameCommand = new RelayCommand(() => Run(_files.RenameAsync), () => _selection.Count == 1 && CanWriteHere);
        DeleteCommand = new RelayCommand(() => Run(() => _files.DeleteAsync(permanent: false)), () => HasSelection && CanWriteHere);
        DeletePermanentlyCommand = new RelayCommand(() => Run(() => _files.DeleteAsync(permanent: true)), () => HasSelection && CanWriteHere);
        NewFolderCommand = new RelayCommand(() => Run(_files.NewFolderAsync), () => CanWriteHere);
        NewItemCommand = new RelayCommand(p => Run(() => _files.NewAsync((NewItem)p!)), _ => CanWriteHere);
        OpenSelectionCommand = new RelayCommand(OpenSelection, () => HasSelection);
        ExtractCommand = new RelayCommand(() => Run(() => _archives.ExtractAsync(ownFolder: true)), () => Archive.Active || SelectionHasArchive);
        CompressCommand = new RelayCommand(() => Run(_archives.CompressAsync), () => HasSelection && CanWriteHere);
        BackdropCommand = new RelayCommand(p => Look.Backdrop = (Backdrop)p!);
        KindCommand = new RelayCommand(p => KindFilter = (FileKind)p!);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => Filtering);
        SearchCommand = new RelayCommand(() => Run(SearchAsync), () => _filter.Trim().Length > 0 && CurrentPath.Length > 0);
        StopSearchCommand = new RelayCommand(Search.Stop, () => Search.Running);
        EndSearchCommand = new RelayCommand(EndSearch, () => Search.Active);
        _searches = new SearchRuns(Search);
        Search.Changed += Announce;

        _newItems = [new NewItem("Folder", "Ctrl+Shift+N", NewItemCommand, null)];
        _ = LoadTemplatesAsync();
    }

    /// <summary>The registry scan is slow, so it runs once, off the UI thread.</summary>
    private async Task LoadTemplatesAsync()
    {
        var templates = await Task.Run(ShellNew.Templates);
        NewItems = [.. _newItems, .. templates.Select(t => new NewItem(t.Label, null, NewItemCommand, t))];
    }

    /// <summary>Parents the shell's progress and conflict dialogs. Set by the window.</summary>
    public nint OwnerHandle { get; set; }

    /// <summary>(title, suggestion) to a name, or null on cancel. Set by the window.</summary>
    public Func<string, string, string?>? AskName { get; set; }

    /// <summary>The archive's name to its password, or null. Set by the window.</summary>
    public Func<string, string?>? AskPassword { get; set; }

    /// <summary>Re-selects rows after a rebuild replaced them. Set by the window.</summary>
    public Action<IReadOnlyList<EntryViewModel>>? Reselect { get; set; }


    public MountsViewModel Mounts { get; }

    public SearchViewModel Search { get; } = new();
    public ArchiveViewModel Archive { get; } = new();
    public OwnMenu OwnMenu { get; }
    public SortViewModel Sort { get; } = new();
    public TabsViewModel Tabs { get; } = new();
    public AppearanceViewModel Look { get; }

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenPlaceCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeletePermanentlyCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand NewItemCommand { get; }
    public ICommand OpenSelectionCommand { get; }
    public ICommand ExtractCommand { get; }
    public ICommand CompressCommand { get; }
    public ICommand BackdropCommand { get; }
    public ICommand KindCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    public ICommand SearchCommand { get; }
    public ICommand StopSearchCommand { get; }
    public ICommand EndSearchCommand { get; }

    public FolderSizesViewModel FolderSizes { get; }

    public IReadOnlyList<EntryViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }
    public IReadOnlyList<Crumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }
    public PlacesViewModel Places { get; } = new();
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
        set
        {
            if (!Set(ref _filter, value)) return;

            // During a search this box is the query: rebuilding on it would drop the results per keystroke.
            if (Search.Active) OnChanged(nameof(Filtering));
            else Rebuild();
        }
    }

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

    public string KindFilterText => _kind == FileKind.All ? "" : _kind.ToString();

    public bool KindFiltered => _kind != FileKind.All;

    public bool Filtering => _filter.Length > 0 || _kind != FileKind.All;

    public bool ShowHidden
    {
        get => _showHidden;
        set { if (Set(ref _showHidden, value)) Run(ReloadAsync); }
    }

    public bool CanPaste { get => _canPaste; private set => Set(ref _canPaste, value); }

    public IReadOnlyList<EntryViewModel> Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            Mounts.Selected(SelectedPaths());
            OnChanged();
            OnChanged(nameof(HasSelection));
            OnChanged(nameof(StatusText));
        }
    }

    public string StatusText => Search.Active || Archive.Active || !Busy
        ? StatusTexts.Status(Search, Archive.Active, CurrentPath, Rows.Count, _listing.Entries.Count, Filtering, _selection)
        : "Reading folder…";

    public string? WarningText => _notice ?? StatusTexts.Warning(Search, _listing);

    public string? ListMessage => StatusTexts.Empty(Search, _listing, Rows.Count, Filtering);

    /// <summary>Where a window opened from another one begins. Null is the user's own folder.</summary>
    public string? StartAt { get; init; }

    public void Start() =>
        Run(() => NavigateAsync(StartAt ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public async Task NavigateAsync(string path)
    {
        var target = Paths.Resolve(path);
        if (target is null)
        {
            Notice = $"\"{path}\" is not a folder we can open.";
            return;
        }

        Notice = null;
        Tabs.Active.History.Visit(target);
        await LoadAsync(target);
    }

    public void RefreshClipboardState() => CanPaste = ClipboardFiles.HasFiles();

    public void Open(EntryViewModel row)
    {
        if (Archive.Active)
        {
            // A folder walks deeper; a file is extracted to temp and opened read-only.
            if (row.Entry.IsDirectory) OpenInsideArchive(row);
            else Run(() => OpenFromArchiveAsync(row));
            return;
        }

        if (ArchiveReader.Detect(row.Entry.FullPath) is not Oan.Core.Archives.ArchiveKind.None && !row.Entry.IsDirectory)
        {
            Run(() => EnterArchiveAsync(row.Entry.FullPath));
            return;
        }

        if (row.Entry.IsDirectory) Run(() => NavigateAsync(row.Entry.FullPath));
        // Never the shell's default verb for an image: that verb is the one that hangs.
        else if (MountsViewModel.IsImage(row.Entry.FullPath)) Run(() => Mounts.OpenAsync(row.Entry.FullPath));
        else Launch(row.Entry.FullPath);
    }

    private async Task EnterArchiveAsync(string archivePath)
    {
        Busy = true;
        try
        {
            Notice = await Archive.OpenAsync(archivePath);
            if (Notice is null) ShowArchiveRows();
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenInsideArchive(EntryViewModel row)
    {
        Archive.Descend(Archive.Inside(row.Entry.FullPath));
        ShowArchiveRows();
    }

    private async Task OpenFromArchiveAsync(EntryViewModel row)
    {
        var (file, error) = await Archive.CopyOutAsync(Archive.Inside(row.Entry.FullPath));
        if (file is null) { Notice = error; return; }

        Notice = "Opened a read-only copy — changes will not go back into the archive.";
        Launch(file);
    }

    private void ShowArchiveRows()
    {
        var rows = Archive.Rows().Select(e => new EntryViewModel(e, Look.IconSize, measurable: false)).ToArray();
        Array.Sort(rows, Sort.Compare);

        Selection = [];
        Rows = rows;
        Address = Archive.DisplayPath;
        Crumbs = Paths.Crumbs(Archive.DisplayPath);

        OnChanged(nameof(CurrentPath));
        OnChanged(nameof(ParentPath));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ListMessage));
        OnChanged(nameof(CanWriteHere));
    }

    public IReadOnlyList<string> SelectedPaths() => _selection.Select(r => r.Entry.FullPath).ToArray();

    /// <summary>The directory flag comes from the row, so building the menu never goes back to disk, which can hang.</summary>
    public IReadOnlyList<ShellItem> SelectedShellItems() =>
        _selection.Select(r => new ShellItem(r.Entry.FullPath, r.Entry.IsDirectory)).ToArray();

    public void Drop(IReadOnlyList<string> paths, bool move, string? into = null) =>
        Run(() => _files.DropAsync(paths, move, into));

    /// <summary>Rows inside an archive have no real path: on the clipboard they fail wherever they are pasted.</summary>
    public bool CanCopyOut => HasSelection && !Archive.Active;

    /// <summary>What every create, rename and delete command asks, so they come up disabled.</summary>
    public bool CanWriteHere => !Archive.Active && CurrentPath.Length > 0 && _listing.Error is null;

    public void Report(string message) => Notice = message;

    public void Shutdown()
    {
        _load?.Cancel();
        FolderSizes.Dispose();
        Places.Dispose();

        Mounts.Dispose();
    }

    private bool SelectionHasArchive => CanWriteHere && _selection.Any(ArchiveOps.IsArchive);

    private string? Notice
    {
        get => _notice;
        set { (_notice, _info) = (value, null); OnChanged(nameof(WarningText)); OnChanged(nameof(InfoText)); }
    }

    public bool HasSelection => _selection.Count > 0;

    private void Remember()
    {
        if (_settings.Save(Settings.DefaultPath) is { } error) Notice = $"Settings not saved: {error}";
    }

    /// <summary>Inside an archive, up leaves its folders first, then the archive.</summary>
    private void GoUp()
    {
        if (!Archive.Active) { Go(History.Parent(CurrentPath)); return; }

        if (Archive.Ascend()) { ShowArchiveRows(); return; }

        var archive = Archive.ArchivePath!;
        Archive.Leave();
        Run(() => NavigateAsync(Path.GetDirectoryName(archive) ?? archive));
    }

    private void Go(string? path)
    {
        if (path is null) return;

        Notice = null;
        Run(() => LoadAsync(path));
    }

    string? IFileOpsHost.Notice { set => Notice = value; }
    string? IFileOpsHost.Info { set { _info = value; OnChanged(nameof(InfoText)); } }
    public string? InfoText => _info;

    Task IFileOpsHost.ReloadAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        if (CurrentPath.Length > 0) await LoadAsync(CurrentPath);
    }

    private async Task LoadAsync(string path)
    {
        LeaveSearch();
        Archive.Leave();
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        Busy = true;

        try
        {
            var options = new ListOptions { ShowHidden = _showHidden };
            var listing = await DirectoryLister.ListAsync(path, options, cts.Token);

            // Quick access is read once: rebuilding it would throw away every size measured so far.
            var sidebar = await Task.Run(
                () => (Free: Paths.FreeSpace(path), Quick: Places.NeedsQuick ? Oan.Core.FileSystem.Places.Quick() : null,
                       Drives: Oan.Core.FileSystem.Places.Drives()),
                cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            Show(path, listing, sidebar.Quick, sidebar.Drives, sidebar.Free);
        Tabs.Visited(path);
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

    /// <summary>A recursive search from here. Hits arrive while the walk is still going.</summary>
    private Task SearchAsync() =>
        _searches.RunAsync(_filter, CurrentPath, ShowHidden, Look.IconSize, () =>
        {
            Selection = [];
            Rows = FoundRows();
        });

    private void EndSearch()
    {
        if (!Search.Active) return;

        Search.Leave();
        _searches.Forget();
        Filter = "";
        Run(ReloadAsync);
    }

    private void Announce()
    {
        OnChanged(nameof(StatusText));
        OnChanged(nameof(WarningText));
        OnChanged(nameof(ListMessage));
    }

    /// <summary>Opening a folder ends the search and clears the typed filter, or the new folder reads as empty for no visible reason.</summary>
    private void LeaveSearch()
    {
        Search.Leave();
        _searches.Forget();

        if (_filter.Length == 0) return;

        // The field, not the property: the setter rebuilds against the listing still on screen.
        _filter = "";
        OnChanged(nameof(Filter));
        OnChanged(nameof(Filtering));
    }

    private void Show(string path, DirectoryListing listing, IReadOnlyList<Place>? quick,
                      IReadOnlyList<Place> drives, long? free)
    {
        _listing = listing;
        if (quick is not null) Places.ShowQuick(quick);
        Places.ShowDrives(drives, root => Mounts.At(root)?.IsoPath);
        Places.ShowFree(free);
        Address = path;
        EditingAddress = false;
        Crumbs = Paths.Crumbs(path);
        Places.Track(CurrentPath);
        Rebuild();
        FolderSizes.Deepen();

        OnChanged(nameof(CurrentPath));
        OnChanged(nameof(ParentPath));
        OnChanged(nameof(WarningText));
    }

    private void Rebuild()
    {
        var keep = _selection.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = Search.Active ? FoundRows() : ListingRows();

        // Before the sort: this puts the known folder sizes back, and sorting without them sorts nulls.
        FolderSizes.Show(CurrentPath, rows);
        Array.Sort(rows, Sort.Compare);

        Selection = [];
        Rows = rows;

        // Replacing the rows drops the selection; put back whatever survived.
        if (keep.Count > 0)
            Reselect?.Invoke(Array.FindAll(rows, r => keep.Contains(r.Entry.FullPath)));

        OnChanged(nameof(Filtering));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ListMessage));
    }

    /// <summary>
    /// The search results, not the listing: sort and filter must not replace them with the folder.
    /// The rows are reused (rebuilding refetches every icon); the typed text is the query, so only the kind filter applies.
    /// </summary>
    private EntryViewModel[] FoundRows() =>
        _kind == FileKind.All
            ? [.. _searches.Found]
            : [.. _searches.Found.Where(r => FileKinds.Matches(r.Entry, _kind))];

    private EntryViewModel[] ListingRows()
    {
        IEnumerable<FileEntry> source = _listing.Entries;
        if (_kind != FileKind.All)
            source = source.Where(e => FileKinds.Matches(e, _kind));
        if (_filter.Length > 0)
            source = source.Where(e => e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        return [.. source.Select(e => new EntryViewModel(e, Look.IconSize))];
    }

    /// <summary>Sorts whichever rows are showing: inside an archive the listing is the archive file's folder, not its contents.</summary>
    private void Reorder()
    {
        if (Archive.Active) ShowArchiveRows();
        else Rebuild();
    }

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

    private void OpenSelection()
    {
        if (_selection.Count == 1)
        {
            Open(_selection[0]);
            return;
        }

        foreach (var row in _selection.Where(r => !r.Entry.IsDirectory)) Open(row);
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
}
