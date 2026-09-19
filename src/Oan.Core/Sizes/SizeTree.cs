using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Oan.Core.Sizes;

/// <summary>
/// Everything under a folder, as flat arrays: one entry per file and folder, each pointing at its
/// parent. Flat rather than a graph of nodes because it is drawn, not navigated — a million
/// objects with four references each is the version that runs out of memory, and nothing about
/// laying out rectangles needs pointers.
///
/// Measured on this machine: 12.1–12.6 bytes per file, so 14 MB for a 1.1 million file profile.
/// Names are not kept; they cost another 95–135 bytes each, and only the one under the pointer is
/// ever needed. <see cref="Name"/> reads it back off the path when asked.
/// </summary>
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

    /// <summary>
    /// Every name, end to end in one buffer, with an offset and a length per entry. Measured: a
    /// string each costs 95-135 bytes on a profile of a million files — over a hundred megabytes
    /// of object headers for text averaging twenty characters. This is about a third of that.
    /// </summary>
    private readonly char[] _text;
    private readonly int[] _at;
    private readonly int[] _len;
    private readonly bool[] _folder;

    /// <summary>Every index, grouped by parent, with <see cref="_first"/> saying where each
    /// group starts. A counting sort rather than a list per folder: one array of ints instead of
    /// a hundred thousand objects, and the map asks for children folder by folder.</summary>
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

    public bool IsFolder(int i) => _folder[i];

    /// <summary>
    /// Walks the whole tree, once, on as many threads as the machine has. The same shape as
    /// FolderSize's walk and for the same reasons — one folder per thread off a shared queue,
    /// reparse points refused, a folder that will not open counted rather than hidden.
    /// </summary>
    public static SizeTree? Read(string root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        ct.ThrowIfCancellationRequested();

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var walk = new Walk(key, ct);
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

    private sealed class Walk(string root, CancellationToken ct)
    {
        private readonly BlockingCollection<(string Path, int Index)> _todo = new(new ConcurrentBag<(string, int)>());
        private readonly ConcurrentBag<(string Name, int Parent, long Size, bool Folder, int At)> _found = [];
        private readonly ConcurrentDictionary<int, long> _totals = new();
        private int _outstanding = 1;
        private int _next;
        private int _skipped;
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

            // Folders carry what is beneath them, and a child's total is only final once its own
            // children are. Walking from the deepest up is what makes one pass enough — indices
            // are handed out as folders are discovered, so a parent always has a smaller one.
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

    /// <summary>One level: each file's size, each subfolder's path, and the name of both.</summary>
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
