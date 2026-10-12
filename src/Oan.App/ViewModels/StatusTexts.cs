using Oan.Core.Model;

namespace Oan.App.ViewModels;

/// <summary>What the status bar and the empty list say. Pure: the same state always reads the same.</summary>
internal static class StatusTexts
{
    public static string Status(SearchViewModel search, bool inArchive, string path, int rows, int total,
        bool filtering, IReadOnlyList<EntryViewModel> selection)
    {
        var items = $"{rows} item{(rows == 1 ? "" : "s")}";

        if (search.Active)
        {
            var head = search.Running ? $"Searching… {rows} found" : $"{rows} found";
            return $"{head}  ·  {search.Tally.FilesSeen:N0} files in {search.Tally.FoldersSeen:N0} folders" + Selected(selection);
        }

        if (inArchive) return $"{items} in this archive — read only" + Selected(selection);
        if (path.Length == 0) return "";

        return (filtering ? $"{rows} of {total} items" : items) + Selected(selection);
    }

    /// <summary>A partial listing or an incomplete search is never silent.</summary>
    public static string? Warning(SearchViewModel search, DirectoryListing listing)
    {
        if (search.Error is not null) return search.Error;

        if (search.Active && !search.Tally.Complete)
        {
            var parts = new List<string>();
            if (search.Tally.Denied > 0) parts.Add($"{search.Tally.Denied} folder(s) denied");
            if (search.Tally.Unreadable > 0) parts.Add($"{search.Tally.Unreadable} file(s) unreadable");
            if (search.Tally.TooLarge > 0) parts.Add($"{search.Tally.TooLarge} too large to scan");
            if (parts.Count > 0) return "Results are a floor, not a total — " + string.Join(", ", parts);
        }

        if (listing.Error is not null) return listing.Error;
        return listing.Skipped > 0 ? $"{listing.Skipped} item(s) in this folder could not be read" : null;
    }

    /// <summary>A failed listing never reads as an empty folder.</summary>
    public static string? Empty(SearchViewModel search, DirectoryListing listing, int rows, bool filtering)
    {
        if (listing.Error is not null) return listing.Error;
        if (rows > 0 || listing.Path.Length == 0) return null;
        if (search.Active) return search.Running ? null : "Nothing matched.";
        return filtering ? "No items match the filter." : "This folder is empty.";
    }

    /// <summary>The size only when every one is known: a total that leaves the folders out is wrong.</summary>
    private static string Selected(IReadOnlyList<EntryViewModel> selection) =>
        selection.Count == 0 ? "" :
        selection.All(r => r.Entry.Size is not null)
            ? $"  ·  {selection.Count} selected  ·  {Format.Size(selection.Sum(r => r.Entry.Size!.Value))}"
            : $"  ·  {selection.Count} selected";
}
