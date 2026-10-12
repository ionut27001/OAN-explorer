using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Oan.Core.FileSystem;

namespace Oan.App.Platform;

/// <summary>
/// Folder sizes measured one folder at a time, off the UI thread: a measurement is threaded internally, and
/// two would only fight. Results arrive on the dispatcher at background priority.
/// </summary>
internal sealed class SizeQueue : IDisposable
{
    private readonly FolderSize _sizes;
    private readonly BlockingCollection<string> _queue = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<FolderSizeResult> _done;
    private readonly Func<string, bool>? _wanted;

    /// <param name="wanted">Asked, off the UI thread, just before a queued folder is walked, so a listing
    /// left behind is skipped.</param>
    /// <param name="capacity">How many answers to keep.</param>
    public SizeQueue(Action<FolderSizeResult> done, Func<string, bool>? wanted = null, int capacity = 64)
    {
        _done = done;
        _wanted = wanted;
        _sizes = new FolderSize(TimeSpan.FromHours(1), capacity);
        new Thread(Pump)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Folder sizes",
        }.Start();
    }

    /// <summary>An answer already known, or null. Lets a row paint without starting a walk.</summary>
    public FolderSizeResult? Cached(string path) => _sizes.Cached(path);

    /// <summary>Throws away what is known about a folder, so the next ask walks it again.</summary>
    public void Forget(string path) => _sizes.Invalidate(path);

    public void Add(string path)
    {
        try { _queue.Add(path); }
        catch (InvalidOperationException) { } // Closed: the window is going away.
    }

    public void Dispose()
    {
        _stop.Cancel();
        _queue.CompleteAdding();
    }

    private void Pump()
    {
        try
        {
            foreach (var path in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (_wanted?.Invoke(path) == false) continue;
                Report(Measure(path));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private FolderSizeResult Measure(string path)
    {
        try
        {
            return _sizes.Measure(path, _stop.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return FolderSizeResult.Unknown(path);
        }
    }

    private void Report(FolderSizeResult result)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null) _done(result);
        else dispatcher.BeginInvoke(DispatcherPriority.Background, () => _done(result));
    }
}
