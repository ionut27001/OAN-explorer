using System.Globalization;
using Oan.Core.Model;
using Xunit;

namespace Oan.Tests;

public class FormatTests
{
    [Fact]
    public void Unknown_size_is_an_em_dash_and_never_zero_bytes()
    {
        Assert.Equal("—", Format.Size(null));
        Assert.NotEqual("0 bytes", Format.Size(null));
    }

    [Fact]
    public void Empty_file_is_zero_bytes()
    {
        Assert.Equal("0 bytes", Format.Size(0));
        Assert.NotEqual(Format.Size(null), Format.Size(0));
    }

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1500000000000, "1.4 TB")]
    public void Size_uses_binary_units(long bytes, string expected)
    {
        using var invariant = new CultureScope("");

        Assert.Equal(expected, Format.Size(bytes));
    }

    [Fact]
    public void Size_above_a_terabyte_is_not_truncated_to_gigabytes()
    {
        var text = Format.Size(5L * 1024 * 1024 * 1024 * 1024);
        Assert.Equal("5 TB", text);
    }

    [Fact]
    public void Largest_size_does_not_overflow_the_unit_table() =>
        Assert.EndsWith(" PB", Format.Size(long.MaxValue), StringComparison.Ordinal);

    [Fact]
    public void Negative_size_is_unknown_not_a_negative_number() =>
        Assert.Equal("—", Format.Size(-1));

    [Fact]
    public void Unknown_date_is_an_em_dash() => Assert.Equal("—", Format.Date(null));

    [Fact]
    public void Known_date_is_formatted_and_follows_the_current_culture()
    {
        var when = new DateTime(2024, 3, 9, 14, 5, 0, DateTimeKind.Local);

        using var invariant = new CultureScope("");
        Assert.Equal("03/09/2024 14:05", Format.Date(when));

        using var german = new CultureScope("de-DE");
        Assert.NotEqual("03/09/2024 14:05", Format.Date(when));
    }

    [Fact]
    public void Kind_names_folders_files_and_extensionless_files()
    {
        Assert.Equal("File folder", Format.Kind(Entry("stuff", FileAttributes.Directory)));
        Assert.Equal("TXT file", Format.Kind(Entry("notes.txt", FileAttributes.Normal)));
        Assert.Equal("File", Format.Kind(Entry("LICENSE", FileAttributes.Normal)));
    }

    static FileEntry Entry(string name, FileAttributes attributes) =>
        new() { Name = name, FullPath = @"C:\" + name, Attributes = attributes };
}

/// <summary>Pins the ambient culture for one test and puts the previous one back.</summary>
sealed class CultureScope : IDisposable
{
    readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
