using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Oan.Core.Sizes;

/// <summary>Everything under a folder, as flat arrays: one entry per file and folder, each pointing at its parent.
/// Measured: 67 bytes per entry (82 MB for 1.1 million files); the walk that builds it peaks at 285 MB alive.</summary>
public sealed class SizeTree
{
    private SizeTree(string root, long[] size, int[] parent, char[] text, int[] at, int[] len,
                     bool[] folder, int[] children, int[] first, int skipped, int notFollowed)
    {
        Root = root;
        Size = size;
        Parent = parent;
        _text = text;
        _at = at;
        _len = len;
        _folder = folder;
        _children = children;
        _first = first;
        Skipped = skipped;
        NotFollowed = notFollowed;
    }

    /// <summary>Every name end to end in one buffer, with an offset and a length per entry. Measured: a string each costs 95-135 bytes.</summary>
    private readonly char[] _text;
    private readonly int[] _at;
    private readonly int[] _len;
    private readonly bool[] _folder;

    /// <summary>Every index, grouped by parent (counting sort); <see cref="_first"/> says where each group starts.</summary>
    private readonly int[] _children;
    private readonly int[] _first;

    public string Root { get; }

    /// <summary>Bytes: a file's own length, a folder's total including everything below it.</summary>
    public long[] Size { get; }

    /// <summary>Index of the containing folder, or -1 for a child of the root.</summary>
    public int[] Parent { get; }

    /// <summary>Entries that could not be read. Their contents are missing from every total above them.</summary>
    public int Skipped { get; }

    /// <summary>Junctions and symlinks deliberately not descended into.</summary>
    public int NotFollowed { get; }

    public int Count => Size.Length;

    public string Name(int i) => new(_text, _at[i], _len[i]);

    /// <summary>The full path of one entry, built on demand by walking up the parents.</summary>
    public string PathOf(int i)
    {
        var parts = new Stack<string>();
        for (var at = i; at >= 0; at = Parent[at]) parts.Push(Name(at));

        return System.IO.Path.Combine([Root, .. parts]);
    }

    public bool IsFolder(int i) => _folder[i];

    /// <summary>Walks the whole tree once, on many threads; same shape as FolderSize's walk.</summary>
    /// <param name="found">Entries read so far, raised on a walking thread every few thousand. There is no total to count towards.</param>
    public static SizeTree? Read(string root, CancellationToken ct = default, Action<int>? found = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        ct.ThrowIfCancellationRequested();

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var walk = new Walk(key, ct, found);
        if (!walk.Run(Math.Clamp(Environment.ProcessorCount, 2, 16))) return null;

        ct.ThrowIfCancellationRequested();
        return walk.Freeze();
    }

    /// <summary>The entries directly inside one folder, or inside the root when it is -1.</summary>
    public ReadOnlySpan<int> ChildrenOf(int folder)
    {
        var at = folder + 1;
        return _children.AsSpan(_first[at], _first[at + 1] - _first[at]);
    }

    private sealed class Walk(string root, CancellationToken ct, Action<int>? found = null)
    {
        private readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
        private readonly ConcurrentBag<(string Name, int Parent, long Size, bool Folder, int At)> _found = [];
        private readonly ConcurrentDictionary<int, long> _totals = new();
        private int _outstanding = 1;
        private int _next;
        private int _skipped;
        private int _reported;
        private int _notFollowed;
        private bool _rootRefused;

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

        public SizeTree Freeze()
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

            // Deepest first makes one pass enough: indices are handed out as folders are discovered,
            // so a parent always has a smaller one than its children.
            for (var i = count - 1; i >= 0; i--)
            {
                if (parent[i] >= 0) size[parent[i]] += folder[i] ? size[i] : 0;
            }

            // Counting sort by parent, with the root's children at slot 0 since its index is -1.
            var first = new int[count + 2];
            foreach (var owner in parent) first[owner + 2]++;
            for (var i = 1; i < first.Length; i++) first[i] += first[i - 1];

            var children = new int[count];
            var cursor = (int[])first.Clone();
            for (var i = 0; i < count; i++) children[cursor[parent[i] + 1]++] = i;

            return new SizeTree(root, size, parent, text, at, len, folder, children, first,
                                _skipped, _notFollowed);
        }

        private void Work()
        {
            foreach (var (path, index) in _todo.GetConsumingEnumerable())
            {
                if (!ct.IsCancellationRequested) Read(path, index);
                if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
            }
        }

        /// <summary>One message per few thousand entries; a callback per directory would cost more than reading it.</summary>
        private void Report()
        {
            if (found is null) return;

            var now = Volatile.Read(ref _next);
            if (now - Volatile.Read(ref _reported) < 4096) return;

            Volatile.Write(ref _reported, now);
            found(now);
        }

        private void Read(string path, int index)
        {
            try
            {
                using var level = new Level(path);
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
                Report();
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

    private sealed class Level : FileSystemEnumerator<(long Bytes, string? Folder, string Name)>
    {
        private bool _readAnEntry;

        public Level(string path) : base(path, new EnumerationOptions
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
}
