using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// One label on one tile, taken apart. The size map spends most of a paint writing names on
/// rectangles, and Cushions builds a fresh Typeface and a fresh FormattedText for every one of
/// them — this says what each of those costs on its own, so the next change can be aimed rather
/// than guessed.
///
/// The strings are the same on every round, which is what the real thing does too: a repaint
/// draws the same names again. Any caching WPF does for itself is therefore in these numbers, as
/// it is in the app's.
///
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~TreemapTextBenchmark" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class TreemapTextBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void What_one_label_costs()
    {
        var names = Names();

        foreach (var line in Sta.Run(() => Labelling.Measure(names, rounds: 7)))
            output.WriteLine($"label\t{names.Length}\t{line}");
    }

    /// <summary>Names shaped like the ones on a real map: mixed lengths, mixed extensions.</summary>
    private static string[] Names()
    {
        string[] stems =
        [
            "Oan.App", "TreemapView", "index", "Microsoft.NET.Sdk.WindowsDesktop", "a",
            "System.Private.CoreLib", "setup", "node_modules", "README", "Cushions",
            "verylongassemblynamethatwillbetrimmed.ThatKeepsGoing", "img_20240113_113045",
        ];

        string[] kinds = ["dll", "cs", "png", "json", "txt", "exe", "pdb", "", "vhdx", "log"];

        return [.. Enumerable.Range(0, 500).Select(i =>
        {
            var kind = kinds[i % kinds.Length];
            var stem = stems[i % stems.Length];
            return kind.Length == 0 ? $"{stem}{i}" : $"{stem}{i}.{kind}";
        })];
    }
}

/// <summary>The pieces of Cushions.Label, timed one at a time.</summary>
internal static class Labelling
{
    private static readonly Typeface Kept = new("Segoe UI");
    private static readonly Rect Tile = new(0, 0, 120, 40);

    public static string[] Measure(string[] names, int rounds)
    {
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var visual = new DrawingVisual();

        return
        [
            Time("typeface_only", names, rounds, _ => Sink(new Typeface("Segoe UI"))),
            Time("formatted_fresh", names, rounds, name => Sink(Built(name, new Typeface("Segoe UI"), dpi).Height)),
            Time("formatted_kept", names, rounds, name => Sink(Built(name, Kept, dpi).Height)),
            Time("formatted_kept_plain", names, rounds, name => Sink(Plain(name, Kept, dpi).Height)),
            Drawn("drawn_fresh", names, rounds, visual, dpi, fresh: true),
            Drawn("drawn_kept", names, rounds, visual, dpi, fresh: false),
        ];
    }

    /// <summary>What Cushions.Text builds: one line, trimmed to the width of the tile.</summary>
    private static FormattedText Built(string what, Typeface face, double dpi) =>
        new(what, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12, Brushes.Black, dpi)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Tile.Width - 8,
        };

    /// <summary>The same without the trimming or the width, to see what the trimming costs.</summary>
    private static FormattedText Plain(string what, Typeface face, double dpi) =>
        new(what, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12, Brushes.Black, dpi);

    private static string Time(string what, string[] names, int rounds, Action<string> work)
    {
        for (var i = 0; i < names.Length; i++) work(names[i]);

        var times = new double[rounds];
        long bytes = 0;

        for (var r = 0; r < rounds; r++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();

            foreach (var name in names) work(name);

            clock.Stop();
            times[r] = clock.Elapsed.TotalMilliseconds;
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var ms = Spread.Middle(times);
        return $"{what}\tms={ms:F2}\tus_each={ms * 1000 / names.Length:F1}\t" +
               $"kb={bytes / rounds / 1024.0:F0}\tbytes_each={bytes / rounds / names.Length}";
    }

    /// <summary>Everything Cushions.Label does for one tile, clip and DrawText included.</summary>
    private static string Drawn(string what, string[] names, int rounds, DrawingVisual visual, double dpi, bool fresh)
    {
        var times = new double[rounds];
        long bytes = 0;

        for (var r = 0; r <= rounds; r++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();

            using (var dc = visual.RenderOpen())
            {
                foreach (var name in names)
                {
                    var label = Built(name, fresh ? new Typeface("Segoe UI") : Kept, dpi);
                    dc.PushClip(new RectangleGeometry(Tile));
                    dc.DrawText(label, new Point(4, 3));
                    dc.Pop();
                }
            }

            clock.Stop();
            if (r == 0) continue;

            times[r - 1] = clock.Elapsed.TotalMilliseconds;
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var ms = Spread.Middle(times);
        return $"{what}\tms={ms:F2}\tus_each={ms * 1000 / names.Length:F1}\t" +
               $"kb={bytes / rounds / 1024.0:F0}\tbytes_each={bytes / rounds / names.Length}";
    }

    /// <summary>Somewhere for a result to go, so none of this can be optimised away.</summary>
    private static void Sink(object value) => Held = value;

    private static void Sink(double value) => Held = value;

    private static object? Held { get; set; }
}
