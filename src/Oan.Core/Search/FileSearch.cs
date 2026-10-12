using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Oan.Core.Search;

/// <summary>Walks a tree and streams each hit as it is found; nothing is indexed or cached.
/// One run per instance at a time; <see cref="Tally"/> belongs to the latest <see cref="RunAsync"/>.</summary>
public sealed class FileSearch
{
    const int Queue = 512;
    const FileAttributes Concealed = FileAttributes.Hidden | FileAttributes.System;
    static readonly TimeSpan PatternBudget = TimeSpan.FromSeconds(1);

    readonly IContentMatcher? content;
    int files, folders, denied, tooLarge, binary, unreadable;

    public FileSearch(IContentMatcher? content = null) => this.content = content;

    /// <summary>Live during the run, final once the enumeration ends.</summary>
    public SearchTally Tally => new()
    {
        FilesSeen = Volatile.Read(ref files),
        FoldersSeen = Volatile.Read(ref folders),
        Denied = Volatile.Read(ref denied),
        TooLarge = Volatile.Read(ref tooLarge),
        Binary = Volatile.Read(ref binary),
        Unreadable = Volatile.Read(ref unreadable),
    };

    /// <summary>Not an iterator, on purpose: a bad pattern must be refused when the search is asked for, not on first read.</summary>
    public IAsyncEnumerable<SearchHit> RunAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (query.Invalid() is { } refusal) throw new ArgumentException(refusal, nameof(query));
        if (query.SearchesContent && content is null)
            throw new InvalidOperationException("This query searches contents, but no matcher was given.");

