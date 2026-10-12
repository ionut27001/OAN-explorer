using System.IO;
using Oan.Core.Model;
using Oan.Core.Search;

namespace Oan.App.ViewModels;

/// <summary>
/// One search, from the query to the rows it produces. The rows are kept here: the listing still holds the
/// folder the search started in, and a sort or filter rebuilt from that would replace the answers.
/// </summary>
internal sealed class SearchRuns(SearchViewModel search)
{
    /// <summary>Rows added per redraw, so the list grows visibly without redrawing once per file found.</summary>
    private const int Batch = 64;

    private readonly List<EntryViewModel> _found = [];

    public IReadOnlyList<EntryViewModel> Found => _found;

    public void Forget() => _found.Clear();

    /// <param name="show">Raised on the UI thread whenever <see cref="Found"/> has grown, and once at the start.</param>
    public async Task RunAsync(string query, string where, bool hidden, int iconSize, Action show)
    {
        if (search.Build(query, where, hidden) is not { } built) return;

        using var run = search.Begin();
        _found.Clear();
        show();

        var engine = new FileSearch(built.SearchesContent ? new ContentMatcher(built) : null);
        var batch = new List<EntryViewModel>(Batch);
        string? error = null;

        try
        {
            await foreach (var hit in engine.RunAsync(built, run.Token).ConfigureAwait(true))
            {
                batch.Add(Row(hit, iconSize));
                if (batch.Count >= Batch) Flush(batch, engine, show);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            if (search.Finish(run, engine.Tally, error)) Flush(batch, engine, show);
        }
    }

    private void Flush(List<EntryViewModel> batch, FileSearch engine, Action show)
    {
        if (batch.Count > 0)
        {
            _found.AddRange(batch);
            batch.Clear();
            show();
        }

        search.Progress(engine.Tally);
    }

    private static EntryViewModel Row(SearchHit hit, int iconSize) => new(
        new FileEntry
        {
            Name = hit.Name,
            FullPath = hit.FullPath,
            Attributes = hit.Attributes,
            Size = hit.Size,
            Modified = hit.Modified,
        },
        iconSize);
}
