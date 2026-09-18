using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Oan.Core.Search;

/// <summary>
/// Walks a tree and reports what matches, handing over each hit as it is found rather than at
/// the end — on a large tree the first answer arrives in milliseconds and the rest follow.
/// Nothing is indexed and nothing is cached: the answer is what is on disk right now.
///
/// One run per instance at a time; <see cref="Tally"/> belongs to the latest
/// <see cref="RunAsync"/>.
/// </summary>
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

    /// <summary>
    /// Not an iterator, on purpose: a pattern that cannot be parsed has to be refused when the
    /// search is asked for, not when the caller happens to read the first result — otherwise a
    /// broken query is indistinguishable from one that found nothing.
    /// </summary>
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
            // The caller may have walked away mid-stream, leaving the walk blocked on a queue
            // nobody is reading; and a failure inside the run surfaces here rather than as a
            // task nobody waited for.
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

    /// <summary>The walk is one thread because the disk is one disk; reading files is not.</summary>
    async Task Scan(
        SearchQuery query, NameTest name, IContentMatcher matcher, ChannelWriter<SearchHit> results, CancellationToken ct)
    {
        // Bounded: the walk finds paths far faster than files can be read, and an unbounded
        // queue would hold a million of them in memory on a big tree.
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
        // A file can vanish or be locked between the walk and the read. That is one line of the
        // tally, not the end of the search.
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
        // Before the loop, not only inside it: a tree with nothing to return never enters the
        // loop, and a search cancelled before it started must still come back cancelled.
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

    /// <summary>A file worth opening. Its size is the directory record's, never a guess.</summary>
    readonly record struct Candidate(string Path, string Name, long? Size, DateTime Modified, FileAttributes Attributes);

    /// <summary>
    /// One pass over the whole tree. Every filter that can be answered from the directory
    /// record is answered here: name, size and timestamp are already in hand, so rejecting an
    /// entry costs nothing and allocates nothing, while producing a candidate allocates a path
    /// and may cost a file being opened.
    /// </summary>
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
                // IgnoreInaccessible would drop denied folders silently and leave a partial
                // result looking complete; ContinueOnError counts them instead.
                IgnoreInaccessible = false,
                RecurseSubdirectories = true,
                // Applied by the base enumerator before our callbacks run, so hidden has to be
                // decided in one place below or IncludeHidden would be a setting with no effect.
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

            // C:\Users\All Users is a junction to ProgramData on every Windows install:
            // following reparse points double-counts a tree at best and, on a loop, never ends.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;

            return query.IncludeHidden || (entry.Attributes & Concealed) == 0;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            walking = true;
            // MoveNext can reject thousands of entries before it returns one, so the check has
            // to live inside the callback: outside it, a cancelled search would keep reading
            // the disk until the next match, which on a large tree can be never.
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) Interlocked.Increment(ref owner.folders);
            else Interlocked.Increment(ref owner.files);

            if (!query.IncludeHidden && (entry.Attributes & Concealed) != 0) return false;

            // A folder is a result when the question is about names. It cannot be one when the
            // question is about contents, because there is nothing inside a folder to read — and
            // returning folders there would be a hit the user cannot explain.
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

        /// <summary>
        /// Also called from the base constructor, when the root itself cannot be opened — our
        /// own fields are not set yet at that point, so that one is let through as an exception
        /// and classified by the caller. Everything after the walk has started is counted and
        /// stepped over: one denied folder does not end a search.
        /// </summary>
        protected override bool ContinueOnError(int error)
        {
            if (!walking) return false;

            if (error == AccessDenied) Interlocked.Increment(ref owner.denied);
            else Interlocked.Increment(ref owner.unreadable);
            return true;
        }
    }

    /// <summary>
    /// The name test, built once per run. It runs straight off the directory record's span, so
    /// a name that does not match costs no allocation at all.
    /// </summary>
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

        /// <summary>
        /// Anchored, with everything that is not a wildcard escaped: let the metacharacters
        /// through and the glob report(1)* quietly becomes a pattern that also matches
        /// report1.txt.
        /// </summary>
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

            // NonBacktracking bounds what a hostile pattern can cost. It refuses lookarounds
            // and backreferences, which SearchQuery.Invalid() accepts as valid regex — those
            // fall back to the ordinary engine, where the timeout is the only guard left.
            try { return new Regex(pattern, options | RegexOptions.NonBacktracking, PatternBudget); }
            catch (NotSupportedException) { return new Regex(pattern, options, PatternBudget); }
        }
    }
}
