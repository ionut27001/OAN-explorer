using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Oan.Core.Search;

public enum SkipReason { None, TooLarge, Binary, Unreadable }

public readonly record struct ContentResult(
    bool Matched, int? Line, string? Preview, SkipReason Skip);

public interface IContentMatcher
{
    ContentResult Match(string path, long size, CancellationToken ct);
}

/// <summary>Looks for the query's content term in one file. A literal term is matched against the raw bytes;
/// regex, UTF-16 files and case-insensitive non-ASCII terms take a decoded line-by-line pass.</summary>
public sealed class ContentMatcher : IContentMatcher
{
    const int ChunkSize = 64 * 1024;
    const int HeadSize = 8 * 1024;
    const int PreviewMax = 200;
    const int PreviewLead = 40;
    static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(2);

    readonly string term;
    readonly byte[] utf8Term;
    readonly bool ignoreCase;
    readonly bool scanBytes;
    readonly long maxScanBytes;
    readonly int bufferSize;
    readonly SearchValues<byte>? firstByte;
    readonly Regex? regex;

    public ContentMatcher(SearchQuery query)
    {
        if (!query.SearchesContent) throw new ArgumentException("The query has no content term.", nameof(query));

        term = query.Content!;
        utf8Term = Encoding.UTF8.GetBytes(term);
        ignoreCase = !query.CaseSensitive;
        maxScanBytes = query.MaxScanBytes;
        bufferSize = Math.Max(ChunkSize, utf8Term.Length * 2);

        if (query.ContentIsRegex)
        {
            if (Unsupported(query) is { } why) throw new ArgumentException(why, nameof(query));
            regex = new Regex(term, Options(query.CaseSensitive), RegexBudget);
        }

        // Byte-level case folding is ASCII-only, so a non-ASCII term needs the decoded pass to
        // fold correctly (s-comma, a-breve); a regex is never run over bytes.
        scanBytes = regex is null && (!ignoreCase || Ascii.IsValid(term));
        if (scanBytes && ignoreCase)
            firstByte = SearchValues.Create([
                (byte)char.ToLowerInvariant((char)utf8Term[0]),
                (byte)char.ToUpperInvariant((char)utf8Term[0])]);
    }

    /// <summary>Null when the content pattern can be run. The linear-time engine rejects backreferences and lookaround,
    /// which SearchQuery.Invalid does not check.</summary>
    public static string? Unsupported(SearchQuery query)
    {
        if (!query.ContentIsRegex || string.IsNullOrEmpty(query.Content)) return null;
        try
        {
            _ = new Regex(query.Content, Options(query.CaseSensitive), RegexBudget);
            return null;
        }
        catch (ArgumentException e) { return e.Message; }
        catch (NotSupportedException e)
        {
            return $"{e.Message.TrimEnd('.')} — backreferences and lookaround can lock a search up, "
                 + "so the linear-time engine does not offer them.";
        }
    }

    static RegexOptions Options(bool caseSensitive) =>
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant
        | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

    public ContentResult Match(string path, long size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (size > maxScanBytes) return Skipped(SkipReason.TooLarge);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            using var stream = Open(path);
            var head = Fill(stream, buffer, 0, HeadSize);
            if (Sniff(buffer.AsSpan(0, head), out var bom) is not { } encoding) return Skipped(SkipReason.Binary);

            return scanBytes && encoding == Encoding.UTF8
                ? InBytes(stream, buffer, Fill(stream, buffer, head, buffer.Length), ct)
                : InText(stream, encoding, bom, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or System.Security.SecurityException or RegexMatchTimeoutException)
        {
            return Skipped(SkipReason.Unreadable);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static ContentResult Skipped(SkipReason reason) => new(false, null, null, reason);

    static FileStream Open(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        // A log another process is writing is exactly the file worth grepping, so share widely.
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = 0,
    });

    static int Fill(Stream stream, byte[] buffer, int from, int upto)
    {
        var n = from;
        while (n < upto)
        {
            var read = stream.Read(buffer, n, upto - n);
            if (read == 0) break;
            n += read;
        }
        return n;
    }

    /// <summary>The file's encoding, or null when it looks binary: a null byte in the first 8 KB, as git does.
    /// Without a BOM, nulls only on odd (or only on even) offsets are guessed to be UTF-16 text.</summary>
    static Encoding? Sniff(ReadOnlySpan<byte> head, out int bom)
    {
        bom = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { bom = 3; return Encoding.UTF8; }
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) { bom = 2; return Encoding.Unicode; }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) { bom = 2; return Encoding.BigEndianUnicode; }

