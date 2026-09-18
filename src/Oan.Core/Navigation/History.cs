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

    /// <summary>The containing folder, or null when there is none above this one.</summary>
    public static string? Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // "C:" without a separator is DRIVE-RELATIVE: Windows resolves it against the
        // process's working directory, so Directory.GetParent("C:") answers with wherever the
        // process happens to be. Up from a drive root would then jump somewhere unrelated.
        if (!Path.IsPathFullyQualified(path)) return null;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return null;

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var trimmed = path.TrimEnd(separators);

        // Already at "C:\" or at a UNC share root — GetParent would climb out of the namespace
        // and invent a local path from "\\server\share".
        if (trimmed.Length <= root.TrimEnd(separators).Length) return null;

        return Directory.GetParent(trimmed)?.FullName;
    }
}
