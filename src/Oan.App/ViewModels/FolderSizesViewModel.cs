using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Sizes;

namespace Oan.App.ViewModels;

/// <summary>
/// Folder sizes for the rows of one listing: measured as you arrive, kept while you are there,
/// and re-measured when the disk changes underneath them.
///
/// None of it is cheap, and none of it is guesswork — there is no way to know a folder's size
/// without administrator, so this walks. Threaded, the whole user profile is 7.4 seconds and 1.1
/// million files, D:\dev is half a second. See docs/folder-size-baseline.md.
///
/// That cost is why re-measuring is rationed by what the last measurement cost: a folder that
/// took 20ms is refreshed almost as soon as it changes, and one that took six seconds is not
/// touched again for two minutes however much it churns. Without that, watching a tree like
/// AppData — which writes constantly — would put the walk in a permanent loop.
/// </summary>
public sealed class FolderSizesViewModel : Observable, IDisposable
{
    /// <summary>How many times its own cost a folder is left alone for after being measured.</summary>
    private const int Quiet = 20;

    private static readonly TimeSpan Least = TimeSpan.FromSeconds(2);

    /// <summary>How long a remembered size is shown without a second thought before it is quietly
    /// checked again. Only matters for folders left behind, where nothing was watching.</summary>
    private static readonly TimeSpan Trust = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, EntryViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, TimeSpan Took)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stale = new(StringComparer.OrdinalIgnoreCase);
    private readonly SizeQueue _queue;
    private readonly FolderChanges _changes;
    private readonly Timer _retry;
    private readonly Timer _settle;
    private readonly Settings _settings;
    private readonly Action _remember;
    private string _folder = "";
    private CancellationTokenSource? _deep;
    private SizeTree? _tree;

    public FolderSizesViewModel(Settings settings, Action remember)
    {
        _settings = settings;
        _remember = remember;

        // Capacity well past a big listing: a cache smaller than the folder you are looking at
        // evicts its own answers and re-walks them the moment you scroll back up.
        _queue = new SizeQueue(Measured, path => _rows.ContainsKey(path), capacity: 512);
        _changes = new FolderChanges(Changed);
        _retry = new Timer(_ => Changed([.. _stale.Keys]), null, Timeout.Infinite, Timeout.Infinite);
        _settle = new Timer(_ => OnDispatcher(() => Settled?.Invoke()), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Raised a moment after the last size landed. Sizes arrive long after the rows are in order,
    /// so whoever put them in order has to be told it can do it again.
    /// </summary>
    public event Action? Settled;

    /// <summary>
    /// Everything under the folder on screen, or null while it is being read and whenever the map
    /// is off. The walk is the expensive one — measured, 479ms for D:\dev and about 7s for the
    /// whole profile — so it runs off the UI thread and is abandoned the moment you navigate.
    /// </summary>
    public SizeTree? Tree { get => _tree; private set => Set(ref _tree, value); }

    /// <summary>Whether this listing's folders are measured at all. Remembered between runs.</summary>
    public bool On
    {
        get => _settings.FolderSizes;
        set
        {
            if (_settings.FolderSizes == value) return;

            _settings.FolderSizes = value;
            OnChanged();
            _remember();

            if (value) MeasureAll();
            else Forget();
        }
    }

    /// <summary>
    /// The listing now on screen. Replacing the map is also what abandons the previous folder's
    /// queued work: the queue asks whether a path is still wanted before walking it, and paths
    /// from a folder you have left are not.
    /// </summary>
    public void Show(string folder, IEnumerable<EntryViewModel> rows)
    {
        _folder = folder;
        _rows.Clear();

        foreach (var row in rows)
        {
            if (!row.Entry.IsDirectory) continue;
            _rows[row.Entry.FullPath] = row;
        }

        // Before anything is queued, because whoever called this is about to put the rows in
        // order and an order decided on sizes nobody has painted yet is an order by nothing.
        Restore();

        _changes.Watch(On && _rows.Count > 0 ? folder : null);
        if (On) MeasureAll();

        Deepen();
    }

    /// <summary>
    /// Reads the whole tree for the folder on screen, if anything is going to draw it. Called
    /// again when the map is switched on, because until then there was no reason to have paid
    /// for it.
    /// </summary>
    public void Deepen()
    {
        _deep?.Cancel();
        _deep?.Dispose();
        _deep = null;
        Tree = null;

        if (!On || !_settings.Treemap || _folder.Length == 0) return;

        var stop = new CancellationTokenSource();
        _deep = stop;
        var wanted = _folder;

        Task.Run(() =>
        {
            SizeTree? tree;
            try { tree = SizeTree.Read(wanted, stop.Token); }
            catch (OperationCanceledException) { return; }

            OnDispatcher(() =>
            {
                // Still the folder that asked: navigating away while a walk is running is the
                // normal case, and its answer belongs to a folder nobody is looking at.
                if (!stop.IsCancellationRequested && _folder == wanted) Tree = tree;
            });
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _deep?.Cancel();
        _deep?.Dispose();
        _settle.Dispose();
        _retry.Dispose();
        _changes.Dispose();
        _queue.Dispose();
    }

    /// <summary>
    /// Puts back what is already known, at no cost, and quietly queues a fresh walk for anything
    /// known but going stale. Quietly matters: a row that already shows a number must not blink
    /// back to an ellipsis every time you walk into the folder — the number is still true enough
    /// to read while a better one is on its way.
    /// </summary>
    private void Restore()
    {
        foreach (var row in _rows.Values)
        {
            if (_queue.Cached(row.Entry.FullPath) is not { } known) continue;

            row.Measured(known);
            _last[known.Path] = (known.MeasuredAt, known.Took);
            if (On && DateTime.UtcNow - known.MeasuredAt > Trust) Quietly(row);
        }
    }

    private void MeasureAll()
    {
        _changes.Watch(_rows.Count > 0 ? _folder : null);

        foreach (var row in _rows.Values)
        {
            if (row.SortSize is null) Measure(row);
        }
    }

    private void Measure(EntryViewModel row)
    {
        row.Measuring();
        Quietly(row);
    }

    private void Quietly(EntryViewModel row)
    {
        _queue.Forget(row.Entry.FullPath);
        _queue.Add(row.Entry.FullPath);
    }

    private void Forget()
    {
        _changes.Watch(null);
        _last.Clear();
        _stale.Clear();
        foreach (var row in _rows.Values) row.ForgetSize();
    }

    /// <summary>Arrives on a timer thread; null means the watcher lost events and gave up saying which.</summary>
    private void Changed(IReadOnlyCollection<string>? children) => OnDispatcher(() => Refresh(children));

    /// <summary>
    /// Both timers fire on a pool thread, and what they wake up reorders rows and redraws the
    /// window. Sending it across here rather than at each subscriber means a subscriber cannot
    /// forget — one of them called InvalidateVisual, which throws off the wrong thread.
    /// </summary>
    private static void OnDispatcher(Action work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) work();
        else dispatcher.BeginInvoke(DispatcherPriority.Background, work);
    }

    private void Refresh(IReadOnlyCollection<string>? children)
    {
        if (!On) return;

        var soonest = TimeSpan.MaxValue;

        foreach (var path in children ?? (IReadOnlyCollection<string>)[.. _rows.Keys])
        {
            if (!_rows.TryGetValue(path, out var row)) continue;

            // Still owed quiet: remember that it moved and come back for it. Dropping it here is
            // what made a change during the quiet period vanish - nothing else was coming to say
            // the folder had moved, so it stayed wrong until the cache let it go.
            if (Rest(path) is { } left)
            {
                _stale[path] = 0;
                if (left < soonest) soonest = left;
                continue;
            }

            _stale.TryRemove(path, out _);

            // Quietly: the row keeps the number it has until a better one arrives. A refresh is
            // not a reason to make the column blink.
            Quietly(row);
        }

        if (soonest != TimeSpan.MaxValue)
            _retry.Change(soonest + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// How much quiet a folder is still owed after its last walk, or null if it owes none. The
    /// debt is what the walk cost: cheap folders are all but live, expensive ones refuse to be
    /// asked again so soon however much they churn.
    /// </summary>
    private TimeSpan? Rest(string path)
    {
        if (!_last.TryGetValue(path, out var last)) return null;

        var owed = last.Took * Quiet;
        if (owed < Least) owed = Least;

        var left = owed - (DateTime.UtcNow - last.At);
        return left > TimeSpan.Zero ? left : null;
    }

    private void Measured(FolderSizeResult result)
    {
        _last[result.Path] = (DateTime.UtcNow, result.Took);
        if (_rows.TryGetValue(result.Path, out var row)) row.Measured(result);

        _settle.Change(TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
    }
}
