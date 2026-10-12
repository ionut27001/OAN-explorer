using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace Oan.Tests;

/// <summary>
/// The arrays a walk ends up with, without SizeTree's reading surface. The benchmarks need three
/// things of a result and nothing else: to hold it so it can be weighed, to say what it would
/// cost in memory, and to be comparable against a walk gathered a different way.
/// </summary>
sealed class Flat(long[] size, int[] parent, bool[] folder, char[] text, int[] at, int[] len,
                  int[] children, int[] first, int skipped, int notFollowed)
{
    public long[] Size { get; } = size;
    public int[] Parent { get; } = parent;
    public bool[] Folder { get; } = folder;
    public char[] Text { get; } = text;
    public int[] At { get; } = at;
    public int[] Len { get; } = len;
    public int[] Children { get; } = children;
    public int[] First { get; } = first;
    public int Skipped { get; } = skipped;
    public int NotFollowed { get; } = notFollowed;

    public int Count => Size.Length;

    /// <summary>
    /// What the arrays cost, counted rather than guessed: eight bytes of size, four each of
    /// parent, offset, length, child and group start, one of folder, plus two per character of
    /// name. Array headers are 24 bytes each and there are eight of them, so they round off.
    /// </summary>
    public long Bytes => (8L + 4 + 4 + 4 + 1 + 4) * Count + 4L * (Count + 2) + 2L * Text.Length + 8 * 24;

    /// <summary>
    /// Every number the same tree has to produce however it was gathered, and none that depends
    /// on the order the threads happened to finish in — indices are handed out by whoever reaches
    /// the folder first, so comparing entry by entry compares the scheduler, not the walk.
    /// </summary>
    public (int Count, int Folders, long Root, long All, long Chars, long Names) Fingerprint()
    {
        var folders = 0;
        long all = 0, chars = 0, names = 0;

        for (var i = 0; i < Count; i++)
        {
            if (Folder[i]) folders++;
            all += Size[i];
            chars += Len[i];
            names += string.GetHashCode(Text.AsSpan(At[i], Len[i]));
        }

        long root = 0;
        foreach (var i in Children.AsSpan(First[0], First[1] - First[0])) root += Size[i];

        return (Count, folders, root, all, chars, names);
    }
}

/// <summary>
/// A character buffer one thread owns: names copied straight in, an offset handed back, no string
/// anywhere. In chunks rather than one array that doubles — the first version doubled, and the
/// copies it left behind cost MORE than the strings it was replacing: 450 MB of churn against the
/// bag's 420 on a million-file profile. A chunk is allocated once and never moved, so the churn is
/// the text and nothing else, and the offsets stay simple because they count used characters
/// across the chunks, which is exactly the order the chunks are copied out in.
/// </summary>
sealed class Names
{
    const int Room = 1 << 16;

    readonly List<char[]> _chunks = [];
    readonly List<int> _used = [];
    char[] _current = [];
    int _at;

    public int Length { get; private set; }

    public int Add(ReadOnlySpan<char> name)
    {
        if (_at + name.Length > _current.Length)
        {
            if (_chunks.Count > 0) _used[^1] = _at;
            _current = new char[Math.Max(Room, name.Length)];
            _chunks.Add(_current);
            _used.Add(0);
            _at = 0;
        }

        name.CopyTo(_current.AsSpan(_at));
        _at += name.Length;

        var start = Length;
        Length += name.Length;
        return start;
    }

    /// <summary>The used part of every chunk, in order: the concatenation the offsets index into.</summary>
    public void CopyTo(Span<char> text)
    {
        var written = 0;
        for (var i = 0; i < _chunks.Count; i++)
        {
            var used = i == _chunks.Count - 1 ? _at : _used[i];
            _chunks[i].AsSpan(0, used).CopyTo(text[written..]);
            written += used;
        }
    }
}

/// <summary>One level, as SizeTree reads it: a file's size, a subfolder's path, the name of both.</summary>
sealed class WalkLevel : FileSystemEnumerator<(long Bytes, string? Folder, string Name)>
{
    bool _readAnEntry;

    public WalkLevel(string path) : base(path, new EnumerationOptions
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

    protected override (long Bytes, string? Folder, string Name) TransformEntry(ref FileSystemEntry entry) =>
        entry.IsDirectory
            ? (0, entry.ToFullPath(), entry.FileName.ToString())
            : (entry.Length, null, entry.FileName.ToString());

    protected override bool ContinueOnError(int error)
    {
        if (!_readAnEntry) return false;
        Skipped++;
        return true;
    }
}

/// <summary>
/// A copy of SizeTree.Walk, kept here because the original is private and because a benchmark
/// that cannot time the two halves separately cannot say which half is the problem. Run and
/// Freeze are the shipped code line for line; only the return type is different, so the split it
/// reports is the split the shipped walk has. The benchmark checks the copy against the original
/// on a tree it built itself, so a copy that has drifted is caught rather than believed.
/// </summary>
sealed class BagWalk(string root, CancellationToken ct = default)
{
    readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
    readonly ConcurrentBag<(string Name, int Parent, long Size, bool Folder, int At)> _found = [];
    readonly ConcurrentDictionary<int, long> _totals = new();
    int _outstanding = 1;
    int _next;
    int _skipped;
    int _notFollowed;
    bool _rootRefused;

