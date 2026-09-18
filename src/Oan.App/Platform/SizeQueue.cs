using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Oan.Core.FileSystem;

namespace Oan.App.Platform;

/// <summary>
/// Folder sizes measured one at a time on a single lowest-priority background thread.
///
/// A size is a full recursive walk — millions of syscalls for a large tree — so it is never on
/// the UI thread and never more than one at a time: two walks do not finish sooner together,
/// they just take the machine's head off. Results arrive on the dispatcher at background
/// priority, so a size landing cannot stutter a scroll.
/// </summary>
internal sealed class SizeQueue : IDisposable
{
    private readonly FolderSize _sizes = new(TimeSpan.FromMinutes(5));
    private readonly BlockingCollection<string> _queue = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<FolderSizeResult> _done;

    public SizeQueue(Action<FolderSizeResult> done)
    {
        _done = done;
        new Thread(Pump)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Folder sizes",
        }.Start();
    }

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
                Report(Measure(path));
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
