using System.Diagnostics;
using System.IO;
using Oan.App.Platform;
using Oan.App.ViewModels;
using Oan.Core.FileSystem;
using Oan.Core.Sizes;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What one navigation costs that nobody asked for.
///
/// Not a test of behaviour — every number here is an answer to "how much work does opening a
/// folder start, and how much of it is the same work twice". Kept out of the normal pass by
/// Category=Benchmark. Run on purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~NavigationCost" \
///               --logger "console;verbosity=detailed"
///
/// Point it elsewhere with OAN_NAV_ROOT; the watcher probe with OAN_NAV_WATCH.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class NavigationCostBenchmark(ITestOutputHelper output)
{
    private static string Root =>
        Environment.GetEnvironmentVariable("OAN_NAV_ROOT") is { Length: > 0 } set
            ? set
            : @"D:\dev\OANFM";

    private static string Watched =>
        Environment.GetEnvironmentVariable("OAN_NAV_WATCH") is { Length: > 0 } set
            ? set
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The sidebar work every navigation waits on, whether or not anything about it changed.
    /// LoadAsync awaits this before the rows are allowed on screen.
    /// </summary>
    [Fact]
    public void Drive_probe_on_every_navigation()
    {
        // Once to warm whatever the OS caches, then ten the way a user clicking around would.
        Places.Drives();

        var each = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var at = Stopwatch.GetTimestamp();
            var drives = Places.Drives();
            var free = Paths.FreeSpace(Root);
            each.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            Assert.NotEmpty(drives);
            _ = free;
        }

        output.WriteLine($"drives\tcount={Places.Drives().Count}\t" +
                         $"min_ms={each.Min():F2}\tmedian_ms={Median(each):F2}\tmax_ms={each.Max():F2}");
    }

    /// <summary>
    /// FolderSize.Measure spawns sixteen threads per folder, however small the folder. A listing
    /// of N folders therefore pays the spawn N times over. This is that tax on its own, with no
    /// disk work inside it at all.
    /// </summary>
    [Fact]
    public void What_sixteen_threads_cost_before_they_read_anything()
    {
        var threads = Math.Clamp(Environment.ProcessorCount, 2, 16);

        var spawn = Time(() =>
        {
            var workers = new Thread[threads];
            for (var i = 0; i < threads; i++)
            {
                workers[i] = new Thread(() => { }) { IsBackground = true };
                workers[i].Start();
            }
            foreach (var worker in workers) worker.Join();
        }, 20);

        // What the same folders cost measured for real, so the tax can be read as a share.
        var children = Directory.GetDirectories(Root);
        var sizes = new FolderSize(TimeSpan.FromMilliseconds(1), 4096);
        var walked = Time(() => { foreach (var c in children) sizes.Measure(c); }, 1);

        output.WriteLine(
            $"spawn\tthreads={threads}\tper_measurement_ms={spawn:F2}\t" +
            $"folders={children.Length}\tspawn_total_ms={spawn * children.Length:F1}\t" +
            $"walk_total_ms={walked:F1}\t" +
            $"spawn_share={(children.Length == 0 ? 0 : spawn * children.Length / walked):P1}");
    }

    /// <summary>
    /// The size column walks each child folder. The map walks the whole tree. The map's walk
    /// contains every child folder's walk — the same bytes, read twice, on two sets of threads.
    /// </summary>
    [Fact]
    public void The_map_walks_what_the_size_column_just_walked()
    {
        var children = Directory.GetDirectories(Root);

        var column = new FolderSize(TimeSpan.FromMilliseconds(1), 4096);
        long columnBytes = 0;
        var columnMs = Time(() =>
        {
            columnBytes = 0;
            foreach (var c in children) columnBytes += column.Measure(c).Bytes ?? 0;
        }, 1);

        SizeTree? tree = null;
        var treeMs = Time(() => tree = SizeTree.Read(Root), 1);
        Assert.NotNull(tree);

        // What the tree holds for the same child folders: the size column's answer, recomputed.
        long treeBytesForChildren = 0;
        foreach (var i in tree!.ChildrenOf(-1))
        {
            if (tree.IsFolder(i)) treeBytesForChildren += tree.Size[i];
        }

        output.WriteLine(
            $"overlap\troot={Root}\tchild_folders={children.Length}\t" +
            $"column_ms={columnMs:F0}\tcolumn_bytes={columnBytes}\t" +
            $"tree_ms={treeMs:F0}\ttree_entries={tree.Count}\ttree_bytes_for_same_children={treeBytesForChildren}\t" +
            $"same_ground={(columnBytes == treeBytesForChildren)}\t" +
            $"wasted_ms_if_shared={Math.Min(columnMs, treeMs):F0}");
    }

    /// <summary>
    /// Whether the two walks fight. Sequential is the floor; together is what a navigation with
    /// the map on actually does, and the gap is what the extra sixteen threads cost rather than
    /// save.
    /// </summary>
    [Fact]
    public void Do_the_two_walks_fight()
    {
        var children = Directory.GetDirectories(Root);

        double Column()
        {
            var sizes = new FolderSize(TimeSpan.FromMilliseconds(1), 4096);
            return Time(() => { foreach (var c in children) sizes.Measure(c); }, 1);
        }

        double Tree() => Time(() => SizeTree.Read(Root), 1);

        // Warm, so neither run is paying for the other's first touch.
        Column();
        Tree();

        var columnAlone = Column();
        var treeAlone = Tree();

        // Threads of their own, not the pool: the app runs these off its own threads too, and a
        // pool that decides to run them one after the other would measure nothing.
        double insideColumn = 0, insideTree = 0;
        var one = new Thread(() => insideColumn = Column());
        var two = new Thread(() => insideTree = Tree());

        var both = Stopwatch.GetTimestamp();
        one.Start();
        two.Start();
        one.Join();
        two.Join();
        var together = Stopwatch.GetElapsedTime(both).TotalMilliseconds;

        output.WriteLine(
            $"fight\tcolumn_alone_ms={columnAlone:F0}\ttree_alone_ms={treeAlone:F0}\t" +
            $"slower_alone_ms={Math.Max(columnAlone, treeAlone):F0}\t" +
            $"together_ms={together:F0}\t" +
            $"column_inside_ms={insideColumn:F0}\ttree_inside_ms={insideTree:F0}\t" +
            $"cost_of_sharing={together / Math.Max(columnAlone, treeAlone):F2}x");
    }

    /// <summary>
    /// What the recursive watcher costs while you simply sit in a busy folder: how many events
    /// the OS delivers, how many distinct children they collapse to, and how many times the
    /// debounce actually reported — a debounce that is reset by every event can starve.
    /// </summary>
    [Fact]
    public void Sitting_still_in_a_busy_tree()
    {
        if (!Directory.Exists(Watched)) return;

        var seconds = TimeSpan.FromSeconds(10);

        // Raw: exactly the watcher FolderChanges builds, counting what the OS hands over.
        var raw = 0;
        var overflows = 0;
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var watcher = new FileSystemWatcher(Watched)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        })
        {
            void Seen(object s, FileSystemEventArgs e)
            {
                Interlocked.Increment(ref raw);
                lock (distinct) distinct.Add(e.FullPath);
            }

            watcher.Created += Seen;
            watcher.Deleted += Seen;
            watcher.Changed += Seen;
            watcher.Renamed += Seen;
            watcher.Error += (_, _) => Interlocked.Increment(ref overflows);

            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            watcher.EnableRaisingEvents = true;
            Thread.Sleep(seconds);
            watcher.EnableRaisingEvents = false;
            var spent = Process.GetCurrentProcess().TotalProcessorTime - cpu;

            output.WriteLine(
                $"watch_raw\troot={Watched}\tseconds={seconds.TotalSeconds}\tevents={raw}\t" +
                $"events_per_s={raw / seconds.TotalSeconds:F1}\tdistinct_paths={distinct.Count}\t" +
                $"overflows={overflows}\tcpu_ms={spent.TotalMilliseconds:F0}");
        }

        // What FolderChanges would hand the view model: how often it settled, and how many
        // children each report named — each of which is queued for a fresh recursive walk.
        var reports = 0;
        var named = 0;
        var everything = 0;
        using (var changes = new FolderChanges(children =>
        {
            Interlocked.Increment(ref reports);
            if (children is null) Interlocked.Increment(ref everything);
            else Interlocked.Add(ref named, children.Count);
        }))
        {
            changes.Watch(Watched);
            Thread.Sleep(seconds);
        }

        output.WriteLine(
            $"watch_settled\troot={Watched}\tseconds={seconds.TotalSeconds}\treports={reports}\t" +
            $"children_named={named}\tassume_all={everything}\t" +
            $"rewalks_asked_for={named + everything}");
    }

    /// <summary>
    /// What it costs to put the recursive watch up and take it down again — which one navigation
    /// does twice, and every keystroke in the filter box does twice more.
    /// </summary>
    [Fact]
    public void Putting_the_recursive_watch_up_and_taking_it_down()
    {
        if (!Directory.Exists(Watched)) return;

        using var changes = new FolderChanges(_ => { });
        changes.Watch(Watched);
        changes.Watch(null);

        var each = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var at = Stopwatch.GetTimestamp();
            changes.Watch(Watched);
            each.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            changes.Watch(null);
        }

        output.WriteLine(
            $"watch_setup\troot={Watched}\tmin_ms={each.Min():F2}\tmedian_ms={Median(each):F2}\t" +
            $"max_ms={each.Max():F2}\tbuffer_kb=64\tpaid_per_navigation=2");
    }

    /// <summary>
    /// The debounce is reset by every event and has no ceiling. Churn arriving faster than the
    /// settle window therefore never settles: the sizes on screen stay wrong for as long as the
    /// writing goes on, however long that is.
    /// </summary>
    [Fact]
    public void A_debounce_with_no_ceiling_never_settles()
    {
        using var temp = new TempFolder();
        var child = temp.AddFolder("busy");
        var reports = 0;
        using var changes = new FolderChanges(_ => Interlocked.Increment(ref reports), TimeSpan.FromMilliseconds(750));
        changes.Watch(temp.Root);

        // Five seconds of writing, one file every 200 ms: well under the 750 ms window.
        var churn = Stopwatch.StartNew();
        var wrote = 0;
        while (churn.Elapsed < TimeSpan.FromSeconds(5))
        {
            File.WriteAllText(Path.Combine(child, $"f{wrote++}.txt"), "x");
            Thread.Sleep(200);
        }

        var duringChurn = Volatile.Read(ref reports);
        Thread.Sleep(1500);
        var afterQuiet = Volatile.Read(ref reports);

        output.WriteLine(
            $"debounce\twrites={wrote}\tseconds_of_churn=5\tsettle_ms=750\t" +
            $"reports_during_churn={duringChurn}\treports_after_it_stopped={afterQuiet}");
    }

    /// <summary>
    /// A second Show for the same folder — which is what one keystroke in the filter box, or one
    /// click on a column header, produces — while the first round of walks is still running.
    /// Nothing dedupes the queue and nothing remembers that a path is already being walked, so
    /// the whole listing is walked again.
    /// </summary>
    [Fact]
    public void Showing_the_same_folder_twice_walks_it_twice()
    {
        var settings = new Settings { FolderSizes = true, Treemap = false };

        output.WriteLine($"reshow\tonce_ms={Once(settings, reshow: false):F0}\t" +
                         $"twice_ms={Once(settings, reshow: true):F0}\troot={Root}");
    }

    private double Once(Settings settings, bool reshow)
    {
        using var sizes = new FolderSizesViewModel(settings, () => { });

        // Quiescence, not "every row has a number": ten of a real listing's folders never get one
        // — they cannot be opened at all — so waiting for them would only ever time out.
        long lastLanded = 0;
        sizes.Settled += () => Interlocked.Exchange(ref lastLanded, Stopwatch.GetTimestamp());

        var rows = Rows();
        var at = Stopwatch.GetTimestamp();
        sizes.Show(Root, rows);

        if (reshow)
        {
            // A keystroke rebuilds the rows from scratch, so the second Show gets new objects
            // that know nothing of the walks already running for the same paths.
            Thread.Sleep(30);
            sizes.Show(Root, Rows());
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMinutes(2))
        {
            var last = Interlocked.Read(ref lastLanded);
            if (last != 0 && Stopwatch.GetElapsedTime(last) > TimeSpan.FromSeconds(2)) break;
            Thread.Sleep(20);
        }

        var quiet = Interlocked.Read(ref lastLanded);
        return quiet == 0 ? -1 : Stopwatch.GetElapsedTime(at, quiet).TotalMilliseconds;
    }

    /// <summary>
    /// What starting the app walks twice. OAN opens in the user profile, so the listing measures
    /// every child of the profile — and the sidebar, on its own second queue, measures six of
    /// those same children at the same moment.
    /// </summary>
    [Fact]
    public void The_sidebar_measures_what_the_listing_is_already_measuring()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var quick = Oan.Core.FileSystem.Places.Quick()
            .Where(p => p.Available && !string.Equals(p.Path, home, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Path)
            .ToArray();

        var listing = Directory.GetDirectories(home).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var both = quick.Where(listing.Contains).ToArray();

        var sizes = new FolderSize(TimeSpan.FromMilliseconds(1), 4096);
        var ms = Time(() => { foreach (var p in both) sizes.Measure(p); }, 1);

        output.WriteLine(
            $"startup_overlap	sidebar_measures={quick.Length}	also_rows_of_the_listing={both.Length}	" +
            $"paths={string.Join("|", both.Select(Path.GetFileName))}	duplicated_ms={ms:F0}");
    }

    /// <summary>
    /// Refreshing a size throws the old one away before the new one exists. A rebuild that lands
    /// in that window — one keystroke in the filter box — finds nothing cached and starts the
    /// whole listing again, with every row blinking back to an ellipsis on the way.
    /// </summary>
    [Fact]
    public void Refreshing_a_size_throws_the_old_one_away_first()
    {
        var folder = Directory.GetDirectories(Root).First();
        using var queue = new SizeQueue(_ => { }, capacity: 512);

        queue.Add(folder);
        var deadline = Stopwatch.StartNew();
        while (queue.Cached(folder) is null && deadline.Elapsed < TimeSpan.FromMinutes(1)) Thread.Sleep(10);

        var before = queue.Cached(folder);

        // Exactly what FolderSizesViewModel.Quietly does.
        queue.Forget(folder);
        queue.Add(folder);
        var during = queue.Cached(folder);

        output.WriteLine(
            $"blink	folder={folder}	had_a_number={before is not null}	" +
            $"still_has_one_while_rewalking={during is not null}");

        Assert.NotNull(before);
        Assert.Null(during);
    }

    private static EntryViewModel[] Rows() =>
        [.. Directory.GetDirectories(Root).Select(d => new EntryViewModel(
            new Oan.Core.Model.FileEntry
            {
                Name = Path.GetFileName(d),
                FullPath = d,
                Attributes = FileAttributes.Directory,
                Size = 0,
                Modified = DateTime.UtcNow,
            },
            16))];

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static double Time(Action work, int times)
    {
        var at = Stopwatch.GetTimestamp();
        for (var i = 0; i < times; i++) work();
        return Stopwatch.GetElapsedTime(at).TotalMilliseconds / times;
    }
}
