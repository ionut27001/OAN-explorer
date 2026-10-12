using Oan.App.Platform;

namespace Oan.App.ViewModels;

/// <summary>Which details columns are shown, by sort key; bound as <c>Look.Columns[Size]</c>. Name is always shown.</summary>
public sealed class ColumnSet(Settings settings, Action remember) : Observable
{
    public event Action? Changed;

    public bool this[string key]
    {
        get => key == nameof(SortKey.Name) || settings.Columns.Contains(key);
        set
        {
            if (key == nameof(SortKey.Name) || this[key] == value) return;

            if (value) settings.Columns.Add(key);
            else settings.Columns.Remove(key);

            OnChanged("Item[]");
            Changed?.Invoke();
            remember();
        }
    }
}
