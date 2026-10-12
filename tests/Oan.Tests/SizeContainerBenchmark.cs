using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// The containers on their own, with no disk under them. A walk over a real profile moves by tens
/// of per cent between runs, which is enough to hide a ten megabyte difference; the same number of
/// entries pushed through the same containers with nothing else happening does not move at all, so
/// this is where "the bag costs X" and "a string per entry costs Y" are actually settled.
///
///   dotnet test -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
/// Size it with OAN_TREE_BENCH_ENTRIES (default 1.1 million, the profile in the comment).
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class SizeContainerBenchmark(ITestOutputHelper output)
{
    static int Entries =>
        int.TryParse(Environment.GetEnvironmentVariable("OAN_TREE_BENCH_ENTRIES"), out var n) ? n : 1_100_000;

    const int NameLength = 20;
    static int Threads => Math.Clamp(Environment.ProcessorCount, 2, 16);

    [Fact]
    public void What_a_string_per_entry_costs()
    {
        var count = Entries;
        var source = new string('n', NameLength * 64).ToCharArray();

        var strings = Weigh(() =>
        {
            var held = new string[count];
            for (var i = 0; i < count; i++) held[i] = new string(source, i % 64 * NameLength, NameLength);
            return held;
        });

        var packed = Weigh(() =>
        {
            var text = new char[(long)count * NameLength];
            var at = new int[count];
            var len = new int[count];
            var written = 0;
            for (var i = 0; i < count; i++)
            {
                source.AsSpan(i % 64 * NameLength, NameLength).CopyTo(text.AsSpan(written));
                at[i] = written;
                len[i] = NameLength;
                written += NameLength;
            }
            return (object)(text, at, len);
        });

        output.WriteLine($"names\tentries={count}\tname_chars={NameLength}");
        output.WriteLine($"strings\t{Line(strings, count)}");
        output.WriteLine($"packed\t{Line(packed, count)}");
        output.WriteLine($"saving\tretained_mb={Mb(strings.Retained - packed.Retained)}\t" +
                         $"alloc_mb={Mb(strings.Alloc - packed.Alloc)}\tms={strings.Ms - packed.Ms}");
    }

    [Fact]
    public void What_the_bag_costs_against_per_thread_buffers()
    {
        var count = Entries;
        // One string for every entry, so what is measured is the container and not the text.
        var name = new string('n', NameLength);

        var bag = Weigh(() =>
        {
            var found = new ConcurrentBag<Held>();
            Spread(count, (from, to) =>
            {
                for (var i = from; i < to; i++) found.Add(new Held(name, i - 1, i, false, i));
            });
            return found;
        });

        var drained = Weigh(() =>
        {
            var found = new ConcurrentBag<Held>();
            Spread(count, (from, to) =>
            {
                for (var i = from; i < to; i++) found.Add(new Held(name, i - 1, i, false, i));
            });
            return (object)(found, found.ToArray());
        });

        var lists = Weigh(() =>
        {
            var per = new List<Held>[Threads];
            Spread(count, (from, to, slot) =>
            {
                var mine = per[slot] = new List<Held>(1 << 12);
                for (var i = from; i < to; i++) mine.Add(new Held(name, i - 1, i, false, i));
            });
            return per;
        });

        var packed = Weigh(() =>
        {
            var per = new List<Packed>[Threads];
            var text = new Names[Threads];
            Spread(count, (from, to, slot) =>
            {
                var mine = per[slot] = new List<Packed>(1 << 12);
                var names = text[slot] = new Names();
                for (var i = from; i < to; i++)
                    mine.Add(new Packed(i - 1, i, false, i, names.Add(name), NameLength));
            });
            return (object)(per, text);
        });

        output.WriteLine($"container\tentries={count}\tthreads={Threads}");
        output.WriteLine($"bag\t{Line(bag, count)}");
        output.WriteLine($"bag+toarray\t{Line(drained, count)}");
        output.WriteLine($"lists\t{Line(lists, count)}");
        output.WriteLine($"lists+chars\t{Line(packed, count)}");
        output.WriteLine($"saving\tlists_vs_bag_retained_mb={Mb(drained.Retained - lists.Retained)}\t" +
                         $"lists_vs_bag_alloc_mb={Mb(drained.Alloc - lists.Alloc)}\t" +
                         $"lists_vs_bag_ms={drained.Ms - lists.Ms}");
    }

    /// <summary>
    /// The third concurrent structure in the walk, and the one nobody mentions: a dictionary
    /// entry per folder, holding the bytes of the files directly inside it. A folder is one entry
    /// in twenty on this machine, so it is measured at that ratio.
    /// </summary>
    [Fact]
    public void What_the_totals_dictionary_costs()
    {
        var folders = Math.Max(1, Entries / 20);

        var dictionary = Weigh(() =>
        {
            var totals = new ConcurrentDictionary<int, long>();
            Spread(folders, (from, to) =>
            {
                for (var i = from; i < to; i++) totals[i] = i;
            });
            return totals;
        });

        var pairs = Weigh(() =>
        {
            var per = new List<(int At, long Bytes)>[Threads];
            Spread(folders, (from, to, slot) =>
            {
                var mine = per[slot] = new List<(int, long)>(1 << 10);
                for (var i = from; i < to; i++) mine.Add((i, i));
            });
            return per;
        });

        output.WriteLine($"totals\tfolders={folders}");
        output.WriteLine($"dictionary\t{Line(dictionary, folders)}");
        output.WriteLine($"pairs\t{Line(pairs, folders)}");
        output.WriteLine($"saving\tretained_mb={Mb(dictionary.Retained - pairs.Retained)}\t" +
                         $"alloc_mb={Mb(dictionary.Alloc - pairs.Alloc)}\tms={dictionary.Ms - pairs.Ms}");
    }

    readonly record struct Cost(long Ms, long Alloc, long Retained);

    /// <summary>
    /// Built while nothing else is alive, held while the heap is measured, and unreachable the
    /// moment this returns — which is why the thing being weighed is created inside and not passed
    /// in. Retained is what a full compacting collection could not take away.
    /// </summary>
    static Cost Weigh(Func<object> build)
    {
        Settle();
        var baseline = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);

        var clock = Stopwatch.StartNew();
        var kept = build();
        var ms = clock.ElapsedMilliseconds;
        var churn = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        Settle();
        var retained = GC.GetTotalMemory(true) - baseline;
        GC.KeepAlive(kept);
        return new Cost(ms, churn, retained);
    }

    static void Spread(int count, Action<int, int> work) =>
        Spread(count, (from, to, _) => work(from, to));

    /// <summary>The same sixteen threads the walk uses, each with its own stretch of the entries.</summary>
    static void Spread(int count, Action<int, int, int> work)
    {
        var threads = new Thread[Threads];
        var share = count / Threads + 1;

        for (var t = 0; t < threads.Length; t++)
        {
            var slot = t;
            var from = slot * share;
            var to = Math.Min(count, from + share);
            threads[t] = new Thread(() => work(from, Math.Max(from, to), slot)) { IsBackground = true };
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
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

    static string Line(Cost cost, int count) =>
        $"ms={cost.Ms}\tretained_mb={Mb(cost.Retained)}\talloc_mb={Mb(cost.Alloc)}\t" +
        $"retained_per_entry={Per(cost.Retained, count)}\talloc_per_entry={Per(cost.Alloc, count)}";

    static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    static string Per(long total, int count) =>
        count == 0 ? "0" : ((double)total / count).ToString("F1", CultureInfo.InvariantCulture);
}
