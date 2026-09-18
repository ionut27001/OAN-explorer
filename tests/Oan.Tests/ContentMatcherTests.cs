using System.Diagnostics;
using System.Text;
using Oan.Core.Search;
using Xunit;

namespace Oan.Tests;

public class ContentMatcherTests
{
    [Fact]
    public void Finds_a_literal_term_and_says_which_line_it_is_on()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("notes.txt", "alpha\nbeta TODO gamma\ndelta\n");

        var hit = Match(file, Query("TODO"));

        Assert.True(hit.Matched);
        Assert.Equal(2, hit.Line);
        Assert.Equal("beta TODO gamma", hit.Preview);
        Assert.Equal(SkipReason.None, hit.Skip);
    }

    [Fact]
    public void A_miss_carries_no_line_and_no_preview()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("notes.txt", "alpha\nbeta\n");

        var miss = Match(file, Query("TODO"));

        Assert.False(miss.Matched);
        Assert.Null(miss.Line);
        Assert.Null(miss.Preview);
        Assert.Equal(SkipReason.None, miss.Skip);
    }

    [Fact]
    public void An_empty_file_is_a_miss_rather_than_a_skip()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("empty.txt");

        var miss = Match(file, Query("TODO"));

        Assert.False(miss.Matched);
        Assert.Equal(SkipReason.None, miss.Skip);
    }

    [Fact]
    public void Case_is_ignored_unless_the_query_asks_for_it()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("readme.txt", "The Readme file\n");

        Assert.True(Match(file, Query("readme")).Matched);
        Assert.True(Match(file, Query("README")).Matched);
        Assert.True(Match(file, Query("Readme", caseSensitive: true)).Matched);
        Assert.False(Match(file, Query("readme", caseSensitive: true)).Matched);
    }

    [Fact]
    public void A_term_split_across_the_read_seam_is_still_found()
    {
        const int chunk = 64 * 1024;  // ContentMatcher.ChunkSize
        using var temp = new TempFolder();
        const string head = "one\ntwo\n";
        var text = head + new string('.', chunk - head.Length - 3) + "NEEDLE rest\n";
        var file = temp.AddFile("big.txt", text);

        var hit = Match(file, Query("NEEDLE"));

        Assert.True(hit.Matched, "the term straddles the buffer boundary and was missed");
        Assert.Equal(3, hit.Line);
        Assert.Contains("NEEDLE rest", hit.Preview);
    }

    [Fact]
    public void Line_numbers_are_the_same_for_crlf_and_lf_endings()
    {
        using var temp = new TempFolder();
        var unix = temp.AddFile("unix.txt", "a\nb\nTODO here\nd\n");
        var windows = temp.AddFile("windows.txt", "a\r\nb\r\nTODO here\r\nd\r\n");

        Assert.Equal(3, Match(unix, Query("TODO")).Line);
        Assert.Equal(3, Match(windows, Query("TODO")).Line);
        Assert.Equal("TODO here", Match(windows, Query("TODO")).Preview);
    }

    [Fact]
    public void A_very_long_line_is_previewed_around_the_match_not_dumped_whole()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("min.js", new string('x', 5000) + "NEEDLE" + new string('y', 5000));

        var preview = Match(file, Query("NEEDLE")).Preview;

        Assert.NotNull(preview);
        Assert.Contains("NEEDLE", preview);
        Assert.True(preview.Length < 260, $"preview was {preview.Length} characters long");
        Assert.StartsWith("…", preview);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Utf8_text_with_diacritics_matches_whichever_case_is_typed()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("raport.txt", "primul rand\nraport ședință final\n");

        Assert.True(Match(file, Query("ședință", caseSensitive: true)).Matched);
        var folded = Match(file, Query("ȘEDINȚĂ"));
        Assert.True(folded.Matched, "a non-ASCII term was not folded to the other case");
        Assert.Equal(2, folded.Line);
        Assert.Equal("raport ședință final", folded.Preview);
    }

    [Fact]
    public void A_utf8_bom_is_not_mistaken_for_part_of_the_first_line()
    {
        using var temp = new TempFolder();
        var file = Write(temp, "bom.txt", new UTF8Encoding(true), "TODO first line\nsecond\n");

        var hit = Match(file, Query("TODO"));

        Assert.True(hit.Matched);
        Assert.Equal(1, hit.Line);
        Assert.Equal("TODO first line", hit.Preview);
    }

    [Fact]
    public void A_preview_cut_out_of_a_long_line_never_splits_a_character()
    {
        using var temp = new TempFolder();
        var pad = string.Concat(Enumerable.Repeat("ăîșță", 200));   // two bytes each
        var file = temp.AddFile("lung.txt", pad + "#NEEDLE" + pad);   // the odd byte puts the cut mid-character

        var preview = Match(file, Query("NEEDLE")).Preview;

        Assert.NotNull(preview);
        Assert.Contains("NEEDLE", preview);
        Assert.DoesNotContain("�", preview);
    }

    [Fact]
    public void Utf16_text_with_a_bom_is_searched_rather_than_taken_for_binary()
    {
        using var temp = new TempFolder();
        var le = Write(temp, "le.txt", Encoding.Unicode, "alpha\r\nbeta TODO gamma\r\n");
        var be = Write(temp, "be.txt", Encoding.BigEndianUnicode, "alpha\r\nbeta TODO gamma\r\n");

        var hit = Match(le, Query("todo"));
        Assert.True(hit.Matched);
        Assert.Equal(2, hit.Line);
        Assert.Equal("beta TODO gamma", hit.Preview);
        Assert.True(Match(be, Query("todo")).Matched);
    }

    [Fact]
    public void Utf16_text_without_a_bom_is_guessed_from_where_the_nulls_fall()
    {
        using var temp = new TempFolder();
        var text = "alpha beta gamma\r\nbeta TODO gamma\r\n";
        var le = Path.Combine(temp.Root, "nobom.txt");
        File.WriteAllBytes(le, Encoding.Unicode.GetBytes(text));

        var hit = Match(le, Query("TODO"));

        Assert.True(hit.Matched);
        Assert.Equal(2, hit.Line);
    }

    [Fact]
    public void A_binary_file_is_skipped_even_when_the_bytes_spell_the_term()
    {
        using var temp = new TempFolder();
        var bytes = new byte[512];
        Random.Shared.NextBytes(bytes);
        bytes[7] = 0;
        bytes[40] = 0;
        "TODO"u8.CopyTo(bytes.AsSpan(100));
        var file = Path.Combine(temp.Root, "lib.dll");
        File.WriteAllBytes(file, bytes);

        var skipped = Match(file, Query("TODO"));

        Assert.False(skipped.Matched);
        Assert.Equal(SkipReason.Binary, skipped.Skip);
    }

    [Fact]
    public void A_file_over_the_cap_is_skipped_without_being_opened()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("big.log", "TODO");
        var never = Path.Combine(temp.Root, "does-not-exist.log");

        var capped = new ContentMatcher(Query("TODO", max: 3)).Match(file, 4, CancellationToken.None);
        Assert.Equal(SkipReason.TooLarge, capped.Skip);

        // Reported as too large, not unreadable: nothing was opened.
        var unopened = new ContentMatcher(Query("TODO", max: 3)).Match(never, 4, CancellationToken.None);
        Assert.Equal(SkipReason.TooLarge, unopened.Skip);

        Assert.True(new ContentMatcher(Query("TODO", max: 4)).Match(file, 4, CancellationToken.None).Matched);
    }

    [Fact]
    public void A_file_held_open_exclusively_is_reported_unreadable_rather_than_throwing()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("locked.txt", "TODO\n");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = Match(file, Query("TODO"));

            Assert.False(blocked.Matched);
            Assert.Equal(SkipReason.Unreadable, blocked.Skip);
        }

        Assert.True(Match(file, Query("TODO")).Matched);
    }

    [Fact]
    public void A_file_being_written_by_another_process_is_still_searched()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("app.log", "started\nTODO check this\n");

        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.True(Match(file, Query("TODO")).Matched);
    }

    [Fact]
    public void A_file_that_is_gone_is_unreadable_rather_than_an_exception()
    {
        var missing = Path.Combine(Path.GetTempPath(), "oan-missing-" + Guid.NewGuid().ToString("N") + ".txt");

        var gone = new ContentMatcher(Query("TODO")).Match(missing, 10, CancellationToken.None);

        Assert.Equal(SkipReason.Unreadable, gone.Skip);
    }

    [Fact]
    public void A_regex_matches_and_reports_the_line_it_hit()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("ids.txt", "nothing here\nuser id=4711 ok\n");

        var hit = Match(file, Query(@"id=\d+", regex: true));

        Assert.True(hit.Matched);
        Assert.Equal(2, hit.Line);
        Assert.Equal("user id=4711 ok", hit.Preview);
        Assert.True(Match(file, Query(@"ID=\d+", regex: true)).Matched);
        Assert.False(Match(file, Query(@"ID=\d+", regex: true, caseSensitive: true)).Matched);
    }

    [Fact]
    public void A_catastrophic_pattern_returns_instead_of_locking_the_search_up()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("bait.txt", new string('a', 5000) + "!\n");

        var clock = Stopwatch.StartNew();
        var result = Match(file, Query("(a+)+$", regex: true));
        clock.Stop();

        Assert.False(result.Matched);
        Assert.Equal(SkipReason.None, result.Skip);   // a timeout would show up here as Unreadable
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"the pattern took {clock.Elapsed.TotalSeconds:0.0}s, which is a lock-up in the making");
    }

    [Fact]
    public void A_pattern_the_linear_engine_cannot_run_is_named_rather_than_silently_missing()
    {
        var lookbehind = Query(@"(?<=x)y", regex: true);
        var backreference = Query(@"(\w)\1", regex: true);

        // SearchQuery.Invalid parses the pattern happily; only the engine knows it cannot run it.
        Assert.Null(lookbehind.Invalid());
        Assert.NotNull(ContentMatcher.Unsupported(lookbehind));
        Assert.NotNull(ContentMatcher.Unsupported(backreference));
        Assert.Throws<ArgumentException>(() => new ContentMatcher(lookbehind));

        Assert.Null(ContentMatcher.Unsupported(Query(@"id=\d+", regex: true)));
        Assert.Null(ContentMatcher.Unsupported(Query("plain text")));
    }

    [Fact]
    public void Cancellation_is_raised_rather_than_answered_as_a_miss()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("notes.txt", "TODO\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => new ContentMatcher(Query("TODO")).Match(file, 5, cts.Token));
    }

    [Fact]
    public void A_query_without_a_content_term_is_a_mistake_not_a_matcher_that_finds_nothing()
    {
        Assert.Throws<ArgumentException>(() => new ContentMatcher(new SearchQuery { Root = @"C:\", Name = "x" }));
    }

    static SearchQuery Query(string content, bool regex = false, bool caseSensitive = false, long max = 64L * 1024 * 1024) =>
        new()
        {
            Root = @"C:\",
            Content = content,
            ContentIsRegex = regex,
            CaseSensitive = caseSensitive,
            MaxScanBytes = max,
        };

    static ContentResult Match(string path, SearchQuery query) =>
        new ContentMatcher(query).Match(path, new FileInfo(path).Length, CancellationToken.None);

    static string Write(TempFolder temp, string name, Encoding encoding, string text)
    {
        var full = Path.Combine(temp.Root, name);
        File.WriteAllBytes(full, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
        return full;
    }
}
