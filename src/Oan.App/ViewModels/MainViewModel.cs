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

public enum SortKey { Name, Size, Kind, Modified }

/// <summary>
/// An entry of the New submenu; a null template is the folder. It carries the command because a
/// menu inside a popup has no ancestor to reach the view model through.
/// </summary>
public sealed record NewItem(string Label, string? Gesture, ICommand Command, NewFileTemplate? Template);

public sealed class MainViewModel : Observable
{
    private readonly IFileOperations _ops;
    private readonly Settings _settings;
    private CancellationTokenSource? _load;
    /// <summary>Hits are appended in batches: one notification per hit would spend the whole
    /// search in layout on a folder that returns thousands.</summary>
    private const int BatchSize = 64;

    // Ours, kept below the shell's own numbering so a pick says whose it is.
    private const int MountId = 1;
    private const int EjectId = 2;

    private DirectoryListing _listing = new() { Path = "", Entries = [] };
    private IReadOnlyList<EntryViewModel> _rows = [];
    private IReadOnlyList<EntryViewModel> _selection = [];
    private IReadOnlyList<Crumb> _crumbs = [];
    private IReadOnlyList<NewItem> _newItems;
    private string _address = "";
    private string _filter = "";
    private string _freeText = "";
    private string? _notice;
    private bool _busy;
    private bool _showHidden;
    private bool _canPaste;
    private bool _editingAddress;
    private FileKind _kind = FileKind.All;

    public MainViewModel(IFileOperations ops, Settings settings, MountsViewModel? mounts = null)
    {
        _ops = ops;
        _settings = settings;
        var look = new AppearanceViewModel(settings, Remember);
        look.IconSizeChanged = () => { foreach (var row in Rows) row.SetIconSize(look.IconSize); };
        Look = look;
        Mounts = mounts ?? new MountsViewModel();
        Mounts.Bind(path => Run(() => NavigateAsync(path)), Report, () => CurrentPath);

        BackCommand = new RelayCommand(() => Go(Tabs.Active.History.Back()), () => Tabs.Active.History.CanGoBack);
        ForwardCommand = new RelayCommand(() => Go(Tabs.Active.History.Forward()), () => Tabs.Active.History.CanGoForward);

        // Switching tabs loads without recording: that folder is already in this tab's history.
        Tabs.Switched += path => Run(() => LoadAsync(path));
        UpCommand = new RelayCommand(GoUp, () => Archive.Active || ParentPath is not null);
        RefreshCommand = new RelayCommand(() => { Notice = null; Run(ReloadAsync); }, () => CurrentPath.Length > 0);

        OpenPlaceCommand = new RelayCommand(p => Run(() => NavigateAsync((string)p!)));
        GoToCommand = new RelayCommand(p => Run(() => NavigateAsync(p as string ?? Address)));
        SortCommand = new RelayCommand(p => Sort.By((SortKey)p!));
        Sort.Changed += Rebuild;

        CopyCommand = new RelayCommand(() => PutOnClipboard(cut: false), () => HasSelection);
        CutCommand = new RelayCommand(() => PutOnClipboard(cut: true), () => HasSelection && CanWriteHere);
        PasteCommand = new RelayCommand(() => Run(PasteAsync), () => CanPaste && CanWriteHere);
        RenameCommand = new RelayCommand(() => Run(RenameAsync), () => _selection.Count == 1 && CanWriteHere);
        DeleteCommand = new RelayCommand(() => Run(() => DeleteAsync(permanent: false)), () => HasSelection && CanWriteHere);
        DeletePermanentlyCommand = new RelayCommand(() => Run(() => DeleteAsync(permanent: true)), () => HasSelection && CanWriteHere);
        NewFolderCommand = new RelayCommand(() => Run(NewFolderAsync), () => CanWriteHere);
        NewItemCommand = new RelayCommand(p => Run(() => NewAsync((NewItem)p!)), _ => CanWriteHere);
        OpenSelectionCommand = new RelayCommand(OpenSelection, () => HasSelection);
        BackdropCommand = new RelayCommand(p => Look.Backdrop = (Backdrop)p!);
        KindCommand = new RelayCommand(p => KindFilter = (FileKind)p!);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => Filtering);
        SearchCommand = new RelayCommand(() => Run(SearchAsync), () => _filter.Trim().Length > 0 && CurrentPath.Length > 0);
        StopSearchCommand = new RelayCommand(Search.Stop, () => Search.Running);
        EndSearchCommand = new RelayCommand(EndSearch, () => Search.Active);
        Search.Changed += Announce;

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

    /// <summary>The disk images OAN has attached, and the only way it opens one.</summary>
    public MountsViewModel Mounts { get; }

