using Oan.Core.Search;

namespace Oan.App.ViewModels;

/// <summary>The question the search box is asking, and what came back of it. The rows stay with MainViewModel.</summary>
public sealed class SearchViewModel : Observable
{
    private CancellationTokenSource? _running;
    private SearchTally _tally = new();
    private bool _active;
    private bool _contents;
    private bool _regex;
    private bool _matchCase;
    private string? _error;

    /// <summary>Raised whenever anything the status bar reads has changed.</summary>
    public event Action? Changed;

    /// <summary>The list is showing hits from a walk rather than the current folder.</summary>
    public bool Active
    {
        get => _active;
        private set { if (Set(ref _active, value)) Changed?.Invoke(); }
    }

    public bool Running => _running is not null;

    public bool Contents { get => _contents; set => Set(ref _contents, value); }
    public bool Regex { get => _regex; set => Set(ref _regex, value); }
    public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value); }

    public SearchTally Tally => _tally;

    /// <summary>Why the search could not run, or null. A pattern that will not compile lands here.</summary>
    public string? Error => _error;

    public CancellationToken Token => _running?.Token ?? CancellationToken.None;

    /// <summary>The query, or null. A regex that does not compile is an error, never a search that found nothing.</summary>
    public SearchQuery? Build(string term, string root, bool includeHidden)
    {
        term = term.Trim();
        if (term.Length == 0 || root.Length == 0) return null;

        var query = new SearchQuery
        {
            Root = root,
            Name = Contents ? null : term,
            NameMode = Regex ? NameMatch.Regex : NameMatch.Substring,
            Content = Contents ? term : null,
            ContentIsRegex = Contents && Regex,
            CaseSensitive = MatchCase,
            IncludeHidden = includeHidden,
        };

        if (query.Invalid() is not { } bad) return query;

        _error = bad;
        Changed?.Invoke();
        return null;
    }

    /// <summary>Cancels whatever was running and hands back the token for the new run.</summary>
    public CancellationTokenSource Begin()
    {
        Stop();

        var cts = new CancellationTokenSource();
        _running = cts;
        _error = null;
        _tally = new SearchTally();
        Active = true;
        Changed?.Invoke();
        return cts;
    }

    public void Progress(SearchTally tally)
    {
        _tally = tally;
        Changed?.Invoke();
    }

    /// <summary>True when this run is still the current one — a later search supersedes it.</summary>
    public bool Finish(CancellationTokenSource run, SearchTally tally, string? error)
    {
        if (_running != run) return false;

        _running = null;
        _tally = tally;
        if (error is not null) _error = error;
        OnChanged(nameof(Running));
        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        var running = _running;
        _running = null;
        running?.Cancel();
        OnChanged(nameof(Running));
        Changed?.Invoke();
    }

    /// <summary>Leaves results mode. The caller reloads the folder the search started from.</summary>
    public void Leave()
    {
        Stop();
        Active = false;
    }
}
