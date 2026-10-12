using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Oan.Tests;

/// <summary>
/// The floor: the same threads, the same queue, the same enumerator, descending into exactly the
/// same folders — but keeping nothing per entry. Whatever this costs is what the operating system
/// charges to list the tree, and no rewrite of the bookkeeping can go below it. The gap between
/// this and the shipped walk is the part that is ours to spend or save.
/// </summary>
sealed class FloorWalk(string root, CancellationToken ct = default)
{
    readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
    int _outstanding = 1;
    int _next;
    long _bytes;
    int _skipped;
    int _notFollowed;

    public int Count => _next;
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Skipped => _skipped;
    public int NotFollowed => _notFollowed;

    public void Run(int threads)
    {
        _todo.Add((root, -1));

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = new Thread(Work) { IsBackground = true, Name = "Size floor" };
            workers[i].Start();
        }

        try
        {
            foreach (var worker in workers) worker.Join();
        }
        finally
        {
            _todo.Dispose();
        }
    }

    void Work()
    {
        foreach (var (path, index) in _todo.GetConsumingEnumerable())
        {
            if (!ct.IsCancellationRequested) Read(path);
            if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
        }
    }

    void Read(string path)
    {
        try
        {
            using var level = new FloorLevel(path);
            var here = 0L;

            while (level.MoveNext())
            {
                if (ct.IsCancellationRequested) break;

                var (bytes, child) = level.Current;
                var at = Interlocked.Increment(ref _next) - 1;

                if (child is null)
                {
                    here += bytes;
                    continue;
                }

                Interlocked.Increment(ref _outstanding);
                try { _todo.Add((child, at)); }
                catch (InvalidOperationException) { Interlocked.Decrement(ref _outstanding); }
            }

            Interlocked.Add(ref _bytes, here);
            Interlocked.Add(ref _skipped, level.Skipped);
            Interlocked.Add(ref _notFollowed, level.NotFollowed);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Interlocked.Increment(ref _skipped);
        }
    }
}

/// <summary>The same level without the name: a size, and a path only where one has to be queued.</summary>
sealed class FloorLevel : FileSystemEnumerator<(long Bytes, string? Folder)>
{
    bool _readAnEntry;

    public FloorLevel(string path) : base(path, new EnumerationOptions
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    })
    { }

    public int Skipped { get; private set; }
    public int NotFollowed { get; private set; }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        _readAnEntry = true;
        if (!entry.IsDirectory) return true;

        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return true;
        NotFollowed++;
        return false;
    }

    protected override (long Bytes, string? Folder) TransformEntry(ref FileSystemEntry entry) =>
        entry.IsDirectory ? (0, entry.ToFullPath()) : (entry.Length, null);

    protected override bool ContinueOnError(int error)
    {
        if (!_readAnEntry) return false;
        Skipped++;
        return true;
    }
}
