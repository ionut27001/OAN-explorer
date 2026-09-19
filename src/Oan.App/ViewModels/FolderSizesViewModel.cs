using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using Oan.App.Platform;
using Oan.Core.FileSystem;

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

    private readonly ConcurrentDictionary<string, EntryViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, TimeSpan Took)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stale = new(StringComparer.OrdinalIgnoreCase);
    private readonly SizeQueue _queue;
    private readonly FolderChanges _changes;
    private readonly Timer _retry;
    private readonly Settings _settings;
    private readonly Action _remember;
    private string _folder = "";

    public FolderSizesViewModel(Settings settings, Action remember)
    {
        _settings = settings;
        _remember = remember;

        // Capacity well past a big listing: a cache smaller than the folder you are looking at
        // evicts its own answers and re-walks them the moment you scroll back up.
        _queue = new SizeQueue(Measured, path => _rows.ContainsKey(path), capacity: 512);
        _changes = new FolderChanges(Changed);
        _retry = new Timer(_ => Changed([.. _stale.Keys]), null, Timeout.Infinite, Timeout.Infinite);
    }

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

            // Free: an answer measured minutes ago is on the row before anything is asked again.
            if (_queue.Cached(row.Entry.FullPath) is { } known) row.Measured(known);
        }

        _changes.Watch(On && _rows.Count > 0 ? folder : null);
        if (On) MeasureAll();
    }

    public void Dispose()
    {
        _retry.Dispose();
        _changes.Dispose();
        _queue.Dispose();
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
    private void Changed(IReadOnlyCollection<string>? children)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) Refresh(children);
        else dispatcher.BeginInvoke(DispatcherPriority.Background, () => Refresh(children));
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
            _queue.Forget(path);
            Measure(row);
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
    }
}