        files = folders = denied = tooLarge = binary = unreadable = 0;
        return Stream(query, new NameTest(query), ct);
    }

    async IAsyncEnumerable<SearchHit> Stream(
        SearchQuery query, NameTest name, [EnumeratorCancellation] CancellationToken ct)
    {
        var results = Channel.CreateBounded<SearchHit>(new BoundedChannelOptions(Queue) { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Task.Run(() => RunTo(query, name, results.Writer, stop.Token), CancellationToken.None);

        try
        {
            await foreach (var hit in results.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return hit;
        }
        finally
        {
            // The caller may have walked away mid-stream, leaving the walk blocked on a full queue.
            stop.Cancel();
            await Settle(run).ConfigureAwait(false);
        }
    }

    async Task RunTo(SearchQuery query, NameTest name, ChannelWriter<SearchHit> results, CancellationToken ct)
    {
        try
        {
            if (content is null)
            {
                foreach (var candidate in Walk(query, name, ct))
                    await results.WriteAsync(Hit(candidate), ct).ConfigureAwait(false);
                return;
            }

            await Scan(query, name, content, results, ct).ConfigureAwait(false);
        }
        finally
        {
            results.TryComplete();
        }
    }

    async Task Scan(
        SearchQuery query, NameTest name, IContentMatcher matcher, ChannelWriter<SearchHit> results, CancellationToken ct)
    {
        // Bounded: the walk finds paths far faster than files can be read.
        var queue = Channel.CreateBounded<Candidate>(new BoundedChannelOptions(Queue) { SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var walk = Task.Run(async () =>
        {
            try
            {
                foreach (var candidate in Walk(query, name, stop.Token))
                    await queue.Writer.WriteAsync(candidate, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            var lanes = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = stop.Token,
            };
            await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(stop.Token), lanes, async (candidate, token) =>
            {
                var found = Read(matcher, candidate, token);
                if (found.Matched)
                    await results.WriteAsync(Hit(candidate, found), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            await Settle(walk).ConfigureAwait(false);
        }
    }

    ContentResult Read(IContentMatcher matcher, Candidate candidate, CancellationToken ct)
    {
        ContentResult found;
        try
        {
            found = matcher.Match(candidate.Path, candidate.Size ?? 0, ct);
        }
        // A file can vanish or be locked between the walk and the read.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            found = new ContentResult(false, null, null, SkipReason.Unreadable);
        }

        switch (found.Skip)
        {
            case SkipReason.TooLarge: Interlocked.Increment(ref tooLarge); break;
            case SkipReason.Binary: Interlocked.Increment(ref binary); break;
            case SkipReason.Unreadable: Interlocked.Increment(ref unreadable); break;
        }
        return found;
    }

    IEnumerable<Candidate> Walk(SearchQuery query, NameTest name, CancellationToken ct)
    {
        // Checked before the loop too: a tree with nothing to return never enters it.
        ct.ThrowIfCancellationRequested();

        Walker? walker = null;
        try
        {
            walker = new Walker(this, query, name, ct);
        }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref denied); }
        catch (IOException) { Interlocked.Increment(ref unreadable); }

        if (walker is null) yield break;

        using (walker)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = walker.MoveNext();
                }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref denied); break; }
                catch (IOException) { Interlocked.Increment(ref unreadable); break; }

                if (!moved) break;
                yield return walker.Current;
            }
        }
    }

    static SearchHit Hit(Candidate candidate, ContentResult found = default) => new()
    {
        FullPath = candidate.Path,
        Name = candidate.Name,
        Size = candidate.Size,
        Modified = candidate.Modified,
        Attributes = candidate.Attributes,
        Line = found.Line,
        Preview = found.Preview,
    };

    static async Task Settle(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    readonly record struct Candidate(string Path, string Name, long? Size, DateTime Modified, FileAttributes Attributes);

    /// <summary>One pass over the tree. Every filter the directory record can answer is applied here, before a path is allocated.</summary>
    sealed class Walker : FileSystemEnumerator<Candidate>
    {
        const int AccessDenied = 5;

        readonly FileSearch owner;
        readonly SearchQuery query;
        readonly NameTest name;
        readonly CancellationToken ct;
        bool walking;

        public Walker(FileSearch owner, SearchQuery query, NameTest name, CancellationToken ct)
            : base(query.Root, new EnumerationOptions
            {
                // IgnoreInaccessible would drop denied folders silently; ContinueOnError counts them instead.
                IgnoreInaccessible = false,
                RecurseSubdirectories = true,
                // Skipping is applied by the base enumerator before our callbacks, so hidden is decided below instead.
                AttributesToSkip = 0,
            })
        {
            this.owner = owner;
            this.query = query;
            this.name = name;
            this.ct = ct;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            walking = true;
            ct.ThrowIfCancellationRequested();

            // Following reparse points double-counts at best and never ends on a loop (C:\Users\All Users).
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;

            return query.IncludeHidden || (entry.Attributes & Concealed) == 0;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            walking = true;
            // MoveNext can reject thousands of entries before returning one, so cancellation is checked in here.
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) Interlocked.Increment(ref owner.folders);
            else Interlocked.Increment(ref owner.files);

            if (!query.IncludeHidden && (entry.Attributes & Concealed) != 0) return false;

            // A folder can match a name query, never a content query.
            if (entry.IsDirectory)
                return !query.SearchesContent && name.Matches(entry.FileName);

            if (query.MinSize is { } min && entry.Length < min) return false;
            if (query.MaxSize is { } max && entry.Length > max) return false;

            var modified = entry.LastWriteTimeUtc.LocalDateTime;
            if (query.ModifiedAfter is { } after && modified < after) return false;
            if (query.ModifiedBefore is { } before && modified > before) return false;

            return name.Matches(entry.FileName);
        }

        protected override Candidate TransformEntry(ref FileSystemEntry entry) => new(
            entry.ToFullPath(), entry.FileName.ToString(), entry.IsDirectory ? null : entry.Length,
            entry.LastWriteTimeUtc.LocalDateTime, entry.Attributes);

        /// <summary>Also called from the base constructor when the root cannot be opened; our fields are not set yet,
        /// so that one is left to throw. After the walk has started, errors are counted and stepped over.</summary>
        protected override bool ContinueOnError(int error)
        {
            if (!walking) return false;

            if (error == AccessDenied) Interlocked.Increment(ref owner.denied);
            else Interlocked.Increment(ref owner.unreadable);
            return true;
        }
    }

    /// <summary>The name test, built once per run; it runs off the directory record's span without allocating.</summary>
    sealed class NameTest
    {
        readonly string? text;
        readonly StringComparison how;
        readonly Regex? pattern;

        public NameTest(SearchQuery query)
        {
            how = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (string.IsNullOrEmpty(query.Name)) return;

            if (query.NameMode == NameMatch.Substring)
                text = query.Name;
            else
                pattern = Compile(
                    query.NameMode == NameMatch.Glob ? FromGlob(query.Name) : query.Name, query.CaseSensitive);
        }

        public bool Matches(ReadOnlySpan<char> candidate)
        {
            if (text is not null) return candidate.Contains(text, how);
            if (pattern is null) return true;

            try { return pattern.IsMatch(candidate); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>Anchored, with everything that is not a wildcard escaped: report(1)* must not match report1.txt.</summary>
        static string FromGlob(string glob)
        {
            var pattern = new StringBuilder(glob.Length * 2).Append('^');
            foreach (var c in glob)
                pattern.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
            return pattern.Append('$').ToString();
        }

        static Regex Compile(string pattern, bool caseSensitive)
        {
            var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

            // NonBacktracking bounds a hostile pattern but refuses lookarounds and backreferences;
            // those fall back to the ordinary engine, where the timeout is the only guard.
            try { return new Regex(pattern, options | RegexOptions.NonBacktracking, PatternBudget); }
            catch (NotSupportedException) { return new Regex(pattern, options, PatternBudget); }
        }
    }
}
