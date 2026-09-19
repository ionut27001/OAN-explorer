using System.Diagnostics;
using System.IO.Enumeration;
using Oan.Core.FileSystem;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What walking really costs. <see cref="FolderSize"/>'s own comment claims 36 seconds for a user
/// profile; this is the test that has to say it out loud, per folder, with a file count and a note
/// of whether the number is a floor.
///
/// Kept out of the normal pass by Category=Benchmark, which
/// tests/Oan.Tests/Oan.Tests.csproj excludes. Run it on purpose:
///   dotnet test --filter "Category=Benchmark" --logger "console;verbosity=detailed"
/// Point it elsewhere with OAN_SIZE_BENCH_ROOTS (semicolon-separated).
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class FolderSizeBenchmark(ITestOutputHelper output)
{
    static string[] Roots =>
        (Environment.GetEnvironmentVariable("OAN_SIZE_BENCH_ROOTS")
         ?? $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};D:\dev")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Every_immediate_child_folder_one_at_a_time()
    {
        foreach (var root in Roots) Sweep(root);
    }

    /// <summary>
    /// Cold against warm. "Cold" can only mean "not yet touched by this process" — emptying the
    /// operating system's own metadata cache needs elevation, which this machine does not give a
    /// test. So the gap below is the gap a running file manager actually sees on a second visit,
    /// which is the number that matters, and not the gap after a reboot.
    /// </summary>
    [Fact]
    public void The_same_folder_twice()
    {
        // Only cold if nothing else in this process walked it first, so run this one on its own:
        //   dotnet test --filter "FullyQualifiedName~The_same_folder_twice"
        var chosen = Environment.GetEnvironmentVariable("OAN_SIZE_BENCH_TWICE");
        foreach (var root in Roots)
        {
            var folder = chosen is { Length: > 0 } ? chosen : Children(root).FirstOrDefault();
            if (folder is null || !Directory.Exists(folder)) continue;

            // A fresh FolderSize each time: the class caches for a minute, and a benchmark that
            // measures its own cache measures nothing.
            var cold = Time(() => new FolderSize().Measure(folder));
            var warm = Time(() => new FolderSize().Measure(folder));

            output.WriteLine(
                $"twice\t{folder}\tcold_ms={cold.Ms}\twarm_ms={warm.Ms}\t" +
                $"bytes={cold.Result.Bytes}\tsame_bytes={cold.Result.Bytes == warm.Result.Bytes}");

            if (chosen is { Length: > 0 }) break;
        }
    }

    void Sweep(string root)
    {
        if (!Directory.Exists(root))
        {
            output.WriteLine($"missing\t{root}");
            return;
        }

        var rows = new List<(string Name, long ColdMs, long WarmMs, long? Bytes, long Files, int Skipped, int NotFollowed)>();
        var total = Stopwatch.StartNew();

        foreach (var folder in Children(root))
        {
            var cold = Time(() => new FolderSize().Measure(folder));
            // The warm pass is a counting twin of FolderSize's own walker — same options, same
            // refusal to follow reparse points, same error rule, plus one increment per file.
            // Its byte total is printed beside FolderSize's so a divergence cannot hide.
            var warm = Time(() => Count(folder));

            rows.Add((Path.GetFileName(folder), cold.Ms, warm.Ms, cold.Result.Bytes,
                warm.Result.Files, cold.Result.Skipped, cold.Result.NotFollowed));

            output.WriteLine(
                $"child\t{root}\t{Path.GetFileName(folder)}\tcold_ms={cold.Ms}\twarm_ms={warm.Ms}\t" +
                $"bytes={cold.Result.Bytes}\tcount_bytes={warm.Result.Bytes}\tfiles={warm.Result.Files}\t" +
                $"skipped={cold.Result.Skipped}\tnotfollowed={cold.Result.NotFollowed}\t" +
                $"floor={cold.Result.IsFloor}");
        }

        total.Stop();

        var floors = rows.Count(r => r.Skipped > 0 || r.NotFollowed > 0);
        output.WriteLine(
            $"total\t{root}\tfolders={rows.Count}\tcold_sum_ms={rows.Sum(r => r.ColdMs)}\t" +
            $"warm_sum_ms={rows.Sum(r => r.WarmMs)}\twall_ms={total.ElapsedMilliseconds}\t" +
            $"files={rows.Sum(r => r.Files)}\tbytes={rows.Sum(r => r.Bytes ?? 0)}\t" +
            $"floors={floors}\tunknown={rows.Count(r => r.Bytes is null)}");

        foreach (var r in rows.OrderByDescending(r => r.ColdMs).Take(5))
            output.WriteLine($"slowest\t{root}\t{r.Name}\tcold_ms={r.ColdMs}\tfiles={r.Files}\tbytes={r.Bytes}");
    }

    /// <summary>Immediate children, hidden ones included: AppData is where the files are.</summary>
    static IEnumerable<string> Children(string root) =>
        Directory.EnumerateDirectories(root, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    static (T Result, long Ms) Time<T>(Func<T> work)
    {
        var clock = Stopwatch.StartNew();
        var result = work();
        clock.Stop();
        return (result, clock.ElapsedMilliseconds);
    }

    static (long? Bytes, long Files) Count(string path)
    {
        try
        {
            using var walker = new CountingWalker(path);
            long bytes = 0, files = 0;
            while (walker.MoveNext())
            {
                bytes += walker.Current;
                files++;
            }
            return (bytes, files);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return (null, 0);
        }
    }

    sealed class CountingWalker : FileSystemEnumerator<long>
    {
        bool _readAnEntry;

        public CountingWalker(string path) : base(path, new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
        })
        { }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            _readAnEntry = true;
            return !entry.IsDirectory;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            _readAnEntry = true;
            return (entry.Attributes & FileAttributes.ReparsePoint) == 0;
        }

        protected override long TransformEntry(ref FileSystemEntry entry) => entry.Length;

        protected override bool ContinueOnError(int error) => _readAnEntry;
    }
}
