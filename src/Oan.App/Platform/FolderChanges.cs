using System.IO;

namespace Oan.App.Platform;

/// <summary>
/// Watches the folder on screen and says which of its children the disk touched.
///
/// Recursive, because a size changes when something deep inside changes, and the top level would
/// never see that. Recursive also means a busy tree — AppData writes constantly — can produce
/// more events than the buffer holds; that is not an error to report but a fact to handle, so an
/// overflow is reported as "assume all of them" and the caller decides what that is worth.
///
/// Events are collected for a moment before being handed over: copying one file produces several,
/// and re-measuring a folder per event would mean measuring it while it is still being written.
/// </summary>
internal sealed class FolderChanges : IDisposable
{
    private readonly Action<IReadOnlyCollection<string>?> _changed;
    private readonly TimeSpan _settle;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private string _root = "";
    private bool _everything;

    /// <param name="changed">The children that changed, or null for "assume all of them".
    /// Raised on a timer thread.</param>
    public FolderChanges(Action<IReadOnlyCollection<string>?> changed, TimeSpan? settle = null)
    {
        _changed = changed;
        _settle = settle ?? TimeSpan.FromMilliseconds(750);
        _timer = new Timer(_ => Report(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Watch this folder instead of whatever was being watched. Null stops watching.</summary>
    public void Watch(string? folder)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                // The largest the API allows. It does not stop an overflow on a tree like
                // AppData, it makes one rarer, and an overflow is handled rather than lost.
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };

            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A folder that cannot be watched is not a folder that cannot be shown. Sizes there
            // simply go stale until the cache lets them go.
            Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    private void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);

        lock (_gate)
        {
            _pending.Clear();
            _everything = false;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Child(e.FullPath) is not { } child) return;

        lock (_gate) _pending.Add(child);
        _timer.Change(_settle, Timeout.InfiniteTimeSpan);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate) _everything = true;
        _timer.Change(_settle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Which child of the watched folder a path lies under. Null for the folder itself and for
    /// anything directly inside it: a file at the top level belongs to no child's total.
    /// </summary>
    private string? Child(string full)
    {
        if (_root.Length == 0 || full.Length <= _root.Length + 1) return null;
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = full[(_root.Length + 1)..];
        var cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return cut < 0 ? null : Path.Combine(_root, rest[..cut]);
    }

    private void Report()
    {
        string[] children;
        bool everything;

        lock (_gate)
        {
            everything = _everything;
            children = [.. _pending];
            _pending.Clear();
            _everything = false;
        }

        if (everything) _changed(null);
        else if (children.Length > 0) _changed(children);
    }
}
