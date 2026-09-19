using Oan.App.Platform;

namespace Oan.App.ViewModels;

/// <summary>
/// The order the list is in, and the arrows in the headers that say so.
///
/// Pure: it takes two rows and answers which comes first. It owns no rows and asks nobody to
/// redraw — <see cref="Changed"/> says the order moved and MainViewModel decides what that means.
/// </summary>
public sealed class SortViewModel : Observable
{
    private SortKey _key = SortKey.Name;
    private bool _descending;

    public event Action? Changed;

    /// <summary>What the list is ordered by. Sizes arrive late, so somebody has to ask.</summary>
    public SortKey Key => _key;

    public string NameHeader => Header("Name", SortKey.Name);
    public string SizeHeader => Header("Size", SortKey.Size);
    public string KindHeader => Header("Type", SortKey.Kind);
    public string ModifiedHeader => Header("Date modified", SortKey.Modified);

    /// <summary>Clicking the column you are already sorted by turns the order around.</summary>
    public void By(SortKey key)
    {
        if (_key == key) _descending = !_descending;
        else (_key, _descending) = (key, false);

        OnChanged(nameof(NameHeader));
        OnChanged(nameof(SizeHeader));
        OnChanged(nameof(KindHeader));
        OnChanged(nameof(ModifiedHeader));
        Changed?.Invoke();
    }

    /// <summary>
    /// The same rows in order, or null when they were already in it. Folder sizes arrive seconds
    /// after the rows were placed, so an order that depends on them has to be redone; the rows
    /// themselves are handed back untouched, which is what keeps their icons from being refetched.
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

        // Folders first, whichever column and whichever direction: a reversed sort that mixed
        // them into the files would move a folder somewhere nobody looks for it.
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        var order = _key switch
        {
            SortKey.Size => Nullable.Compare(x.SortSize, y.SortSize),
            // Ordinal: this is a token this program built, not language. Every other comparison
            // in here is ordinal or StrCmpLogicalW, and a culture collation would be the one
            // exception, on the hot path of a comparer called O(n log n) times.
            SortKey.Kind => string.CompareOrdinal(x.KindText, y.KindText),
            SortKey.Modified => Nullable.Compare(a.Modified, b.Modified),
            _ => NaturalSort.Instance.Compare(a.Name, b.Name),
        };

        // The name is the last term of the comparison, not a fallback applied after the
        // direction: every folder has the same Type, so ordering by Type is a field of ties, and
        // a tie-break left pointing one way made the second click produce a bit-identical list
        // while the header drew an arrow saying otherwise.
        if (order == 0) order = NaturalSort.Instance.Compare(a.Name, b.Name);

        // Sign, because string.Compare only promises a sign, and negating int.MinValue keeps it.
        return _descending ? -Math.Sign(order) : order;
    }

    private string Header(string text, SortKey key) =>
        _key == key ? $"{text}  {(_descending ? '▾' : '▴')}" : text;
}
