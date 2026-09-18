namespace Oan.Core.Navigation;

/// <summary>Back/forward stacks, the same model the browser and Explorer use.</summary>
public sealed class History
{
    private readonly List<string> _entries = [];
    private int _index = -1;

    public string? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public void Visit(string path)
    {
        if (string.Equals(Current, path, StringComparison.OrdinalIgnoreCase)) return;

        // Navigating after going back drops the forward entries, as every browser does.
        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(path);
        _index = _entries.Count - 1;
    }

    public string? Back() => CanGoBack ? _entries[--_index] : null;
    public string? Forward() => CanGoForward ? _entries[++_index] : null;

    public static string? Parent(string path)
    {
        var parent = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar));
        return parent?.FullName;
    }
}
