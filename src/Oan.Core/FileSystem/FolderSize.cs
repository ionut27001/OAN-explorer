using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Oan.Core.FileSystem;

/// <summary>
/// What one measurement found. <see cref="Bytes"/> is null when nothing could be read at all;
/// 0 means "measured, and it is empty". A number with <see cref="Skipped"/> or
/// <see cref="NotFollowed"/> above zero is a floor, not a total.
/// </summary>
public sealed record FolderSizeResult
{
    public required string Path { get; init; }
    public required long? Bytes { get; init; }

    /// <summary>Entries the walk could not read — their contents are missing from the total.</summary>
    public int Skipped { get; init; }

    /// <summary>Junctions and symlinks deliberately not descended into.</summary>
    public int NotFollowed { get; init; }

    public DateTime MeasuredAt { get; init; } = DateTime.UtcNow;

    public bool IsFloor => Bytes is not null && (Skipped > 0 || NotFollowed > 0);

    public static FolderSizeResult Unknown(string path) => new() { Path = path, Bytes = null };
}

/// <summary>
/// Adds up every file beneath a folder.
///
/// There is no shortcut without an index. WizTree is instant because it reads the NTFS $MFT as
/// one sequential stream, which needs administrator; measured on this machine, every call that
/// would yield bulk per-file data comes back access-denied, and the account has neither
/// SeBackupPrivilege nor Backup Operators. See docs/folder-size-baseline.md.
///
/// So this walks, and the one thing that makes a walk faster was measured too: calling
/// NtQueryDirectoryFile by hand is a wash, because FileSystemEnumerator already is that call, and
/// buffer size moves nothing. Threads are the whole win — 3.2x on a source tree, 7.2x on the
/// profile and on C:\Windows. It is still not cheap: C:\Users\oance is 1.1 million files and 58
/// seconds of single-threaded walking. Cache the answer, never measure a whole profile unasked,
/// and always pass a token you are willing to cancel.
/// </summary>
public sealed class FolderSize
{
    // More threads than cores still pays, because this waits on the disk rather than the CPU;
    // past about the core count the curve flattens (16 threads 3.19x, 32 threads 3.23x).
    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount, 2, 16);

    private readonly Dictionary<string, (FolderSizeResult Result, long Ticket)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _freshness;
    private readonly int _capacity;
    private long _ticket;

    /// <param name="freshness">How long a measurement stays usable before the folder is walked
    /// again. A file manager changes the disk under its own feet, so this is not forever.</param>
    /// <param name="capacity">How many answers to keep. A sidebar has a dozen places; a listing
    /// can have hundreds of folders, and a cache smaller than the listing re-walks on scroll.</param>
    public FolderSize(TimeSpan? freshness = null, int capacity = 64)
    {
        _freshness = freshness ?? TimeSpan.FromMinutes(1);
        _capacity = Math.Max(1, capacity);
    }

    public async Task<long?> MeasureAsync(string path, CancellationToken ct = default) =>
        (await MeasureDetailedAsync(path, ct)).Bytes;

    public Task<FolderSizeResult> MeasureDetailedAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Measure(path, ct), ct);

    /// <summary>The already-known size of a folder, or null if it has not been measured lately.
    /// Lets the UI paint a cached number without starting a walk.</summary>
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

        // Before the walk, not only inside it: an empty folder never enters the loop, so a
        // request cancelled before it started would otherwise come back as a confident 0.
        ct.ThrowIfCancellationRequested();

        var key = Key(path);
        if (Cached(key) is { } hit) return hit;

        var walk = new Walk(key, ct);
        walk.Run(Threads);
        if (walk.RootRefused) return FolderSizeResult.Unknown(path);

        var result = new FolderSizeResult
        {
            Path = key,
            Bytes = walk.Bytes,
            Skipped = walk.Skipped,
            NotFollowed = walk.NotFollowed,
        };

        Remember(result);
        return result;
    }

    private void Remember(FolderSizeResult result)
    {
        lock (_cache)
        {
            // The ticket, rather than MeasuredAt, decides what to drop: two measurements can
            // share a clock tick, and eviction must not then be a coin toss.
            _cache[result.Path] = (result, ++_ticket);
            if (_cache.Count <= _capacity) return;
            _cache.Remove(_cache.MinBy(e => e.Value.Ticket).Key);
        }
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The folders still to read, and the running totals. Each thread takes a folder, reads that
    /// one level, adds its files and hands its subfolders back. The count of folders handed out
    /// but not finished is what says the walk is over — an empty queue does not, because a thread
    /// still reading may be about to refill it.
    /// </summary>
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

        /// <summary>The top folder itself never opened, so nothing at all is known about it.</summary>
        public bool RootRefused { get; private set; }

        public void Run(int threads)
        {
            _todo.Add(root);

            // Threads of their own, not the thread pool: these sit blocked on the disk, and
            // sixteen pool threads doing that would starve everything else the app runs on it.
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
            foreach (var folder in _todo.GetConsumingEnumerable())
            {
                if (!ct.IsCancellationRequested) Read(folder);

                // Last one out closes the queue, which is what lets every other thread's
                // GetConsumingEnumerable return instead of blocking for work that never comes.
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

    /// <summary>
    /// One level of one folder: its files' sizes, and the subfolders to come back for.
    ///
    /// Subclassed for the reason <see cref="DirectoryLister"/> gives: with IgnoreInaccessible the
    /// runtime hides unreadable folders and a partial total looks like a complete one. With it
    /// off, the two failures arrive down the same path and must be told apart — the top folder
    /// never opening is fatal and has to be thrown, one unreadable branch is a skip.
    /// </summary>
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

        /// <summary>
        /// C:\Users\All Users is a junction to ProgramData on every Windows install, and a
        /// junction may point at its own parent. Following them double-counts at best and never
        /// terminates at worst.
        /// </summary>
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

        /// <summary>
        /// Nothing read yet means the folder itself never opened: thrown, so the caller can say
        /// whether that is one unreadable branch or the whole measurement. Later errors are the
        /// ordinary case of a subfolder that will not open, and those are a skip.
        /// </summary>
        protected override bool ContinueOnError(int error)
        {
            if (!_readAnEntry) return false;
            Skipped++;
            return true;
        }
    }
}
