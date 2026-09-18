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
/// There is no shortcut. Without an index this is a full recursive walk, and it is not cheap:
/// measured on this machine, Desktop takes 11 ms but C:\Users\oance takes 36 seconds for 1.1
/// million files, warm cache included. (WizTree is instant because it reads the NTFS $MFT as one
/// sequential stream, which needs administrator rights.) So: cache the answer, never measure a
/// whole profile unasked, and always pass a token you are willing to cancel.
/// </summary>
public sealed class FolderSize
{
    private const int Capacity = 64;

    private readonly Dictionary<string, (FolderSizeResult Result, long Ticket)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _freshness;
    private long _ticket;

    /// <param name="freshness">How long a measurement stays usable before the folder is walked
    /// again. A file manager changes the disk under its own feet, so this is not forever.</param>
    public FolderSize(TimeSpan? freshness = null) => _freshness = freshness ?? TimeSpan.FromMinutes(1);

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

        long total = 0;
        FolderSizeResult result;
        try
        {
            using var walker = new Walker(key);
            while (walker.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                total += walker.Current;
            }
            result = new FolderSizeResult
            {
                Path = key,
                Bytes = total,
                Skipped = walker.Skipped,
                NotFollowed = walker.NotFollowed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // The top folder itself could not be opened: nothing was read, so nothing is known.
            return FolderSizeResult.Unknown(path);
        }

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
            if (_cache.Count <= Capacity) return;
            _cache.Remove(_cache.MinBy(e => e.Value.Ticket).Key);
        }
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Subclassed for the reason <see cref="DirectoryLister"/> gives: with IgnoreInaccessible the
    /// runtime hides unreadable folders and a partial total looks like a complete one. With it
    /// off, the two failures arrive down the same path and must be told apart — failing before a
    /// single record was read means the top folder never opened, which is fatal; failing later is
    /// one unreadable branch, which is a skip.
    /// </summary>
    private sealed class Walker : FileSystemEnumerator<long>
    {
        private bool _readAnEntry;

        public Walker(string path) : base(path, new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
            // Hidden and system files take up space like any other, and Explorer counts them.
            AttributesToSkip = 0,
        })
        { }

        public int Skipped { get; private set; }
        public int NotFollowed { get; private set; }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            _readAnEntry = true;
            return !entry.IsDirectory;
        }

        /// <summary>
        /// C:\Users\All Users is a junction to ProgramData on every Windows install, and a
        /// junction may point at its own parent. Following them double-counts at best and never
        /// terminates at worst.
        /// </summary>
        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            // Flagged here as well as in ShouldIncludeEntry, and this is the case that matters:
            // saying yes makes the runtime open that subfolder's handle immediately, so a denied
            // first subfolder raises the error before any entry was ever included. Without this
            // line that error reads as "the top folder never opened" and a whole folder's size
            // comes back unknown instead of as a floor.
            _readAnEntry = true;

            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return true;
            NotFollowed++;
            return false;
        }

        protected override long TransformEntry(ref FileSystemEntry entry) => entry.Length;

        protected override bool ContinueOnError(int error)
        {
            if (!_readAnEntry) return false;
            Skipped++;
            return true;
        }
    }
}
