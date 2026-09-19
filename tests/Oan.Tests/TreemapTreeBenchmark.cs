using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Oan.Core.FileSystem;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What a treemap needs that a size column does not.
///
/// <see cref="FolderSize"/> returns one number per folder and throws every file away as it goes.
/// A treemap needs the opposite: every file, its size, and which folder it is in. The walk is the
/// same walk — the same directories are opened either way — so the question is not "how much
/// slower" but "how much does keeping it cost", in time and in memory.
///
/// Kept out of the normal pass by Category=Benchmark. Run it on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~TreemapTree" --logger "console;verbosity=detailed"
/// Point it elsewhere with OAN_TREEMAP_ROOT.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class TreemapTreeBenchmark(ITestOutputHelper output)
{
    static string Root =>
        Environment.GetEnvironmentVariable("OAN_TREEMAP_ROOT") ?? @"D:\dev";

    [Fact]
    public void Keeping_every_file_against_keeping_one_total()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            output.WriteLine($"missing\t{root}");
            return;
        }

        // What the size column costs today: one threaded walk per child folder, which between
        // them open every directory under the root exactly once.
        var children = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        }).ToArray();

        var clock = Stopwatch.StartNew();
        long totals = 0;
        foreach (var child in children) totals += new FolderSize().Measure(child).Bytes ?? 0;
        var perChildMs = clock.ElapsedMilliseconds;

        output.WriteLine($"totals\t{root}\tchildren={children.Length}\tms={perChildMs}\tbytes={totals}");

        // The same directories, read once, keeping every file. Names are the expensive half and
        // are measured on their own, because a treemap can be built without them and only needs
        // one when the mouse stops moving.
        var bare = Walk(root, names: false);
        var named = Walk(root, names: true);

        foreach (var run in new[] { bare, named })
        {
            output.WriteLine(
                $"tree\t{root}\tnames={run.Names}\tms={run.Ms}\tfiles={run.Files}\tfolders={run.Folders}\t" +
                $"bytes={run.Bytes}\theld_bytes={run.Held}\tper_file_bytes={(double)run.Held / run.Files:F1}");
        }

        output.WriteLine(
            $"delta\t{root}\ttree_vs_totals_ms={named.Ms - perChildMs}\t" +
            $"names_cost_ms={named.Ms - bare.Ms}\tnames_cost_bytes={named.Held - bare.Held}");

        var movedFiles = Math.Abs(named.Files - bare.Files);
        var movedBytes = Math.Abs(named.Bytes - bare.Bytes);

        output.WriteLine(
            $"drift\t{root}\tseconds_apart={(bare.Ms + named.Ms) / 1000.0:F1}\tfiles={movedFiles}\t" +
            $"bytes={movedBytes}\tfile_share={(double)movedFiles / bare.Files:E1}");

        // Not Assert.Equal. The first version of this asserted the two walks agreed exactly, and
        // C:\Users\oance failed it: ten files and 2.1 MB moved in the seven seconds between them.
        // That is not a bug in the walk, it is the profile writing to itself while being read —
        // docs/folder-size-baseline.md measured the same thing on AppData. So what is checked is
        // that both walks saw the same TREE, not that the disk stood still, and the disagreement
        // is printed because it is the answer to "how stale is a treemap the moment it is drawn".
        Assert.InRange((double)movedFiles / bare.Files, 0, 1e-3);
        Assert.InRange((double)movedBytes / bare.Bytes, 0, 1e-3);
    }

    /// <summary>
    /// Time and memory for one collecting walk. The tree is flattened into arrays inside
    /// <see cref="Build"/> and the scratch lists are dead by the time it returns — which is why
    /// the memory is measured after the call and not around it. Measured around it, a Debug build
    /// keeps every local alive to the end of the method and the number is nonsense.
    /// </summary>
    Run Walk(string root, bool names)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var before = GC.GetTotalMemory(true);

        var clock = Stopwatch.StartNew();
        var tree = Build(root, names);
        clock.Stop();

        var held = GC.GetTotalMemory(true) - before;
        var run = new Run(names, clock.ElapsedMilliseconds, tree.Files.Length, tree.FolderParent.Length,
            Sum(tree.Files), held);

        GC.KeepAlive(tree);
        return run;
    }

    static long Sum(long[] sizes)
    {
        long total = 0;
        foreach (var size in sizes) total += size;
        return total;
    }

    static Tree Build(string root, bool names)
    {
        var collect = new Collect(root, names);
        collect.Run(Math.Clamp(Environment.ProcessorCount, 2, 16));
        return collect.Flatten();
    }

    readonly record struct Run(bool Names, long Ms, int Files, int Folders, long Bytes, long Held);

    /// <summary>
    /// The flat form a treemap wants: parallel arrays, one entry per file, plus the folder each
    /// file sits in. Not a node graph — 1.1 million objects with four references each is the
    /// version that runs out of memory, and nothing about drawing rectangles needs pointers.
    /// </summary>
    sealed record Tree(long[] Files, int[] FileParent, string[]? FileName, int[] FolderParent, string[]? FolderName);

    /// <summary>
    /// The same threaded walk <see cref="FolderSize"/> does — a queue of folders, one thread per
    /// core taking one folder each, the count of folders handed out but not finished deciding
    /// when it is over — except that every file is kept instead of added up.
    /// </summary>
    sealed class Collect(string root, bool names)
    {
        readonly BlockingCollection<(string Path, int Id)> _todo = new(new ConcurrentBag<(string, int)>());
        readonly ConcurrentBag<List<(int Parent, long Bytes, string Name)>> _files = [];
        readonly ConcurrentBag<List<(int Id, int Parent, string Name)>> _folders = [];
        int _outstanding = 1;
        int _nextFolder;

        public void Run(int threads)
        {
            _todo.Add((root, 0));

            var workers = new Thread[threads];
            for (var i = 0; i < threads; i++)
            {
                workers[i] = new Thread(Work) { IsBackground = true, Name = "Treemap walk" };
                workers[i].Start();
            }

            foreach (var worker in workers) worker.Join();
            _todo.Dispose();
        }

        public Tree Flatten()
        {
            var fileCount = _files.Sum(list => list.Count);
            var sizes = new long[fileCount];
            var parents = new int[fileCount];
            var fileNames = names ? new string[fileCount] : null;
            var at = 0;

            foreach (var list in _files)
            {
                foreach (var (parent, bytes, name) in list)
                {
                    sizes[at] = bytes;
                    parents[at] = parent;
                    if (fileNames is not null) fileNames[at] = name;
                    at++;
                }
            }

            var folderCount = _nextFolder + 1;
            var folderParents = new int[folderCount];
            var folderNames = names ? new string[folderCount] : null;

            foreach (var list in _folders)
            {
                foreach (var (id, parent, name) in list)
                {
                    if (id >= folderCount) continue;
                    folderParents[id] = parent;
                    if (folderNames is not null) folderNames[id] = name;
                }
            }

            return new Tree(sizes, parents, fileNames, folderParents, folderNames);
        }

        void Work()
        {
            var files = new List<(int, long, string)>(4096);
            var folders = new List<(int, int, string)>(256);
            _files.Add(files);
            _folders.Add(folders);

            foreach (var (path, id) in _todo.GetConsumingEnumerable())
            {
                Read(path, id, files, folders);
                if (Interlocked.Decrement(ref _outstanding) == 0) _todo.CompleteAdding();
            }
        }

        void Read(string path, int id,
            List<(int, long, string)> files, List<(int, int, string)> folders)
        {
            try
            {
                using var level = new Level(path, names);
                while (level.MoveNext())
                {
                    var (name, bytes, folder) = level.Current;
                    if (folder is null)
                    {
                        files.Add((id, bytes, name));
                        continue;
                    }

                    var child = Interlocked.Increment(ref _nextFolder);
                    folders.Add((child, id, name));

                    Interlocked.Increment(ref _outstanding);
                    try { _todo.Add((folder, child)); }
                    catch (InvalidOperationException) { Interlocked.Decrement(ref _outstanding); }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    /// <summary>
    /// One level. <paramref name="names"/> off returns the empty string for every file, which is
    /// the point of the variant: the string allocation is what a per-file tree really costs, and
    /// it has to be measurable on its own.
    /// </summary>
    sealed class Level(string path, bool names)
        : FileSystemEnumerator<(string Name, long Bytes, string? Folder)>(path, new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
        })
    {
        bool _read;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            _read = true;
            if (!entry.IsDirectory) return true;
            return (entry.Attributes & FileAttributes.ReparsePoint) == 0;
        }

        protected override (string Name, long Bytes, string? Folder) TransformEntry(ref FileSystemEntry entry) =>
            entry.IsDirectory
                ? (names ? entry.FileName.ToString() : "", 0L, entry.ToFullPath())
                : (names ? entry.FileName.ToString() : "", entry.Length, null);

        protected override bool ContinueOnError(int error) => _read;
    }
}
