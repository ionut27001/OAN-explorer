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

    public int Compare(EntryViewModel x, EntryViewModel y)
    {
        var (a, b) = (x.Entry, y.Entry);

        // Folders first, whichever column and whichever direction: a reversed sort that mixed
        // them into the files would move a folder somewhere nobody looks for it.
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        var order = _key switch
        {
            SortKey.Size => Nullable.Compare(a.Size, b.Size),
            SortKey.Kind => string.Compare(x.KindText, y.KindText, StringComparison.CurrentCultureIgnoreCase),
            SortKey.Modified => Nullable.Compare(a.Modified, b.Modified),
            _ => NaturalSort.Instance.Compare(a.Name, b.Name),
        };

        if (_descending) order = -order;

        // Ties fall back to the name so the order is stable: without it a re-sort of equal rows
        // shuffles them, and the eye reads that as the list having changed.
        return order != 0 ? order : NaturalSort.Instance.Compare(a.Name, b.Name);
    }

    private string Header(string text, SortKey key) =>
        _key == key ? $"{text}  {(_descending ? '▾' : '▴')}" : text;
}