    public bool Run(int threads)
    {
        _todo.Add((root, -1));

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = new Thread(Work) { IsBackground = true, Name = "Size tree" };
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
        return !_rootRefused;
    }

    public Flat Freeze()
    {
        var found = _found.ToArray();
        var count = found.Length;

        var size = new long[count];
        var parent = new int[count];
        var folder = new bool[count];
        var at = new int[count];
        var len = new int[count];

        var room = 0;
        foreach (var entry in found) room += entry.Name.Length;
        var text = new char[room];

        var written = 0;
        foreach (var (label, owner, bytes, isFolder, index) in found)
        {
            size[index] = isFolder ? _totals.GetValueOrDefault(index) : bytes;
            parent[index] = owner;
            folder[index] = isFolder;

            label.AsSpan().CopyTo(text.AsSpan(written));
            at[index] = written;
            len[index] = label.Length;
            written += label.Length;
        }

        for (var i = count - 1; i >= 0; i--)
        {
            if (parent[i] >= 0) size[parent[i]] += folder[i] ? size[i] : 0;
        }

        var first = new int[count + 2];
        foreach (var owner in parent) first[owner + 2]++;
        for (var i = 1; i < first.Length; i++) first[i] += first[i - 1];

        var children = new int[count];
        var cursor = (int[])first.Clone();
        for (var i = 0; i < count; i++) children[cursor[parent[i] + 1]++] = i;

        return new Flat(size, parent, folder, text, at, len, children, first, _skipped, _notFollowed);
    }

    /// <summary>
    /// The same Freeze with a clock between its steps, so "Freeze is slow" can be answered with
    /// which part of it. Kept beside the real one rather than instead of it: a Freeze with four
    /// stopwatches in it is not the Freeze that ships, and the plain one above stays comparable.
    /// </summary>
    public (Flat Tree, long DrainMs, long ScatterMs, long RollupMs, long SortMs) FreezeInParts()
    {
        var clock = Stopwatch.StartNew();
        var found = _found.ToArray();
        var count = found.Length;
        var drain = clock.ElapsedMilliseconds;

        clock.Restart();
        var size = new long[count];
        var parent = new int[count];
        var folder = new bool[count];
        var at = new int[count];
        var len = new int[count];

        var room = 0;
        foreach (var entry in found) room += entry.Name.Length;
        var text = new char[room];

        var written = 0;
        foreach (var (label, owner, bytes, isFolder, index) in found)
        {
            size[index] = isFolder ? _totals.GetValueOrDefault(index) : bytes;
            parent[index] = owner;
            folder[index] = isFolder;

            label.AsSpan().CopyTo(text.AsSpan(written));
            at[index] = written;
            len[index] = label.Length;
            written += label.Length;
        }
        var scatter = clock.ElapsedMilliseconds;

        clock.Restart();
        for (var i = count - 1; i >= 0; i--)
        {
            if (parent[i] >= 0) size[parent[i]] += folder[i] ? size[i] : 0;
        }
        var rollup = clock.ElapsedMilliseconds;

        clock.Restart();
        var first = new int[count + 2];
        foreach (var owner in parent) first[owner + 2]++;
        for (var i = 1; i < first.Length; i++) first[i] += first[i - 1];

        var children = new int[count];
        var cursor = (int[])first.Clone();
        for (var i = 0; i < count; i++) children[cursor[parent[i] + 1]++] = i;
        var sort = clock.ElapsedMilliseconds;

        return (new Flat(size, parent, folder, text, at, len, children, first, _skipped, _notFollowed),
                drain, scatter, rollup, sort);
    }

    void Work()
    {
        foreach (var (path, index) in _todo.GetConsumingEnumerable())
        {
            if (!ct.IsCancellationRequested) Read(path, index);
            if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
        }
    }

    void Read(string path, int index)
    {
        try
        {
            using var level = new WalkLevel(path);
            var here = 0L;

            while (level.MoveNext())
            {
                if (ct.IsCancellationRequested) break;

                var (bytes, child, label) = level.Current;
                var at = Interlocked.Increment(ref _next) - 1;

                if (child is null)
                {
                    here += bytes;
                    _found.Add((label, index, bytes, false, at));
                    continue;
                }

                _found.Add((label, index, 0, true, at));
                Interlocked.Increment(ref _outstanding);
                try { _todo.Add((child, at)); }
                catch (InvalidOperationException) { Interlocked.Decrement(ref _outstanding); }
            }

            if (index >= 0) _totals[index] = here;
            Interlocked.Add(ref _skipped, level.Skipped);
            Interlocked.Add(ref _notFollowed, level.NotFollowed);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            if (index < 0) _rootRefused = true;
            else Interlocked.Increment(ref _skipped);
        }
    }
}
