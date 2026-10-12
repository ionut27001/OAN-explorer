using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using Oan.App.Platform;
using Oan.Core.FileSystem;
using Oan.Core.Sizes;

namespace Oan.App.ViewModels;

/// <summary>
/// Folder sizes for one listing's rows. Re-measuring is rationed by what the last walk cost, or a
/// churning tree like AppData keeps the walk in a permanent loop. See docs/folder-size-baseline.md.
/// </summary>
public sealed class FolderSizesViewModel : Observable, IDisposable
{
    /// <summary>How many times its own cost a folder is left alone for after being measured.</summary>
    private const int Quiet = 20;

    private static readonly TimeSpan Least = TimeSpan.FromSeconds(2);

    /// <summary>How long a remembered size is shown before it is quietly checked again.</summary>
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
    private bool _reading;
    private int _found;

    public FolderSizesViewModel(Settings settings, Action remember)
    {
        _settings = settings;
        _remember = remember;

        // A cache smaller than the listing evicts its own answers and re-walks them on scroll.
        _queue = new SizeQueue(Measured, path => _rows.ContainsKey(path), capacity: 512);
        _changes = new FolderChanges(Changed);
        _retry = new Timer(_ => Changed([.. _stale.Keys]), null, Timeout.Infinite, Timeout.Infinite);
        _settle = new Timer(_ => OnDispatcher(() => Settled?.Invoke()), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised a moment after the last size landed, so the rows can be put in order again.</summary>
    public event Action? Settled;

    /// <summary>Everything under the folder on screen; null while it is being read or the map is off.</summary>
    public SizeTree? Tree { get => _tree; private set => Set(ref _tree, value); }

    /// <summary>The tree is being walked. The map waits: a half-measured picture reads as a wrong answer.</summary>
    public bool Reading
    {
        get => _reading;
        private set
        {
            if (!Set(ref _reading, value)) return;

            OnChanged(nameof(Ready));
        }
    }

    public bool Ready => !_reading;

    /// <summary>How many entries the walk has read. There is no total to count towards.</summary>
    public string FoundText => _found == 0 ? "" : $"{_found:N0} items so far";

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

            if (value)
            {
                _changes.Watch(_rows.Count > 0 ? _folder : null);
                MeasureAll();
            }
            else Forget();

            // The map draws what the column measures, so the switch owns both.
            Deepen();
        }
    }

    /// <summary>The listing now on screen. Replacing the rows abandons the previous folder's queued work.</summary>
    public void Show(string folder, IEnumerable<EntryViewModel> rows)
    {
        _folder = folder;
        _rows.Clear();

        foreach (var row in rows)
        {
            if (!row.Entry.IsDirectory) continue;
            _rows[row.Entry.FullPath] = row;
        }

        // Before anything is queued: the caller is about to sort, and needs the known sizes in place.
        Restore();

        _changes.Watch(On && _rows.Count > 0 ? folder : null);
        if (On) MeasureAll();
    }

    /// <summary>
    /// Reads the whole tree for the folder on screen. Not called from <see cref="Show"/>, which also
    /// runs on every filter keystroke, and the walk is seconds long.
    /// </summary>
    public void Deepen()
    {
        var wanted = On && _settings.Treemap ? _folder : "";

        _deep?.Cancel();
        _deep?.Dispose();
        _deep = null;
        Tree = null;
        _found = 0;
        OnChanged(nameof(FoundText));
        Reading = wanted.Length > 0;

        if (wanted.Length == 0) return;

        var stop = new CancellationTokenSource();
        _deep = stop;

        Task.Run(() =>
        {
            SizeTree? tree;
            try
            {
                tree = SizeTree.Read(wanted, stop.Token, Counted);
            }
            catch (OperationCanceledException)
            {
                // Another folder's walk owns the indicator now; do not turn it off here.
                return;
            }

            OnDispatcher(() =>
            {
                // Navigating away while a walk is running is the normal case.
                if (stop.IsCancellationRequested || _folder != wanted) return;

                Tree = tree;
                Reading = false;
            });
        }, CancellationToken.None);
    }

    /// <summary>Raised on a walking thread.</summary>
    private void Counted(int entries) => OnDispatcher(() =>
    {
        if (!Reading) return;

        _found = entries;
        OnChanged(nameof(FoundText));
    });

    public void Dispose()
    {
        _deep?.Cancel();
        _deep?.Dispose();
        _settle.Dispose();
        _retry.Dispose();
        _changes.Dispose();
        _queue.Dispose();
    }

    /// <summary>Puts back what is known and quietly re-queues what is going stale, without blanking the number.</summary>
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

    /// <summary>Both timers fire on a pool thread and what they wake touches the UI, so everything crosses here.</summary>
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

            // Still owed quiet: remember that it moved and come back, or the change is lost.
            if (Rest(path) is { } left)
            {
                _stale[path] = 0;
                if (left < soonest) soonest = left;
                continue;
            }

            _stale.TryRemove(path, out _);

            Quietly(row);
        }

        if (soonest != TimeSpan.MaxValue)
            _retry.Change(soonest + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    /// <summary>How much quiet a folder is still owed: what its last walk cost times <see cref="Quiet"/>.</summary>
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