        if (!head.Contains((byte)0)) return Encoding.UTF8;
        if (head.Length < 16) return null;   // too few bytes for the alternation to mean anything

        int even = 0, odd = 0;
        for (var i = 0; i < head.Length; i++)
            if (head[i] == 0) { if ((i & 1) == 0) even++; else odd++; }

        if (even == 0 && odd * 4 >= head.Length) return Encoding.Unicode;
        if (odd == 0 && even * 4 >= head.Length) return Encoding.BigEndianUnicode;
        return null;
    }

    ContentResult InBytes(Stream stream, byte[] buffer, int filled, CancellationToken ct)
    {
        var lines = 0;   // newlines before the start of the current window

        while (filled > 0)
        {
            ct.ThrowIfCancellationRequested();
            var window = buffer.AsSpan(0, filled);
            var hit = Find(window);

            if (hit >= 0)
                return new(true, lines + window[..hit].Count((byte)'\n') + 1,
                           Preview(window, hit, utf8Term.Length), SkipReason.None);
            if (filled < buffer.Length) break;

            // Carry the tail, or a term straddling two reads is silently never seen. The carried
            // bytes are counted in the next window instead, so no line is counted twice.
            var keep = Math.Min(utf8Term.Length - 1, filled);
            lines += window[..(filled - keep)].Count((byte)'\n');
            buffer.AsSpan(filled - keep, keep).CopyTo(buffer);
            filled = Fill(stream, buffer, keep, buffer.Length);
            if (filled == keep) break;
        }
        return new(false, null, null, SkipReason.None);
    }

    int Find(ReadOnlySpan<byte> window)
    {
        if (firstByte is null) return window.IndexOf(utf8Term);

        for (var at = 0; at + utf8Term.Length <= window.Length;)
        {
            var next = window[at..].IndexOfAny(firstByte);
            if (next < 0) break;
            at += next;
            if (at + utf8Term.Length > window.Length) break;
            if (Ascii.EqualsIgnoreCase(window.Slice(at, utf8Term.Length), utf8Term)) return at;
            at++;
        }
        return -1;
    }

    ContentResult InText(Stream stream, Encoding encoding, int bom, CancellationToken ct)
    {
        stream.Position = bom;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, ChunkSize);
        var line = 0;

        while (reader.ReadLine() is { } text)
        {
            ct.ThrowIfCancellationRequested();
            line++;

            if (regex is not null)
            {
                if (regex.Match(text) is { Success: true } m)
                    return new(true, line, Preview(text, m.Index, m.Length), SkipReason.None);
                continue;
            }

            var at = text.IndexOf(term, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (at >= 0) return new(true, line, Preview(text, at, term.Length), SkipReason.None);
        }
        return new(false, null, null, SkipReason.None);
    }

    static string Preview(ReadOnlySpan<byte> window, int at, int length)
    {
        var start = window[..at].LastIndexOf((byte)'\n') + 1;
        var ends = window[at..].IndexOf((byte)'\n');
        var end = ends < 0 ? window.Length : at + ends;

        var from = Math.Max(start, at - PreviewLead);
        var to = Math.Min(end, Math.Max(at + length, from + PreviewMax));
        while (from > start && (window[from] & 0xC0) == 0x80) from--;   // never split a UTF-8 char
        while (to < end && (window[to] & 0xC0) == 0x80) to++;

        return Tidy(Encoding.UTF8.GetString(window[from..to]), from > start, to < end);
    }

    static string Preview(string line, int at, int length)
    {
        var from = Math.Max(0, at - PreviewLead);
        var to = Math.Min(line.Length, Math.Max(at + length, from + PreviewMax));
        return Tidy(line[from..to], from > 0, to < line.Length);
    }

    static string Tidy(string text, bool clippedLeft, bool clippedRight)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]) || chars[i] == (char)0xFEFF) chars[i] = ' ';   // tabs, CR, and a BOM

        return (clippedLeft ? "…" : "") + new string(chars).Trim() + (clippedRight ? "…" : "");
    }
}
