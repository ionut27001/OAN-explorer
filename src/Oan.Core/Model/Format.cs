using System.Globalization;

namespace Oan.Core.Model;

public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Explorer's own convention: powers of 1024 labelled KB/MB/GB, no decimals below 10.
    /// Null renders as an em dash, never as "0 bytes" — not knowing a size and a size of
    /// zero are different facts.
    /// </summary>
    public static string Size(long? bytes)
    {
        if (bytes is null) return "—";
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 bytes";

        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }

        var digits = unit == 0 ? 0 : value < 10 ? 1 : 0;
        return string.Create(CultureInfo.CurrentCulture, $"{Math.Round(value, digits)} {Units[unit]}");
    }

    public static string Date(DateTime? when) =>
        when?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public static string Kind(FileEntry entry)
    {
        if (entry.IsDirectory) return "File folder";
        var ext = entry.Extension;
        return ext.Length > 1 ? $"{ext[1..].ToUpperInvariant()} file" : "File";
    }
}
