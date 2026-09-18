using System.Text.RegularExpressions;

namespace Oan.Core.Search;

public enum NameMatch { Substring, Glob, Regex }

public sealed record SearchQuery
{
    public required string Root { get; init; }

    /// <summary>Null means every name matches; the content term then decides.</summary>
    public string? Name { get; init; }
    public NameMatch NameMode { get; init; } = NameMatch.Substring;

    /// <summary>Null means names only — no file is opened.</summary>
    public string? Content { get; init; }
    public bool ContentIsRegex { get; init; }

    public bool CaseSensitive { get; init; }
    public bool IncludeHidden { get; init; }

    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedAfter { get; init; }
    public DateTime? ModifiedBefore { get; init; }

    /// <summary>Files larger than this are reported as skipped rather than read.</summary>
    public long MaxScanBytes { get; init; } = 64L * 1024 * 1024;

    public bool SearchesContent => !string.IsNullOrEmpty(Content);

    /// <summary>
    /// Null when the query is usable. A message when it is not — an unparsable regex is the
    /// common case, and it must be told apart from a valid pattern that simply found nothing.
    /// </summary>
    public string? Invalid()
    {
        if (string.IsNullOrWhiteSpace(Root)) return "No folder to search.";
        if (string.IsNullOrEmpty(Name) && !SearchesContent) return "Nothing to search for.";

        if (NameMode == NameMatch.Regex && Name is not null && Bad(Name) is { } n) return $"Name pattern: {n}";
        if (ContentIsRegex && Content is not null && Bad(Content) is { } c) return $"Content pattern: {c}";
        return null;

        static string? Bad(string pattern)
        {
            try { _ = new Regex(pattern); return null; }
            catch (ArgumentException ex) { return ex.Message; }
        }
    }
}

/// <summary>Where a match was found. Line and Preview are null for a name-only match.</summary>
public sealed record SearchHit
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required long? Size { get; init; }
    public required DateTime? Modified { get; init; }
    public int? Line { get; init; }
    public string? Preview { get; init; }
}

/// <summary>
/// What the run could not do. Every count here is a reason a result set is a floor rather than
/// a total, and the UI has to be able to say so.
/// </summary>
public sealed record SearchTally
{
    public int FilesSeen { get; init; }
    public int FoldersSeen { get; init; }
    public int Denied { get; init; }
    public int TooLarge { get; init; }
    public int Binary { get; init; }
    public int Unreadable { get; init; }
    public bool Complete => Denied == 0 && Unreadable == 0;
}
