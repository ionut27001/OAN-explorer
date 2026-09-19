using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Oan.Core.FileSystem;

namespace Oan.App.Platform;

/// <summary>
/// Folder sizes measured one folder at a time, off the UI thread.
///
/// One at a time still, but not for the reason first written here: a single measurement is now
/// threaded internally (see FolderSize), so it already uses the machine, and running two would
/// only have them fight. Results arrive on the dispatcher at background priority, so a size
/// landing cannot stutter a scroll.
/// </summary>
internal sealed class SizeQueue : IDisposable
{
    private readonly FolderSize _sizes;
    private readonly BlockingCollection<string> _queue = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<FolderSizeResult> _done;
    private readonly Func<string, bool>? _wanted;

    /// <param name="wanted">Asked just before a queued folder is walked. A listing can be left
    /// behind while its folders are still in the queue, and walking those costs the same as
    /// walking the ones on screen. Called off the UI thread.</param>
    /// <param name="capacity">How many answers to keep; a listing needs more than a sidebar.</param>
    public SizeQueue(Action<FolderSizeResult> done, Func<string, bool>? wanted = null, int capacity = 64)
    {
        _done = done;
        _wanted = wanted;
        _sizes = new FolderSize(TimeSpan.FromMinutes(5), capacity);
        new Thread(Pump)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Folder sizes",
        }.Start();
    }

    /// <summary>An answer already known, or null. Lets a row paint without starting a walk.</summary>
    public FolderSizeResult? Cached(string path) => _sizes.Cached(path);

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
