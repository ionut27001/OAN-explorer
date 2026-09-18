using System.Diagnostics;
using Oan.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>Kept out of the normal pass: these read whole trees and take minutes.</summary>
sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OAN_BENCH") != "1") Skip = "Benchmark. Set OAN_BENCH=1 to run.";
    }
}

/// <summary>
/// One tree per run, chosen by environment so the same binary can be pointed at any path:
/// OAN_BENCH_ROOT, OAN_BENCH_NAME, OAN_BENCH_MODE (substring|glob|regex), OAN_BENCH_TERM,
/// OAN_BENCH_REGEX, OAN_BENCH_HIDDEN. Results and the tools compared against are in
/// docs/search-benchmark.md, which also lists the exact invocations.
/// </summary>
public sealed class SearchBenchmark(ITestOutputHelper output)
{
    static string Root => Environment.GetEnvironmentVariable("OAN_BENCH_ROOT") ?? @"C:\Windows\System32";
    static string Name => Environment.GetEnvironmentVariable("OAN_BENCH_NAME") ?? "net";
    static string Term => Environment.GetEnvironmentVariable("OAN_BENCH_TERM") ?? "Microsoft";
    static bool Hidden => Environment.GetEnvironmentVariable("OAN_BENCH_HIDDEN") == "1";
    static bool Regex => Environment.GetEnvironmentVariable("OAN_BENCH_REGEX") == "1";

    static NameMatch Mode() => Environment.GetEnvironmentVariable("OAN_BENCH_MODE") switch
    {
        "glob" => NameMatch.Glob,
        "regex" => NameMatch.Regex,
        _ => NameMatch.Substring,
    };

    /// <summary>What the platform costs to list the tree at all, matching nothing.</summary>
    [BenchmarkFact]
    public void Raw_enumeration()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = Hidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        var clock = Stopwatch.StartNew();
        var seen = 0;
        foreach (var _ in Directory.EnumerateFileSystemEntries(Root, "*", options)) seen++;
        clock.Stop();

        output.WriteLine($"raw\troot={Root}\tentries={seen}\tms={clock.ElapsedMilliseconds}");
    }

    [BenchmarkFact]
    public Task Name_search() => Measure("name", new FileSearch(), new SearchQuery
    {
        Root = Root,
        Name = Name,
        NameMode = Mode(),
        IncludeHidden = Hidden,
    });

    [BenchmarkFact]
    public Task Content_search()
    {
        var query = new SearchQuery
        {
            Root = Root,
            Content = Term,
            ContentIsRegex = Regex,
            IncludeHidden = Hidden,
        };
        return Measure("content", new FileSearch(new ContentMatcher(query)), query);
    }

    async Task Measure(string label, FileSearch search, SearchQuery query)
    {
        var clock = Stopwatch.StartNew();
        long first = -1;
        var hits = 0;

        await foreach (var _ in search.RunAsync(query))
            if (hits++ == 0) first = clock.ElapsedMilliseconds;

        clock.Stop();
        var tally = search.Tally;
        output.WriteLine(
            $"{label}\troot={query.Root}\thits={hits}\tms={clock.ElapsedMilliseconds}\tfirstms={first}\t" +
            $"files={tally.FilesSeen}\tfolders={tally.FoldersSeen}\tdenied={tally.Denied}\t" +
            $"binary={tally.Binary}\ttoolarge={tally.TooLarge}\tunreadable={tally.Unreadable}\t" +
            $"complete={tally.Complete}");
    }
}
