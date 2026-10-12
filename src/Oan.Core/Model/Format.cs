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
        if (bytes == 1) return "1 byte";

        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }

        var digits = unit == 0 ? 0 : value < 10 ? 1 : 0;
        return string.Create(CultureInfo.CurrentCulture, $"{Math.Round(value, digits)} {Units[unit]}");
    }

    public static string Date(DateTime? when) =>
        when?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    /// <summary>Explorer's letters, in its order: R H S D A C E.</summary>
    public static string Attributes(FileAttributes attributes) =>
        string.Concat(Letters.Where(l => (attributes & l.Flag) != 0).Select(l => l.Letter));

    private static readonly (FileAttributes Flag, char Letter)[] Letters =
    [
        (FileAttributes.ReadOnly, 'R'), (FileAttributes.Hidden, 'H'), (FileAttributes.System, 'S'),
        (FileAttributes.Directory, 'D'), (FileAttributes.Archive, 'A'), (FileAttributes.Compressed, 'C'),
        (FileAttributes.Encrypted, 'E'),
    ];

    public static string Kind(FileEntry entry)
    {
        if (entry.IsDirectory) return "File folder";
        var ext = entry.Extension;
        return ext.Length > 1 ? $"{ext[1..].ToUpperInvariant()} file" : "File";
    }
}
