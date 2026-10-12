using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oan.Core.Sizes;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What SizeTree.Read really costs, split into the parts that can be paid off separately: the
/// enumeration the operating system charges for, the bookkeeping the walk adds on top, and the
/// memory that is alive only until Freeze has finished against the memory that survives it.
///
/// Kept out of the normal pass by Category=Benchmark, which tests/Oan.Tests/Oan.Tests.csproj
/// excludes. Run it on purpose, in Release — a Debug walk measures the JIT, not the walk:
///   dotnet test -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
/// Point it elsewhere with OAN_TREE_BENCH_ROOTS (semicolon-separated).
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class SizeTreeBenchmark(ITestOutputHelper output)
{
    static string[] Roots =>
        (Environment.GetEnvironmentVariable("OAN_TREE_BENCH_ROOTS")
         ?? $@"D:\dev;{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static int Threads => Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>
    /// The shipped entry point first, so every later number is anchored to something that was
    /// actually measured through the public door, and then the same walk split in two.
    /// </summary>
    [Fact]
    public void Where_the_time_and_the_memory_go()
    {
        foreach (var root in Roots)
        {
            if (!Exists(root)) continue;
            Warm(root);
            OneRoot(root);
        }
    }

    // A method of its own per root: a tree still reachable from the loop when the next baseline is
    // read, and unreachable when its total is, subtracts itself out of the next root's figure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void OneRoot(string root)
    {
        var shipped = Take("SizeTree.Read", () =>
        {
            var clock = Stopwatch.StartNew();
            var tree = SizeTree.Read(root)!;
            return (tree, clock.ElapsedMilliseconds, 0L, GC.GetTotalMemory(false), GC.GetTotalMemory(false));
        }, tree => $"entries={tree.Count}");

        output.WriteLine($"shipped\t{root}\t{Line(shipped)}\t{shipped.Note}");

        var split = Take("BagWalk", () => BagOnce(root, Threads), flat => $"{Shape(flat)}\t{Survives(flat)}");
        output.WriteLine($"clone\t{root}\t{Line(split)}\t{split.Note}");
        output.WriteLine($"phase\t{root}\twalk_ms={split.FirstMs}\tfreeze_ms={split.SecondMs}\t" +
                         $"freeze_share={Share(split.SecondMs, split.FirstMs + split.SecondMs)}\t" +
                         $"after_walk_mb={Mb(split.AfterFirst)}\tafter_freeze_mb={Mb(split.AfterSecond)}\t" +
                         $"retained_mb={Mb(split.Retained)}");
        output.WriteLine($"process\t{root}\t{Footprint()}");
    }

    /// <summary>
    /// Four walks over the same tree: what the operating system charges on its own, what the
    /// shipped bookkeeping adds, what per-thread buffers give back, and what dropping the string
    /// per entry gives back on top of that. Two rounds, because the first one is where a cold
    /// metadata cache and a cold JIT land.
    /// </summary>
    [Fact]
    public void The_bag_the_buffers_and_no_strings_against_the_floor()
    {
        foreach (var root in Roots)
        {
            if (!Exists(root)) continue;
            Warm(root);

            for (var round = 1; round <= 2; round++)
            {
                var floor = Take("floor", () => FloorOnce(root, Threads), c => $"entries={c.Count}");
                var bag = Take("bag", () => BagOnce(root, Threads), f => $"{f.Fingerprint()}");
                var buffers = Take("buffers", () => BufferOnce(root, Threads), f => $"{f.Fingerprint()}");
                var packed = Take("chars", () => CharOnce(root, Threads), f => $"{f.Fingerprint()}");

                foreach (var trial in new[] { floor, bag, buffers, packed })
                    output.WriteLine($"round{round}\t{root}\t{Line(trial)}");

                output.WriteLine($"agree{round}\t{root}\t{floor.Note}\tbag={bag.Note}\tbuffers={buffers.Note}\t" +
                                 $"chars={packed.Note}\tsame={bag.Note == buffers.Note && bag.Note == packed.Note}");

                output.WriteLine($"delta{round}\t{root}\t" +
                                 $"bag_over_floor_ms={bag.FirstMs - floor.FirstMs}\t" +
                                 $"buffers_save_walk_ms={bag.FirstMs - buffers.FirstMs}\t" +
                                 $"buffers_save_freeze_ms={bag.SecondMs - buffers.SecondMs}\t" +
                                 $"chars_save_walk_ms={buffers.FirstMs - packed.FirstMs}\t" +
                                 $"chars_save_freeze_ms={buffers.SecondMs - packed.SecondMs}\t" +
                                 $"bag_peak_mb={Mb(bag.Peak)}\tbuffers_peak_mb={Mb(buffers.Peak)}\tchars_peak_mb={Mb(packed.Peak)}\t" +
                                 $"bag_alloc_mb={Mb(bag.Allocated)}\tbuffers_alloc_mb={Mb(buffers.Allocated)}\tchars_alloc_mb={Mb(packed.Allocated)}\t" +
                                 $"bag_kept_mb={Mb(bag.Retained)}\tbuffers_kept_mb={Mb(buffers.Retained)}\tchars_kept_mb={Mb(packed.Retained)}");
            }
        }
    }

    /// <summary>
    /// Freeze, step by step: draining the bag into an array, packing the names and scattering the
    /// entries into the arrays, rolling the totals up from the deepest entry, and the counting
    /// sort that groups children by parent.
    /// </summary>
    [Fact]
    public void What_freeze_spends_its_time_on()
    {
        foreach (var root in Roots)
        {
            if (!Exists(root)) continue;
            Warm(root);
            output.WriteLine($"freeze\t{root}\t{Parts(root)}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    string Parts(string root)
    {
        var clock = Stopwatch.StartNew();
        var walk = new BagWalk(root);
        walk.Run(Threads);
        var walkMs = clock.ElapsedMilliseconds;

        var (tree, drain, scatter, rollup, sort) = walk.FreezeInParts();
        return $"walk_ms={walkMs}\tdrain_ms={drain}\tscatter_ms={scatter}\trollup_ms={rollup}\t" +
               $"sort_ms={sort}\tfreeze_ms={drain + scatter + rollup + sort}\tentries={tree.Count}\t" +
               $"chars={tree.Text.Length}";
    }

    /// <summary>
    /// A tree this test built itself, so the walks can be held to agreeing exactly rather than
    /// approximately: a profile changes under a benchmark, a temp folder does not. If a prototype
    /// disagrees here, every number it produced above is about a different tree.
    /// </summary>
    [Fact]
    public void Every_prototype_agrees_with_the_shipped_walk()
    {
        using var temp = new TempFolder();
        Build(temp.Root, breadth: 6, depth: 3, files: 7);

        var shipped = Fingerprint(SizeTree.Read(temp.Root)!);

        var bag = new BagWalk(temp.Root);
        bag.Run(Threads);
        var buffers = new BufferWalk(temp.Root);
        buffers.Run(Threads);
        var chars = new CharWalk(temp.Root);
        chars.Run(Threads);

        output.WriteLine($"agree\t{temp.Root}\tshipped={shipped}");

        Assert.Equal(shipped, bag.Freeze().Fingerprint());
        Assert.Equal(shipped, buffers.Freeze().Fingerprint());
        Assert.Equal(shipped, chars.Freeze().Fingerprint());
    }

    // The walks, each in a method of its own so that when it returns the walk object is
    // unreachable — a local still in scope keeps the bag alive through the measuring collection,
    // and the retained figure then includes the very thing it is supposed to exclude.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Flat Tree, long WalkMs, long FreezeMs, long AfterWalk, long AfterFreeze) BagOnce(string root, int threads)
    {
        var clock = Stopwatch.StartNew();
        var walk = new BagWalk(root);
        walk.Run(threads);
        var walkMs = clock.ElapsedMilliseconds;
        var afterWalk = GC.GetTotalMemory(false);

        clock.Restart();
        var tree = walk.Freeze();
        return (tree, walkMs, clock.ElapsedMilliseconds, afterWalk, GC.GetTotalMemory(false));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Flat Tree, long WalkMs, long FreezeMs, long AfterWalk, long AfterFreeze) BufferOnce(string root, int threads)
    {
        var clock = Stopwatch.StartNew();
        var walk = new BufferWalk(root);
        walk.Run(threads);
        var walkMs = clock.ElapsedMilliseconds;
        var afterWalk = GC.GetTotalMemory(false);

        clock.Restart();
        var tree = walk.Freeze();
        return (tree, walkMs, clock.ElapsedMilliseconds, afterWalk, GC.GetTotalMemory(false));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Flat Tree, long WalkMs, long FreezeMs, long AfterWalk, long AfterFreeze) CharOnce(string root, int threads)
    {
        var clock = Stopwatch.StartNew();
        var walk = new CharWalk(root);
        walk.Run(threads);
        var walkMs = clock.ElapsedMilliseconds;
        var afterWalk = GC.GetTotalMemory(false);

        clock.Restart();
        var tree = walk.Freeze();
        return (tree, walkMs, clock.ElapsedMilliseconds, afterWalk, GC.GetTotalMemory(false));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Counted Tree, long WalkMs, long FreezeMs, long AfterWalk, long AfterFreeze) FloorOnce(string root, int threads)
    {
        var clock = Stopwatch.StartNew();
        var walk = new FloorWalk(root);
        walk.Run(threads);
        var walkMs = clock.ElapsedMilliseconds;
        var afterWalk = GC.GetTotalMemory(false);
        return (new Counted(walk.Count, walk.Bytes), walkMs, 0, afterWalk, GC.GetTotalMemory(false));
    }

    /// <summary>What the floor keeps: two numbers, so that it keeps nothing worth weighing.</summary>
    sealed record Counted(int Count, long Bytes);

    sealed record Trial(string Label, string Note, long FirstMs, long SecondMs, long AfterFirst,
                        long AfterSecond, long Peak, long Allocated, long Retained,
                        int Gen0, int Gen1, int Gen2);

    /// <summary>
    /// One measured walk, and the result never leaves: the tree is described while it is alive and
    /// dropped before the next baseline is taken. Handing it back to the caller is what made the
    /// first version lie — the previous root's tree was alive when the baseline was read and dead
    /// by the time the total was, so the second root appeared to retain four megabytes.
    /// </summary>
    static Trial Take<T>(string label,
        Func<(T Result, long FirstMs, long SecondMs, long AfterFirst, long AfterSecond)> once,
        Func<T, string>? describe = null) where T : class
    {
        Settle();
        var baseline = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);

        var water = new HighWater();
        var run = once();
        var churn = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var peak = water.Stop();
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        Settle();
        var retained = GC.GetTotalMemory(true) - baseline;
        var note = describe?.Invoke(run.Result) ?? "";
        GC.KeepAlive(run.Result);

        return new Trial(label, note, run.FirstMs, run.SecondMs, run.AfterFirst - baseline,
            run.AfterSecond - baseline, peak - baseline, churn, retained, gen0, gen1, gen2);
    }

    /// <summary>
    /// The high-water mark of the managed heap while the walk runs. Polled rather than hooked:
    /// the .NET GC will tell you what is allocated now and what was allocated in total, but not
    /// what the most was at once. One sampling thread out of sixteen workers is cheap enough that
    /// the walk it watches is still the walk that ships.
    /// </summary>
    sealed class HighWater
    {
        readonly Thread _thread;
        long _max;
        volatile bool _stop;

        public HighWater()
        {
            _thread = new Thread(Sample) { IsBackground = true, Name = "high water" };
            _thread.Start();
        }

        public long Stop()
        {
            _stop = true;
            _thread.Join();
            return _max;
        }

        void Sample()
        {
            while (!_stop)
            {
                var now = GC.GetTotalMemory(false);
                if (now > _max) _max = now;
                Thread.Sleep(1);
            }
        }
    }

    static void Settle()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    static string Line(Trial t) =>
        $"{t.Label}\tfirst_ms={t.FirstMs}\tsecond_ms={t.SecondMs}\tpeak_mb={Mb(t.Peak)}\t" +
        $"retained_mb={Mb(t.Retained)}\talloc_mb={Mb(t.Allocated)}\tgc={t.Gen0}/{t.Gen1}/{t.Gen2}";

    static string Shape(Flat f) =>
        $"entries={f.Count}\tchars={f.Text.Length}\tarrays_mb={Mb(f.Bytes)}\t" +
        $"bytes_per_entry={Per(f.Bytes, f.Count)}\tchars_per_entry={Per(f.Text.Length, f.Count)}";

    /// <summary>
    /// The three heaps the walk builds, priced: what the tree keeps, what the strings cost while
    /// they are alive, and what the entries cost. A string is 22 bytes of header and terminator
    /// plus two per character, rounded up to eight; an entry is 32 once the reference, the long,
    /// the two ints and the bool are padded.
    /// </summary>
    static string Survives(Flat f)
    {
        long strings = 0;
        for (var i = 0; i < f.Count; i++) strings += (22 + 2L * f.Len[i] + 7) / 8 * 8;

        return $"tree_mb={Mb(f.Bytes)}\tstrings_mb={Mb(strings)}\tentries_mb={Mb(32L * f.Count)}\t" +
               $"copy_mb={Mb(32L * f.Count)}\tstring_bytes_per_entry={Per(strings, f.Count)}";
    }

    static string Footprint()
    {
        var info = GC.GetGCMemoryInfo();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return $"heap_mb={Mb(GC.GetTotalMemory(false))}\tcommitted_mb={Mb(info.TotalCommittedBytes)}\t" +
               $"working_set_mb={Mb(self.WorkingSet64)}\tprivate_mb={Mb(self.PrivateMemorySize64)}\t" +
               $"server_gc={System.Runtime.GCSettings.IsServerGC}";
    }

    static (int Count, int Folders, long Root, long All, long Chars, long Names) Fingerprint(SizeTree tree)
    {
        var folders = 0;
        long all = 0, chars = 0, names = 0;

        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsFolder(i)) folders++;
            all += tree.Size[i];
            var name = tree.Name(i);
            chars += name.Length;
            names += string.GetHashCode(name.AsSpan());
        }

        long root = 0;
        foreach (var i in tree.ChildrenOf(-1)) root += tree.Size[i];

        return (tree.Count, folders, root, all, chars, names);
    }

    /// <summary>A tree with a known shape, deep enough that folders carry folders.</summary>
    static void Build(string root, int breadth, int depth, int files)
    {
        Directory.CreateDirectory(root);
        for (var f = 0; f < files; f++)
            File.WriteAllText(Path.Combine(root, $"file-{f}.bin"), new string('x', 10 + f));

        if (depth == 0) return;
        for (var b = 0; b < breadth; b++)
            Build(Path.Combine(root, $"folder-{b}"), breadth, depth - 1, files);
    }

    /// <summary>One pass nobody measures, so the metadata cache and the JIT are not in the number.</summary>
    void Warm(string root)
    {
        var clock = Stopwatch.StartNew();
        var walk = new FloorWalk(root);
        walk.Run(Threads);
        output.WriteLine($"warm\t{root}\tms={clock.ElapsedMilliseconds}\tentries={walk.Count}\t" +
                         $"skipped={walk.Skipped}\tnotfollowed={walk.NotFollowed}");
    }

    bool Exists(string root)
    {
        if (Directory.Exists(root)) return true;
        output.WriteLine($"missing\t{root}");
        return false;
    }

    static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    static string Per(long total, int count) =>
        count == 0 ? "0" : ((double)total / count).ToString("F1", CultureInfo.InvariantCulture);

    static string Share(long part, long whole) =>
        whole == 0 ? "0" : ((double)part / whole).ToString("F2", CultureInfo.InvariantCulture);
}
