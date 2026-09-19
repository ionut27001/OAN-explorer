using System.Collections.Concurrent;
using Oan.App.Platform;
using Oan.Core.FileSystem;

namespace Oan.App.ViewModels;

/// <summary>
/// Folder sizes for the rows of one listing.
///
/// Off by default, and that is a measurement rather than a preference. There is no cheap way to
/// know a folder's size without administrator — see docs/folder-size-baseline.md — so the column
/// costs a full walk of everything below every folder on screen. Threaded, the user profile is
/// 7.4 seconds and 1.1 million files; AppData alone is 6.1 of those 7.4. Nobody opening a folder
/// asked for that, so it is asked for explicitly, per listing, and forgotten on the way out.
///
/// What is already known is painted for free, switch or no switch: that is a dictionary lookup.
/// </summary>
public sealed class FolderSizesViewModel : Observable, IDisposable
{
    private readonly ConcurrentDictionary<string, EntryViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly SizeQueue _queue;
    private bool _on;

    public FolderSizesViewModel() =>
        // Capacity well past a big listing: a cache smaller than the folder you are looking at
        // evicts its own answers and re-walks them the moment you scroll back up.
        _queue = new SizeQueue(Measured, path => _rows.ContainsKey(path), capacity: 512);

    /// <summary>Whether this listing's folders are being measured.</summary>
    public bool On
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value)) return;

            if (_on) MeasureAll();
            else Forget();
        }
    }

    /// <summary>
    /// The rows of the listing now on screen. Replacing the map is also what abandons the
    /// previous folder's queued work: the pump asks whether a path is still wanted, and paths
    /// from a listing you have navigated away from are not.
    /// </summary>
    public void Show(IEnumerable<EntryViewModel> rows)
    {
        _rows.Clear();
        foreach (var row in rows)
        {
            if (!row.Entry.IsDirectory) continue;

            _rows[row.Entry.FullPath] = row;

            // Free: an answer measured minutes ago is still on the row before anything is asked.
            if (_queue.Cached(row.Entry.FullPath) is { } known) row.Measured(known);
        }

        if (_on) MeasureAll();
    }

    public void Dispose() => _queue.Dispose();

    private void MeasureAll()
    {
        foreach (var row in _rows.Values)
        {
            if (row.SortSize is not null) continue;

            row.Measuring();
            _queue.Add(row.Entry.FullPath);
        }
    }

    private void Forget()
    {
        foreach (var row in _rows.Values) row.ForgetSize();
    }

    private void Measured(FolderSizeResult result)
    {
        if (_rows.TryGetValue(result.Path, out var row)) row.Measured(result);
    }
}
