using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Oan.Tests;

/// <summary>What the bag holds today, as a struct in a list one thread owns.</summary>
readonly record struct Held(string Name, int Parent, long Size, bool Folder, int At);

/// <summary>The same entry with the name already copied into that thread's character buffer.</summary>
readonly record struct Packed(int Parent, long Size, bool Folder, int At, int NameAt, int NameLen);

/// <summary>
/// One change from the shipped walk and nothing else: the ConcurrentBag becomes a plain List per
/// worker thread, merged once every thread has joined. Nothing else moves — the same threads, the
/// same queue, the same totals dictionary, the same string per entry — so the difference the
/// benchmark reports is the bag's, not a rewrite's.
/// </summary>
sealed class BufferWalk(string root, CancellationToken ct = default)
{
    readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
    readonly ConcurrentDictionary<int, long> _totals = new();
    List<Held>[] _perThread = [];
    int _outstanding = 1;
    int _next;
    int _skipped;
    int _notFollowed;
    bool _rootRefused;

    public bool Run(int threads)
    {
        _todo.Add((root, -1));
        _perThread = new List<Held>[threads];

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var mine = _perThread[i] = new List<Held>(1 << 12);
            workers[i] = new Thread(() => Work(mine)) { IsBackground = true, Name = "Size tree" };
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
        var count = 0;
        var room = 0;
        foreach (var list in _perThread)
        {
            count += list.Count;
            foreach (var entry in list) room += entry.Name.Length;
        }

        var size = new long[count];
        var parent = new int[count];
        var folder = new bool[count];
        var at = new int[count];
        var len = new int[count];
        var text = new char[room];

        var written = 0;
        foreach (var list in _perThread)
        {
            foreach (var (label, owner, bytes, isFolder, index) in list)
            {
                size[index] = isFolder ? _totals.GetValueOrDefault(index) : bytes;
                parent[index] = owner;
                folder[index] = isFolder;

                label.AsSpan().CopyTo(text.AsSpan(written));
                at[index] = written;
                len[index] = label.Length;
                written += label.Length;
            }
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

    void Work(List<Held> mine)
    {
        foreach (var (path, index) in _todo.GetConsumingEnumerable())
        {
            if (!ct.IsCancellationRequested) Read(path, index, mine);
            if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
        }
    }

    void Read(string path, int index, List<Held> mine)
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
                    mine.Add(new Held(label, index, bytes, false, at));
                    continue;
                }

                mine.Add(new Held(label, index, 0, true, at));
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

/// <summary>
/// One further change: no string per entry. The name is copied out of the enumerator's own buffer
/// straight into a character buffer the thread owns, and the entry keeps an offset into it. At
/// the end each thread's characters land in the final buffer in one block copy, so the merge is
/// one memcpy per thread instead of one per file. Folders still cost a string — the queue is
/// paths — but folders are a twentieth of the entries.
/// </summary>
sealed class CharWalk(string root, CancellationToken ct = default)
{
    readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
    readonly ConcurrentDictionary<int, long> _totals = new();
    List<Packed>[] _perThread = [];
    Names[] _names = [];
    int _outstanding = 1;
    int _next;
    int _skipped;
    int _notFollowed;
    bool _rootRefused;

    public bool Run(int threads)
    {
        _todo.Add((root, -1));
        _perThread = new List<Packed>[threads];
        _names = new Names[threads];

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var mine = _perThread[i] = new List<Packed>(1 << 12);
            var names = _names[i] = new Names();
            workers[i] = new Thread(() => Work(mine, names)) { IsBackground = true, Name = "Size tree" };
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
        var count = 0;
        var room = 0;
        foreach (var list in _perThread) count += list.Count;
        foreach (var names in _names) room += names.Length;

        var size = new long[count];
        var parent = new int[count];
        var folder = new bool[count];
        var at = new int[count];
        var len = new int[count];
        var text = new char[room];

        var written = 0;
        for (var t = 0; t < _perThread.Length; t++)
        {
            _names[t].CopyTo(text.AsSpan(written));

            foreach (var (owner, bytes, isFolder, index, nameAt, nameLen) in _perThread[t])
            {
                size[index] = isFolder ? _totals.GetValueOrDefault(index) : bytes;
                parent[index] = owner;
                folder[index] = isFolder;
                at[index] = written + nameAt;
                len[index] = nameLen;
            }

            written += _names[t].Length;
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

    void Work(List<Packed> mine, Names names)
    {
        foreach (var (path, index) in _todo.GetConsumingEnumerable())
        {
            if (!ct.IsCancellationRequested) Read(path, index, mine, names);
            if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
        }
    }

    void Read(string path, int index, List<Packed> mine, Names names)
    {
        try
        {
            using var level = new CharLevel(path, names);
            var here = 0L;

            while (level.MoveNext())
            {
                if (ct.IsCancellationRequested) break;

                var (bytes, child, nameAt, nameLen) = level.Current;
                var at = Interlocked.Increment(ref _next) - 1;

                if (child is null)
                {
                    here += bytes;
                    mine.Add(new Packed(index, bytes, false, at, nameAt, nameLen));
                    continue;
                }

                mine.Add(new Packed(index, 0, true, at, nameAt, nameLen));
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

/// <summary>
/// The same level, handing back where the name was written instead of a string holding it. The
/// buffer belongs to the calling thread, so no lock: one thread reads one folder at a time.
/// </summary>
sealed class CharLevel : FileSystemEnumerator<(long Bytes, string? Folder, int At, int Len)>
{
    readonly Names _names;
    bool _readAnEntry;

    public CharLevel(string path, Names names) : base(path, new EnumerationOptions
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    })
    {
        _names = names;
    }

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

    protected override (long Bytes, string? Folder, int At, int Len) TransformEntry(ref FileSystemEntry entry)
    {
        var name = entry.FileName;
        var at = _names.Add(name);
        return entry.IsDirectory
            ? (0, entry.ToFullPath(), at, name.Length)
            : (entry.Length, null, at, name.Length);
    }

    protected override bool ContinueOnError(int error)
    {
        if (!_readAnEntry) return false;
        Skipped++;
        return true;
    }
}