    public SearchViewModel Search { get; } = new();
    public ArchiveViewModel Archive { get; } = new();
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

    /// <summary>Enter in the search box: walk the tree instead of filtering the loaded rows.</summary>
    public ICommand SearchCommand { get; }
    public ICommand StopSearchCommand { get; }
    public ICommand EndSearchCommand { get; }

    /// <summary>The Size column for this listing's folders; see the class for why it is off.</summary>
    public FolderSizesViewModel FolderSizes { get; } = new();

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


    public string StatusText
    {
        get
        {
            if (Search.Active)
            {
                var running = Search.Running;
                var head = running ? $"Searching… {Rows.Count} found" : $"{Rows.Count} found";
                var seen = $"  ·  {Search.Tally.FilesSeen:N0} files in {Search.Tally.FoldersSeen:N0} folders";
                return head + seen + (_selection.Count > 0 ? $"  ·  {_selection.Count} selected" : "");
            }

            if (Archive.Active)
            {
                var inside = $"{Rows.Count} item{(Rows.Count == 1 ? "" : "s")} in this archive — read only";
                return _selection.Count > 0 ? $"{inside}  ·  {_selection.Count} selected" : inside;
            }

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
            if (Search.Error is not null) return Search.Error;

            // Un rezultat incomplet nu se prezinta ca total.
            if (Search.Active && !Search.Tally.Complete)
            {
                var parts = new List<string>();
                if (Search.Tally.Denied > 0) parts.Add($"{Search.Tally.Denied} folder(s) denied");
                if (Search.Tally.Unreadable > 0) parts.Add($"{Search.Tally.Unreadable} file(s) unreadable");
                if (Search.Tally.TooLarge > 0) parts.Add($"{Search.Tally.TooLarge} too large to scan");
                if (parts.Count > 0) return "Results are a floor, not a total — " + string.Join(", ", parts);
            }

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
            if (Search.Active) return Search.Running ? null : "Nothing matched.";
            return Filtering ? "No items match the filter." : "This folder is empty.";
        }
    }

