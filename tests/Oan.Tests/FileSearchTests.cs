using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Oan.Core.Search;
using Xunit;

namespace Oan.Tests;

public class FileSearchTests
{
    static SearchQuery Named(string root, string name, NameMatch mode = NameMatch.Substring) =>
        new() { Root = root, Name = name, NameMode = mode };

    static async Task<List<SearchHit>> Collect(FileSearch search, SearchQuery query, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in search.RunAsync(query, ct))
            hits.Add(hit);
        return hits;
    }

    static string[] Names(IEnumerable<SearchHit> hits) =>
        hits.Select(h => h.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Substring_ignores_case_by_default()
    {
        using var temp = new TempFolder();
        temp.AddFile("Report.txt");
        temp.AddFile("report-old.TXT");
        temp.AddFile("notes.md");

        var hits = await Collect(new FileSearch(), Named(temp.Root, "report"));

        Assert.Equal(["Report.txt", "report-old.TXT"], Names(hits));
    }

    [Fact]
    public async Task Substring_honours_case_when_asked()
    {
        using var temp = new TempFolder();
        temp.AddFile("Report.txt");
        temp.AddFile("report-old.TXT");

        var query = Named(temp.Root, "report") with { CaseSensitive = true };

        Assert.Equal(["report-old.TXT"], Names(await Collect(new FileSearch(), query)));
    }

    [Fact]
    public async Task Glob_matches_star_and_question()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        temp.AddFile("ab.txt");
        temp.AddFile("a.md");

        var one = await Collect(new FileSearch(), Named(temp.Root, "?.txt", NameMatch.Glob));
        var all = await Collect(new FileSearch(), Named(temp.Root, "*.txt", NameMatch.Glob));

        Assert.Equal(["a.txt"], Names(one));
        Assert.Equal(["a.txt", "ab.txt"], Names(all));
    }

    [Fact]
    public async Task Glob_is_anchored_so_a_suffix_stays_a_suffix()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        temp.AddFile("a.txtx");

        var hits = await Collect(new FileSearch(), Named(temp.Root, "*.txt", NameMatch.Glob));

        Assert.Equal(["a.txt"], Names(hits));
    }

    [Fact]
    public async Task Glob_escapes_regex_metacharacters()
    {
        using var temp = new TempFolder();
        temp.AddFile("report(1).txt");
        temp.AddFile("report1.txt");
        temp.AddFile("a+b.log");
        temp.AddFile("aab.log");

        var parens = await Collect(new FileSearch(), Named(temp.Root, "report(1)*", NameMatch.Glob));
        var plus = await Collect(new FileSearch(), Named(temp.Root, "a+b*", NameMatch.Glob));

        Assert.Equal(["report(1).txt"], Names(parens));
        Assert.Equal(["a+b.log"], Names(plus));
    }

    [Fact]
    public async Task Regex_uses_the_pattern_as_given()
    {
        using var temp = new TempFolder();
        temp.AddFile("log-2024.txt");
        temp.AddFile("log-abc.txt");

        var hits = await Collect(new FileSearch(), Named(temp.Root, @"^log-\d+\.txt$", NameMatch.Regex));

        Assert.Equal(["log-2024.txt"], Names(hits));
    }

    [Fact]
    public void An_unparsable_regex_is_refused_before_the_walk_starts()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt");
        var search = new FileSearch();
        var query = Named(temp.Root, "(unclosed", NameMatch.Regex);

        var error = Assert.Throws<ArgumentException>(() => search.RunAsync(query));

        Assert.Contains("Name pattern", error.Message);
        Assert.Equal(0, search.Tally.FilesSeen);
    }

    [Fact]
    public async Task Recurses_into_subfolders()
    {
        using var temp = new TempFolder();
        var deep = Directory.CreateDirectory(Path.Combine(temp.Root, "a", "b", "c")).FullName;
        File.WriteAllText(Path.Combine(deep, "buried.txt"), "x");
        temp.AddFile("shallow.txt");

        var search = new FileSearch();
        var hits = await Collect(search, Named(temp.Root, ".txt"));

        Assert.Equal(["buried.txt", "shallow.txt"], Names(hits));
        Assert.Equal(3, search.Tally.FoldersSeen);
        Assert.Equal(2, search.Tally.FilesSeen);
        Assert.True(search.Tally.Complete);
    }

    [Fact]
    public async Task Hidden_is_left_out_by_default_and_included_when_asked()
    {
        using var temp = new TempFolder();
        temp.AddFile("plain.txt");
        temp.AddHiddenFile("secret.txt");
        var stash = temp.AddFolder("stash");
        File.WriteAllText(Path.Combine(stash, "inside.txt"), "x");
        File.SetAttributes(stash, FileAttributes.Directory | FileAttributes.Hidden);

        var visible = await Collect(new FileSearch(), Named(temp.Root, ".txt"));
        var everything = await Collect(new FileSearch(), Named(temp.Root, ".txt") with { IncludeHidden = true });

        Assert.Equal(["plain.txt"], Names(visible));
        Assert.Equal(["inside.txt", "plain.txt", "secret.txt"], Names(everything));
    }

    [Fact]
    public async Task Size_filters_come_from_the_directory_record()
    {
        using var temp = new TempFolder();
        temp.AddFile("small.txt", new string('x', 10));
        temp.AddFile("big.txt", new string('x', 5000));

        var big = await Collect(new FileSearch(), Named(temp.Root, ".txt") with { MinSize = 1000 });
        var small = await Collect(new FileSearch(), Named(temp.Root, ".txt") with { MaxSize = 1000 });

        Assert.Equal(["big.txt"], Names(big));
        Assert.Equal(["small.txt"], Names(small));
        Assert.Equal(5000, Assert.Single(big).Size);
    }

    [Fact]
    public async Task Date_filters_come_from_the_directory_record()
    {
        using var temp = new TempFolder();
        var old = temp.AddFile("old.txt");
        temp.AddFile("fresh.txt");
        File.SetLastWriteTime(old, new DateTime(2001, 2, 3, 4, 5, 6));

        var cutoff = new DateTime(2010, 1, 1);
        var before = await Collect(new FileSearch(), Named(temp.Root, ".txt") with { ModifiedBefore = cutoff });
        var after = await Collect(new FileSearch(), Named(temp.Root, ".txt") with { ModifiedAfter = cutoff });

        Assert.Equal(["old.txt"], Names(before));
        Assert.Equal(["fresh.txt"], Names(after));
    }

    [Fact]
    public async Task A_denied_folder_is_counted_rather_than_thrown()
    {
        using var temp = new TempFolder();
        temp.AddFile("visible.txt");
        var locked = temp.AddFolder("locked");
        File.WriteAllText(Path.Combine(locked, "out-of-reach.txt"), "x");

        var search = new FileSearch();
        List<SearchHit> hits;
        Deny(locked);
        try
        {
            hits = await Collect(search, Named(temp.Root, ".txt"));
        }
        finally
        {
            Allow(locked);
        }

        Assert.Equal(["visible.txt"], Names(hits));
        Assert.Equal(1, search.Tally.Denied);
        Assert.False(search.Tally.Complete);
    }

    [Fact]
    public async Task A_junction_is_not_followed()
    {
        using var temp = new TempFolder();
        var real = temp.AddFolder("real");
        File.WriteAllText(Path.Combine(real, "once.txt"), "x");
        var loop = Path.Combine(temp.Root, "loop");
        Assert.True(Junction(loop, temp.Root), "mklink /J could not create a junction here.");

        try
        {
            var hits = await Collect(new FileSearch(), Named(temp.Root, "once"))
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(["once.txt"], Names(hits));
        }
        finally
        {
            Directory.Delete(loop);
        }
    }

    [Fact]
    public async Task Content_is_only_read_for_files_the_cheap_filters_kept()
    {
        using var temp = new TempFolder();
        temp.AddFile("wanted.txt", "needle");
        temp.AddFile("wanted.md", "needle");
        temp.AddFile("huge.txt", new string('x', 5000));

        var matcher = new FakeMatcher(_ => new ContentResult(true, 12, "a needle here", SkipReason.None));
        var search = new FileSearch(matcher);
        var query = new SearchQuery
        {
            Root = temp.Root,
            Name = "*.txt",
            NameMode = NameMatch.Glob,
            Content = "needle",
            MaxSize = 1000,
        };

        var hit = Assert.Single(await Collect(search, query));

        Assert.Equal("wanted.txt", Path.GetFileName(Assert.Single(matcher.Seen)));
        Assert.Equal("wanted.txt", hit.Name);
        Assert.Equal(12, hit.Line);
        Assert.Equal("a needle here", hit.Preview);
    }

    [Fact]
    public async Task A_name_only_hit_carries_no_line_or_preview()
    {
        using var temp = new TempFolder();
        temp.AddFile("a.txt", "some text");

        var hit = Assert.Single(await Collect(new FileSearch(), Named(temp.Root, "a.txt")));

        Assert.Null(hit.Line);
        Assert.Null(hit.Preview);
        Assert.Equal(9, hit.Size);
        Assert.NotNull(hit.Modified);
    }

    [Fact]
    public async Task Skipped_files_are_tallied_by_reason()
    {
        using var temp = new TempFolder();
        temp.AddFile("big.txt");
        temp.AddFile("image.txt");
        temp.AddFile("locked.txt");
        temp.AddFile("good.txt");

        var matcher = new FakeMatcher(path => Path.GetFileName(path) switch
        {
            "big.txt" => new ContentResult(false, null, null, SkipReason.TooLarge),
            "image.txt" => new ContentResult(false, null, null, SkipReason.Binary),
            "locked.txt" => new ContentResult(false, null, null, SkipReason.Unreadable),
            _ => new ContentResult(true, 1, "hit", SkipReason.None),
        });
        var search = new FileSearch(matcher);

        var hits = await Collect(search, new SearchQuery { Root = temp.Root, Content = "x" });

        Assert.Equal(["good.txt"], Names(hits));
        Assert.Equal(1, search.Tally.TooLarge);
        Assert.Equal(1, search.Tally.Binary);
        Assert.Equal(1, search.Tally.Unreadable);
        Assert.False(search.Tally.Complete);
    }

    [Fact]
    public async Task A_file_that_vanishes_under_the_matcher_does_not_kill_the_run()
    {
        using var temp = new TempFolder();
        temp.AddFile("gone.txt");
        temp.AddFile("here.txt");

        var matcher = new FakeMatcher(path => Path.GetFileName(path) == "gone.txt"
            ? throw new FileNotFoundException("vanished", path)
            : new ContentResult(true, 1, "hit", SkipReason.None));
        var search = new FileSearch(matcher);

        var hits = await Collect(search, new SearchQuery { Root = temp.Root, Content = "x" });

        Assert.Equal(["here.txt"], Names(hits));
        Assert.Equal(1, search.Tally.Unreadable);
    }

    [Fact]
    public async Task Hits_arrive_while_the_run_is_still_going()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 120; i++)
            temp.AddFile($"file{i:D3}.txt", "needle");

        var matcher = new FakeMatcher(_ =>
        {
            Thread.Sleep(10);
            return new ContentResult(true, 1, "needle", SkipReason.None);
        });
        var search = new FileSearch(matcher);

        await using var hits = search
            .RunAsync(new SearchQuery { Root = temp.Root, Content = "needle" })
            .GetAsyncEnumerator();

        Assert.True(await hits.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(matcher.Count < 120, $"the first hit waited for {matcher.Count} of 120 files");
    }

    [Fact]
    public async Task A_token_cancelled_before_the_walk_is_felt_on_an_empty_tree()
    {
        using var temp = new TempFolder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Collect(new FileSearch(), Named(temp.Root, "anything"), cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelling_mid_run_stops_within_a_few_hundred_milliseconds()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 2000; i++)
            temp.AddFile($"file{i:D4}.txt", "needle");

        var matcher = new FakeMatcher(_ =>
        {
            Thread.Sleep(5);
            return new ContentResult(true, 1, "needle", SkipReason.None);
        });
        var search = new FileSearch(matcher);
        using var cts = new CancellationTokenSource();

        var run = Collect(search, new SearchQuery { Root = temp.Root, Content = "needle" }, cts.Token);
        await Task.Delay(100);
        var clock = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 500, $"cancellation took {clock.ElapsedMilliseconds} ms");

        var seenWhenStopped = search.Tally.FilesSeen;
        await Task.Delay(300);
        Assert.Equal(seenWhenStopped, search.Tally.FilesSeen);
        Assert.True(seenWhenStopped < 2000, $"the walk read {seenWhenStopped} of 2000 entries before it stopped");
    }

    /// <summary>
    /// A name-only walk hands nothing to the content queue while it looks, so the check inside
    /// the enumerator's own callbacks is the only thing that can stop it early. C:\Windows is
    /// used because the point is a tree too large to finish, and building one would take longer
    /// than reading one.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_name_search_stops_the_walk_itself()
    {
        var search = new FileSearch();
        using var cts = new CancellationTokenSource();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var run = Collect(search, Named(windows, "oan-no-such-file-9f3c"), cts.Token);
        await Task.Delay(250);
        Assert.True(search.Tally.FilesSeen > 0, "the walk never got going");

        var clock = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        clock.Stop();

        // The run only reports cancelled once the walk behind it has actually stopped, so this
        // is the walk's own reaction time, not the reader's.
        Assert.True(clock.ElapsedMilliseconds < 2000, $"the walk took {clock.ElapsedMilliseconds} ms to stop");
    }

    [Fact]
    public async Task A_root_that_is_not_there_is_counted_rather_than_thrown()
    {
        using var temp = new TempFolder();
        var search = new FileSearch();

        var hits = await Collect(search, Named(Path.Combine(temp.Root, "gone"), "anything"));

        Assert.Empty(hits);
        Assert.Equal(1, search.Tally.Unreadable);
        Assert.False(search.Tally.Complete);
    }

    [Fact]
    public void A_content_query_without_a_matcher_is_a_programming_error()
    {
        using var temp = new TempFolder();

        Assert.Throws<InvalidOperationException>(
            () => new FileSearch().RunAsync(new SearchQuery { Root = temp.Root, Content = "x" }));
    }

    sealed class FakeMatcher : IContentMatcher
    {
        readonly Func<string, ContentResult> reply;
        readonly List<string> seen = [];

        public FakeMatcher(Func<string, ContentResult> reply) => this.reply = reply;

        public int Count { get { lock (seen) return seen.Count; } }

        public string[] Seen { get { lock (seen) return [.. seen]; } }

        public ContentResult Match(string path, long size, CancellationToken ct)
        {
            lock (seen) seen.Add(path);
            return reply(path);
        }
    }

    static void Deny(string folder)
    {
        var info = new DirectoryInfo(folder);
        var rules = info.GetAccessControl();
        rules.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(rules);
    }

    static void Allow(string folder)
    {
        var info = new DirectoryInfo(folder);
        var rules = info.GetAccessControl();
        rules.RemoveAccessRuleAll(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(rules);
    }

    /// <summary>mklink /J needs no privilege; Directory.CreateSymbolicLink does.</summary>
    static bool Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (mklink is null) return false;
        mklink.WaitForExit(10_000);
        return mklink.HasExited && mklink.ExitCode == 0;
    }
}
