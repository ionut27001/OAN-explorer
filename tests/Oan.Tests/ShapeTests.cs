using System.Runtime.CompilerServices;
using Xunit;

namespace Oan.Tests;

/// <summary>
/// The shape of the code, not its behaviour.
///
/// The previous OAN reached 53,000 lines without being able to copy a file, one reasonable-looking
/// mechanism at a time. A long file and a long method are how that starts, so the limit is checked
/// rather than remembered. Exceptions are listed with a reason and a line count they may not
/// exceed — a limit with an open-ended exception list is not a limit.
/// Not part of the normal test run. Building a mechanism means trying things, and a length
/// limit going red halfway through is noise, not help — so this is the step you take when the
/// mechanism is finished and works:
///
///     dotnet test --filter "Category=Shape"
///
/// </summary>
[Trait("Category", "Shape")]
public class ShapeTests
{
    private const int MaxFileLines = 400;
    private const int MaxTestFileLines = 520;
    private const int MaxMethodLines = 55;

    /// <summary>Known exceptions: path, the ceiling it may not pass, and why it is here at all.</summary>
    private static readonly (string File, int Ceiling, string Why)[] Allowed =
    [
        // 928 -> 965 when mounting landed, which is what tripped this test; search moved out to
        // SearchViewModel and it is 904 now. The ceiling follows it down each time, so the only
        // direction this file can go is smaller. Still holding navigation, selection, clipboard,
        // file operations, the sidebar and settings — clipboard is the next one out.
        ("src/Oan.App/ViewModels/MainViewModel.cs", 910,
            "Several concerns still in one class; the ceiling ratchets down as they leave."),
        ("src/Oan.App/MainWindow.xaml", 540, "One window's markup. XAML does not factor the way code does."),
        ("src/Oan.App/Themes/Controls.xaml", 430, "Control templates for the whole app, in one dictionary."),
    ];

    private static IEnumerable<FileInfo> Sources() =>
        new DirectoryInfo(Repo.Root)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".cs" or ".xaml")
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void No_file_grows_past_its_limit()
    {
        var over = new List<string>();

        foreach (var file in Sources())
        {
            var relative = Repo.Relative(file.FullName);
            var lines = File.ReadAllLines(file.FullName).Length;
            var ceiling = Allowed.FirstOrDefault(a => a.File == relative) is { Ceiling: > 0 } known
                ? known.Ceiling
                : relative.StartsWith("tests/", StringComparison.Ordinal) ? MaxTestFileLines : MaxFileLines;

            if (lines > ceiling) over.Add($"{relative}: {lines} lines, limit {ceiling}");
        }

        Assert.True(over.Count == 0,
            "Split it, or add it to Allowed with a written reason:\n  " + string.Join("\n  ", over));
    }

    [Fact]
    public void An_exception_that_has_shrunk_below_the_default_is_removed_from_the_list()
    {
        // Otherwise the list only ever grows, and in a year it is the whole project.
        var stale = Allowed
            .Where(a => File.Exists(Path.Combine(Repo.Root, a.File)))
            .Where(a => File.ReadAllLines(Path.Combine(Repo.Root, a.File)).Length <= MaxFileLines)
            .Select(a => a.File)
            .ToList();

        Assert.True(stale.Count == 0, "No longer needed in Allowed: " + string.Join(", ", stale));
    }

    [Fact]
    public void No_method_is_longer_than_a_screen()
    {
        var over = new List<string>();

        foreach (var file in Sources().Where(f => f.Extension == ".cs" && !f.FullName.Contains("tests")))
        {
            var lines = File.ReadAllLines(file.FullName);
            var depth = 0;
            var start = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var opens = lines[i].Count(c => c == '{');
                var closes = lines[i].Count(c => c == '}');

                var before = depth;
                depth += opens - closes;

                // A member body opens when depth crosses 1 -> 2: the namespace is file-scoped,
                // so the type is 1 and the member is 2. Testing `depth == 2` before adding, as
                // the first version did, never matched — the brace that enters the body is read
                // while depth is still 1, and the test silently measured nothing.
                if (before == 1 && depth == 2 && start < 0 && !DeclaresType(lines, i)) start = i;

                if (start >= 0 && depth <= 1)
                {
                    if (i - start > MaxMethodLines)
                        over.Add($"{Repo.Relative(file.FullName)}:{start + 1} — {i - start} lines");
                    start = -1;
                }
            }
        }

        Assert.True(over.Count == 0, "Longer than " + MaxMethodLines + " lines:\n  " + string.Join("\n  ", over));
    }

    /// <summary>
    /// A nested type's body also sits at depth 2, and it is allowed to be long for reasons a
    /// method is not. Looks at the brace's own line and the one above it, which is where the
    /// declaration lives when the brace is on its own.
    /// </summary>
    private static bool DeclaresType(string[] lines, int brace)
    {
        string[] words = ["class ", "record ", "struct ", "interface ", "enum "];

        for (var i = Math.Max(0, brace - 1); i <= brace; i++)
        {
            if (words.Any(w => lines[i].Contains(w, StringComparison.Ordinal))) return true;
        }
        return false;
    }
}

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Relative(string full) =>
        Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');

    // From the source path, not AppContext.BaseDirectory: the test binary can be copied
    // somewhere with no repo above it, and then these tests silently check nothing.
    private static string Find([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Directory.Build.props not found above {here}");
    }
}