    public void Start() =>
        Run(() => NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

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
            // Inside one already: a folder walks deeper, a file is extracted to temp and opened
            // read-only, because nothing written back would reach the archive.
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
            if (await Archive.OpenAsync(archivePath) is { } error) Notice = error;
            else ShowArchiveRows();
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
        Selection = [];
        Rows = [.. Archive.Rows().Select(e => new EntryViewModel(e, Look.IconSize, measurable: false))];
        Address = Archive.DisplayPath;
        Crumbs = Paths.Crumbs(Archive.DisplayPath);

        OnChanged(nameof(CurrentPath));
        OnChanged(nameof(ParentPath));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ListMessage));
        OnChanged(nameof(CanWriteHere));
    }

    public IReadOnlyList<string> SelectedPaths() => _selection.Select(r => r.Entry.FullPath).ToArray();

    /// <summary>
    /// The selection as the shell wants it. The directory flag comes from the row we already
    /// have, so building the menu never has to go back to disk to ask what these things are —
    /// which on this machine is a question that can hang.
    /// </summary>
    public IReadOnlyList<ShellItem> SelectedShellItems() =>
        _selection.Select(r => new ShellItem(r.Entry.FullPath, r.Entry.IsDirectory)).ToArray();

    public void Drop(IReadOnlyList<string> paths, bool move) => Run(() => DropAsync(paths, move));

    /// <summary>
    /// Nothing inside an archive can be written. Asked by every command that would create,
    /// rename or delete, so they come up disabled rather than enabled and then apologising.
    /// </summary>
    public bool CanWriteHere => !Archive.Active && CurrentPath.Length > 0 && _listing.Error is null;

    /// <summary>Puts a message in the status bar. For failures the window itself detects.</summary>
    public void Report(string message) => Notice = message;

    /// <summary>Called when the window closes: nothing may be left walking the disk afterwards.</summary>
    public void Shutdown()
    {
        _load?.Cancel();
        FolderSizes.Dispose();
        Places.Dispose();

        // An image still attached after OAN exits is litter on the user's machine.
        Mounts.Dispose();
    }


    /// <summary>
    /// What OAN puts at the top of the system menu. Mount is here and not only on double-click
    /// because the right-click menu is where people look for it, and on this machine the system's
    /// own mount entry belongs to a handler that hangs.
    /// </summary>
    public IReadOnlyList<OwnMenuItem> OwnMenuItems()
    {
        var images = _selection.Where(r => MountsViewModel.IsImage(r.Entry.FullPath)).ToArray();
        if (images.Length == 0) return [];

        var mounted = images.All(r => Mounts.IsMounted(r.Entry.FullPath));
        return [new OwnMenuItem(mounted ? EjectId : MountId, mounted ? "Eject" : "Mount")];
    }

    public void RunOwnMenuItem(int id)
    {
        var images = _selection
            .Where(r => MountsViewModel.IsImage(r.Entry.FullPath))
            .Select(r => r.Entry.FullPath)
            .ToArray();

        foreach (var image in images)
        {
            if (id == MountId) Run(() => Mounts.OpenAsync(image));
            else if (id == EjectId) Mounts.UnmountCommand.Execute(image);
        }
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

        // A drag that ends where it began is a cancelled gesture, not a failure. This is the one
        // no-op the window is allowed to pass over in silence: the user asked for nothing, and an
        // orange warning for a slipped mouse reads as something having gone wrong.
        if (incoming.Length == 0) return Task.CompletedTask;

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

    public bool HasSelection => _selection.Count > 0;

    /// <summary>A setting that could not be written is said out loud. It still applies this run.</summary>
    private void Remember()
    {
        if (_settings.Save(Settings.DefaultPath) is { } error) Notice = $"Settings not saved: {error}";
    }

    /// <summary>Up inside an archive walks out of its folders first, then out of the archive.</summary>
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

            // The quick-access folders are read once: they are the profile's own folders, they do
            // not come and go while the window is open, and rebuilding them would throw away every
            // size measured so far.
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


    /// <summary>
    /// A recursive search from the current folder, as opposed to the box's filter, which only
    /// hides rows already loaded. Hits arrive while the walk is still going: a search that only
    /// answers at the end is unusable on a large tree.
    /// </summary>
    private async Task SearchAsync()
    {
        if (Search.Build(_filter, CurrentPath, ShowHidden) is not { } query) return;

        var run = Search.Begin();
        Selection = [];
        Rows = [];

        var engine = new FileSearch(query.SearchesContent ? new ContentMatcher(query) : null);
        var batch = new List<EntryViewModel>(BatchSize);
        string? error = null;

        try
        {
            await foreach (var hit in engine.RunAsync(query, run.Token).ConfigureAwait(true))
            {
                batch.Add(Row(hit));
                if (batch.Count >= BatchSize) Flush(batch, engine);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            if (Search.Finish(run, engine.Tally, error)) Flush(batch, engine);
            run.Dispose();
        }
    }

    private EntryViewModel Row(SearchHit hit) => new(
        new FileEntry
        {
            Name = hit.Name,
            FullPath = hit.FullPath,
            Attributes = hit.Attributes,
            Size = hit.Size,
            Modified = hit.Modified,
        },
        Look.IconSize);

    private void Flush(List<EntryViewModel> batch, FileSearch engine)
    {
        if (batch.Count > 0)
        {
            Rows = [.. Rows, .. batch];
            batch.Clear();
        }

        Search.Progress(engine.Tally);
    }

    /// <summary>Leaves results mode and reloads the folder the search started from.</summary>
    private void EndSearch()
    {
        if (!Search.Active) return;

        Search.Leave();
        Filter = "";
        Run(ReloadAsync);
    }

    private void Announce()
    {
        OnChanged(nameof(StatusText));
        OnChanged(nameof(WarningText));
        OnChanged(nameof(ListMessage));
    }


    /// <summary>
    /// What opening a folder ends. Results belong to the folder the search started from, and the
    /// typed filter is a question about the folder you were in — carried over, the next one reads
    /// as empty with nothing on screen to explain why.
    /// </summary>
    private void LeaveSearch()
    {
        Search.Leave();

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

        OnChanged(nameof(CurrentPath));
        OnChanged(nameof(ParentPath));
        OnChanged(nameof(WarningText));
    }

    private void Rebuild()
    {
        var keep = _selection.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<FileEntry> source = _listing.Entries;
        if (_kind != FileKind.All)
            source = source.Where(e => FileKinds.Matches(e, _kind));
        if (_filter.Length > 0)
            source = source.Where(e => e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        var rows = source.Select(e => new EntryViewModel(e, Look.IconSize)).ToArray();
        Array.Sort(rows, Sort.Compare);

        Selection = [];
        Rows = rows;
        FolderSizes.Show(rows);

        // Replacing the rows drops the list's selection; put back whatever survived, so a sort,
        // a refresh or a finished operation does not make the user pick the same items again.
        if (keep.Count > 0)
            Reselect?.Invoke(Array.FindAll(rows, r => keep.Contains(r.Entry.FullPath)));

        OnChanged(nameof(Filtering));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ListMessage));
    }

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
