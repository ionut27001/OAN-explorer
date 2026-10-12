using System.IO;

namespace Oan.App.Platform;

/// <summary>
/// Watches the folder on screen, recursively, and says which of its children the disk touched. A buffer
/// overflow is reported as "assume all of them". Events are batched until the writing stops, with a ceiling:
/// without one, a ten minute copy reported nothing until it ended.
/// </summary>
internal sealed class FolderChanges : IDisposable
{
    private readonly Action<IReadOnlyCollection<string>?> _changed;
    private readonly TimeSpan _settle;

    /// <summary>The longest a batch may keep pushing its own deadline back.</summary>
    private readonly TimeSpan _ceiling;


    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private string _root = "";
    private bool _everything;

    /// <summary>Ticks at the first event of a batch, zero when nothing is pending.</summary>
    private long _since;

    /// <param name="changed">The children that changed, or null for "assume all of them".
    /// Raised on a timer thread.</param>
    public FolderChanges(Action<IReadOnlyCollection<string>?> changed, TimeSpan? settle = null)
    {
        _changed = changed;
        _settle = settle ?? TimeSpan.FromMilliseconds(750);
        _ceiling = _settle * 5;
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
                // The largest the API allows. It makes an overflow on a busy tree rarer, not impossible.
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
            // A folder that cannot be watched can still be shown; its sizes go stale.
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
            _since = 0;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Child(e.FullPath) is not { } child) return;

        lock (_gate)
        {
            _pending.Add(child);
            Arm();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            _everything = true;
            Arm();
        }
    }

    /// <summary>Waits for the writing to stop, but never past the ceiling. Called under the gate.</summary>
    private void Arm()
    {
        var now = Environment.TickCount64;
        if (_since == 0) _since = now;

        var left = _ceiling - TimeSpan.FromMilliseconds(now - _since);
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;

        _timer.Change(left < _settle ? left : _settle, Timeout.InfiniteTimeSpan);
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
            _since = 0;
        }

        if (everything) _changed(null);
        else if (children.Length > 0) _changed(children);
    }
}
