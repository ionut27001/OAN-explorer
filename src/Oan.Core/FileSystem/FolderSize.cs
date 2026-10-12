using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace Oan.Core.FileSystem;

/// <summary>What one measurement found. <see cref="Bytes"/> null means nothing could be read; 0 means measured and empty.</summary>
public sealed record FolderSizeResult
{
    public required string Path { get; init; }
    public required long? Bytes { get; init; }

    /// <summary>Entries the walk could not read — their contents are missing from the total.</summary>
    public int Skipped { get; init; }

    /// <summary>Junctions and symlinks deliberately not descended into.</summary>
    public int NotFollowed { get; init; }

    public DateTime MeasuredAt { get; init; } = DateTime.UtcNow;

    /// <summary>How long the walk took; the basis for deciding how often it may be repeated.</summary>
    public TimeSpan Took { get; init; }

    public bool IsFloor => Bytes is not null && (Skipped > 0 || NotFollowed > 0);

    public static FolderSizeResult Unknown(string path) => new() { Path = path, Bytes = null };
}

/// <summary>Adds up every file beneath a folder by walking it; reading the $MFT needs administrator (docs/folder-size-baseline.md).
/// Measured: hand-rolled NtQueryDirectoryFile and buffer size change nothing, threads give 3.2x-7.2x.
/// Still not cheap (1.1 million files, 58 s single-threaded): cache the answer and always pass a cancellable token.</summary>
public sealed class FolderSize
{
    // Disk-bound, so threads pay up to about the core count, then flatten (16 threads 3.19x, 32 threads 3.23x).
    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount, 2, 16);

    private readonly Dictionary<string, (FolderSizeResult Result, long Ticket)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _freshness;
    private readonly int _capacity;
    private long _ticket;

    /// <param name="freshness">How long a measurement stays usable before the folder is walked again.</param>
    /// <param name="capacity">How many answers to keep; smaller than a listing means re-walking on scroll.</param>
    public FolderSize(TimeSpan? freshness = null, int capacity = 64)
    {
        _freshness = freshness ?? TimeSpan.FromMinutes(1);
        _capacity = Math.Max(1, capacity);
    }

    public async Task<long?> MeasureAsync(string path, CancellationToken ct = default) =>
        (await MeasureDetailedAsync(path, ct)).Bytes;

    public Task<FolderSizeResult> MeasureDetailedAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Measure(path, ct), ct);

    /// <summary>The already-known size of a folder, or null if not measured lately. Never starts a walk.</summary>
    public FolderSizeResult? Cached(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = Key(path);
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out var hit)) return null;
            if (DateTime.UtcNow - hit.Result.MeasuredAt < _freshness) return hit.Result;
            _cache.Remove(key);
            return null;
        }
    }

    public FolderSizeResult Measure(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return FolderSizeResult.Unknown(path);

        // Checked before the walk too: an empty folder never enters the loop and would come back as a confident 0.
        ct.ThrowIfCancellationRequested();

        var key = Key(path);
        if (Cached(key) is { } hit) return hit;

        var started = Stopwatch.GetTimestamp();
        var walk = new Walk(key, ct);
        walk.Run(Threads);
        if (walk.RootRefused) return FolderSizeResult.Unknown(path);

        var result = new FolderSizeResult
        {
            Path = key,
            Bytes = walk.Bytes,
            Skipped = walk.Skipped,
            NotFollowed = walk.NotFollowed,
            Took = Stopwatch.GetElapsedTime(started),
        };

        Remember(result);
        return result;
    }

    /// <summary>Drops what is known about a folder, so the next ask walks it again.</summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var key = Key(path);
        lock (_cache) _cache.Remove(key);
    }

    private void Remember(FolderSizeResult result)
    {
        lock (_cache)
        {
            // The ticket, not MeasuredAt, decides eviction: two measurements can share a clock tick.
            _cache[result.Path] = (result, ++_ticket);
            if (_cache.Count <= _capacity) return;
            _cache.Remove(_cache.MinBy(e => e.Value.Ticket).Key);
        }
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>The folders still to read, and the running totals. The count of folders handed out but not finished ends the walk;
    /// an empty queue does not, because a thread still reading may refill it.</summary>
    private sealed class Walk(string root, CancellationToken ct)
    {
        private readonly BlockingCollection<string> _todo = new(new ConcurrentBag<string>());
        private long _bytes;
        private int _skipped;
        private int _notFollowed;
        private int _outstanding = 1;

        public long Bytes => Interlocked.Read(ref _bytes);
        public int Skipped => _skipped;
        public int NotFollowed => _notFollowed;

        /// <summary>The top folder itself never opened.</summary>
        public bool RootRefused { get; private set; }

        public void Run(int threads)
        {
            _todo.Add(root);

            // Own threads, not the pool: these sit blocked on the disk and would starve everything else.
            var workers = new Thread[threads];
            for (var i = 0; i < threads; i++)
            {
                workers[i] = new Thread(Work) { IsBackground = true, Name = "Folder size walk" };
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
            ct.ThrowIfCancellationRequested();
        }

        private void Work()
        {
            BackgroundMode.Enter();
            foreach (var folder in _todo.GetConsumingEnumerable())
            {
                if (!ct.IsCancellationRequested) Read(folder);

                // Last one out closes the queue, so every other thread's GetConsumingEnumerable returns.
                if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
            }
        }

        private void Read(string folder)
        {
            var isRoot = ReferenceEquals(folder, root);
            try
            {
                using var level = new Level(folder);
                while (level.MoveNext())
                {
                    if (ct.IsCancellationRequested) break;

                    var (bytes, child) = level.Current;
                    if (child is null)
                    {
                        Interlocked.Add(ref _bytes, bytes);
                        continue;
                    }

                    Interlocked.Increment(ref _outstanding);
                    try { _todo.Add(child); }
                    catch (InvalidOperationException) { Interlocked.Decrement(ref _outstanding); }
                }

                Interlocked.Add(ref _skipped, level.Skipped);
                Interlocked.Add(ref _notFollowed, level.NotFollowed);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                if (isRoot) RootRefused = true;
                else Interlocked.Increment(ref _skipped);
            }
        }
    }

    /// <summary>One level of one folder. IgnoreInaccessible is off so unreadable folders are counted, not hidden:
    /// the top folder never opening is thrown, one unreadable branch is a skip.</summary>
    private sealed class Level : FileSystemEnumerator<(long Bytes, string? Folder)>
    {
        private bool _readAnEntry;

        public Level(string path) : base(path, new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            // Hidden and system files take up space like any other, and Explorer counts them.
            AttributesToSkip = 0,
        })
        { }

        public int Skipped { get; private set; }
        public int NotFollowed { get; private set; }

        /// <summary>Reparse folders are not followed: a junction may point at its own parent (C:\Users\All Users).</summary>
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

        /// <summary>Nothing read yet means the folder itself never opened, which is thrown; later errors are skips.</summary>
        protected override bool ContinueOnError(int error)
        {
            if (!_readAnEntry) return false;
            Skipped++;
            return true;
        }
    }
}
