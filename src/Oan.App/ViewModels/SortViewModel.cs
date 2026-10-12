using Oan.App.Platform;

namespace Oan.App.ViewModels;

/// <summary>The order the list is in, and the arrows in the headers that say so. Owns no rows.</summary>
public sealed class SortViewModel : Observable
{
    private SortKey _key = SortKey.Name;
    private bool _descending;

    public event Action? Changed;

    public SortKey Key => _key;

    public string NameHeader => Header("Name", SortKey.Name);
    public string SizeHeader => Header("Size", SortKey.Size);
    public string KindHeader => Header("Type", SortKey.Kind);
    public string ModifiedHeader => Header("Date modified", SortKey.Modified);
    public string CreatedHeader => Header("Date created", SortKey.Created);
    public string AttributesHeader => Header("Attributes", SortKey.Attributes);

    /// <summary>Clicking the column you are already sorted by turns the order around.</summary>
    public void By(SortKey key)
    {
        if (_key == key) _descending = !_descending;
        else (_key, _descending) = (key, false);

        OnChanged(nameof(NameHeader));
        OnChanged(nameof(SizeHeader));
        OnChanged(nameof(KindHeader));
        OnChanged(nameof(ModifiedHeader));
        OnChanged(nameof(CreatedHeader));
        OnChanged(nameof(AttributesHeader));
        Changed?.Invoke();
    }

    /// <summary>
    /// The same rows in order, or null when they were already in it. Folder sizes arrive seconds after the
    /// rows, so a size order has to be redone; the rows themselves are reused, so no icon is refetched.
    /// </summary>
    public EntryViewModel[]? Reorder(IReadOnlyList<EntryViewModel> rows)
    {
        var sorted = rows.ToArray();
        Array.Sort(sorted, Compare);
        return sorted.SequenceEqual(rows) ? null : sorted;
    }

    public int Compare(EntryViewModel x, EntryViewModel y)
    {
        var (a, b) = (x.Entry, y.Entry);

        // Folders first, whichever column and whichever direction.
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        var order = _key switch
        {
            SortKey.Size => Nullable.Compare(x.SortSize, y.SortSize),
            // Ordinal: a token this program built, not language, on the hot path of the comparer.
            SortKey.Kind => string.CompareOrdinal(x.KindText, y.KindText),
            SortKey.Modified => Nullable.Compare(a.Modified, b.Modified),
            SortKey.Created => Nullable.Compare(a.Created, b.Created),
            SortKey.Attributes => string.CompareOrdinal(x.AttributesText, y.AttributesText),
            _ => NaturalSort.Instance.Compare(a.Name, b.Name),
        };

        // The name is the last term, before the direction is applied: every folder has the same Type, and a
        // tie-break that ignored the direction made the second click produce an identical list.
        if (order == 0) order = NaturalSort.Instance.Compare(a.Name, b.Name);

        // Sign, because string.Compare only promises a sign, and negating int.MinValue keeps it.
        return _descending ? -Math.Sign(order) : order;
    }

    private string Header(string text, SortKey key) =>
        _key == key ? $"{text}  {(_descending ? '▾' : '▴')}" : text;
}
