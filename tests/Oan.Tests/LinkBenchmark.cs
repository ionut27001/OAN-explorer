using System.Diagnostics;
using System.Runtime.InteropServices;
using Oan.Core.Interop;
using Oan.Core.Links;
using Oan.Core.Shell;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What shortcuts, reparse points and long paths actually cost on this machine, and how the
/// machine's own Start Menu answers. Nothing here asserts a number — a measurement that fails the
/// build on a different machine is a measurement nobody trusts.
///
/// Kept out of the normal pass by Category=Benchmark, which Oan.Tests.csproj excludes. Run it on
/// purpose:
///   dotnet test --filter "Category=Benchmark&amp;FullyQualifiedName~LinkBenchmark" --logger "console;verbosity=detailed"
///
/// OAN_LINK_VOLUMES (semicolon-separated roots) points the MAX_PATH sweep at more volumes than
/// %TEMP%; 8.3 name generation differs per volume and that is the thing worth sweeping.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class LinkBenchmark(ITestOutputHelper output)
{
    static string[] StartMenus =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
    ];

    /// <summary>
    /// The machine's own shortcuts, read the way OAN would read them. Two hundred of them, none
    /// written by this suite, which is the only sample that says anything about the real world.
    /// </summary>
    [Fact]
    public void Every_shortcut_in_the_Start_Menu()
    {
        var links = StartMenus
            .Where(Directory.Exists)
            .SelectMany(menu => Directory.EnumerateFiles(menu, "*", SearchOption.AllDirectories))
            .Where(Shortcuts.IsShortcut)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        output.WriteLine($"start menus: {string.Join(" ; ", StartMenus)}");
        output.WriteLine($"shortcuts:   {links.Length}");
        if (links.Length == 0) return;

        // The very first one pays for CoCreateInstance, the shell link DLL loading, and the STA
        // thread starting. Reported on its own so the per-link figures are not carrying it.
        var first = Time(() => Shortcuts.Resolve(links[0]));
        output.WriteLine($"first resolve in the process: {first.Ms:F1} ms");

        var results = new List<(string Path, LinkTarget Link, double Ms)>(links.Length);
        foreach (var path in links)
        {
            var timed = Time(() => Shortcuts.Resolve(path));
            results.Add((path, timed.Result, timed.Ms));
        }

        var warm = links.Select(path => Time(() => Shortcuts.Resolve(path)).Ms).ToArray();
        var cold = results.Select(r => r.Ms).ToArray();

        Report("cold", cold);
        Report("warm", warm);

        var byKind = results.GroupBy(r => r.Link.Kind).OrderBy(g => g.Key.ToString());
        foreach (var group in byKind) output.WriteLine($"kind\t{group.Key}\t{group.Count()}");

        foreach (var group in results.GroupBy(r => r.Link.LeadsTo).OrderBy(g => g.Key.ToString()))
            output.WriteLine($"leads to\t{group.Key}\t{group.Count()}");

        var broken = results.Where(r => r.Link.IsBroken).ToArray();
        var refused = results.Where(r => r.Link.Refusal is not null).ToArray();

        output.WriteLine($"resolved to a target: {results.Count(r => r.Link.Target is not null)}");
        output.WriteLine($"broken (target named, and gone): {broken.Length}");
        output.WriteLine($"refused (nothing to name): {refused.Length}");

        foreach (var (path, link, _) in broken.Take(20))
            output.WriteLine($"broken\t{Path.GetFileName(path)}\t-> {link.Target}");

        foreach (var (path, link, _) in refused.Take(20))
            output.WriteLine($"refused\t{Path.GetFileName(path)}\t{link.Refusal}");
    }

    /// <summary>
    /// The same shortcut over and over. Cold can only mean "not yet touched by this process":
    /// emptying the system's metadata cache needs elevation, which this suite does not take. The
    /// gap below is the one a running file manager sees on a second visit, which is the number
    /// that matters for a listing.
    /// </summary>
    [Fact]
    public void One_shortcut_cold_and_then_warm()
    {
        // Two homes, because the first run of this showed a shortcut under %TEMP% costing five
        // times one in the Start Menu, and a number that depends on where the file lives is worth
        // reporting as two numbers rather than averaging into one that is true nowhere.
        var elsewhere = Path.Combine(AppContext.BaseDirectory, "link-bench");
        Directory.CreateDirectory(elsewhere);

        using var temp = new TempFolder();
        try
        {
            Measure("%TEMP%", temp.Root);
            Measure(elsewhere, elsewhere);
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { output.WriteLine(e.Message); }
        }

        void Measure(string label, string folder)
        {
            var target = Path.Combine(folder, "bench.txt");
            File.WriteAllText(target, "x");
            var link = ShellLinkWriter.At(Path.Combine(folder, "bench.lnk"), target);

            var cold = Time(() => Shortcuts.Resolve(link));
            var warm = Enumerable.Range(0, 200).Select(_ => Time(() => Shortcuts.Resolve(link)).Ms).ToArray();

            output.WriteLine($"{label}\tcold_ms={cold.Ms:F2}");
            Report($"{label} warm x200", warm);
        }
    }

    /// <summary>
    /// The three ways of asking about a reparse point, in the order they cost. The first is the
    /// only one cheap enough to run per row while a listing is being built.
    /// </summary>
    [Fact]
    public void What_each_reparse_question_costs()
    {
        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var junction = Path.Combine(users, "Default User");
        if (!Directory.Exists(junction)) { output.WriteLine($"no junction at {junction}"); return; }

        using var temp = new TempFolder();
        var plain = temp.AddFile("plain.txt", "x");

        Report("TagOf, a junction", Repeat(1000, () => Reparse.TagOf(junction, out _)));
        Report("TagOf, an ordinary file", Repeat(1000, () => Reparse.TagOf(plain, out _)));
        Report("Of (tag + target)", Repeat(200, () => Reparse.Of(junction)));
        Report("HardLinks.CountOf (opens a handle)", Repeat(1000, () => HardLinks.CountOf(plain)));
        Report("File.Exists, for comparison", Repeat(1000, () => File.Exists(plain)));
    }

    /// <summary>
    /// The MAX_PATH table, per volume, because 8.3 name generation is a per-volume setting and it
    /// is the thing most likely to change the answer.
    /// </summary>
    [Fact]
    public void What_a_long_path_does_to_each_shell_call()
    {
        output.WriteLine($"RtlAreLongPathsEnabled (this process) = {LongPath.EnabledForThisProcess}");

        var roots = (Environment.GetEnvironmentVariable("OAN_LINK_VOLUMES") ?? Path.GetTempPath())
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var root in roots.Where(Directory.Exists)) Sweep(root);
    }

    void Sweep(string root)
    {
        var scratch = Path.Combine(root, "oan-longpath-" + Guid.NewGuid().ToString("N"));
        var at = scratch;
        while (at.Length < 300) at = Path.Combine(at, new string('d', 40));

        try
        {
            Directory.CreateDirectory(at);
            var file = Path.Combine(at, "deep.txt");
            File.WriteAllText(file, "past the limit");

            var shortened = LongPath.Short(file);
            var has83 = shortened is not null && !string.Equals(shortened, file, StringComparison.OrdinalIgnoreCase);

            output.WriteLine($"--- {root}  len={file.Length}  8.3 names: {(has83 ? "yes" : "no")}");
            foreach (var (call, result) in Attempts(file)) output.WriteLine($"{root}\tplain\t{call}\t{result}");
            foreach (var (call, result) in Attempts(LongPath.Extended(file))) output.WriteLine($"{root}\t\\\\?\\\t{call}\t{result}");
            if (has83)
                foreach (var (call, result) in Attempts(shortened!)) output.WriteLine($"{root}\t8.3\t{call}\t{result}");

            output.WriteLine($"{root}\tShellApi.PathOf round trip\t{RoundTrip(file)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"{root}\tcould not be measured: {e.Message}");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"{root}\tscratch left behind at {scratch}: {e.Message}");
            }
        }
    }

    static string RoundTrip(string path)
    {
        try
        {
            var item = ShellApi.Item(path);
            try
            {
                var back = ShellApi.PathOf(item);
                return string.Equals(back, path, StringComparison.OrdinalIgnoreCase) ? "same path" : $"came back as {back}";
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception e) when (e is IOException or COMException)
        {
            return $"failed 0x{e.HResult:X8}";
        }
    }

    static (string Call, string Result)[] Attempts(string path) =>
    [
        ("SHGetFileInfo", FileInfoCall(path)),
        ("SHParseDisplayName", ParseCall(path)),
        ("SHCreateItemFromParsingName", ItemCall(path)),
        // SHObjectProperties is the fourth call this project makes and is deliberately absent:
        // it puts a modal property sheet on screen, and a test that opened one would sit there
        // until somebody closed it. It takes the same parse name as SHParseDisplayName.
    ];

    static string FileInfoCall(string path)
    {
        var info = default(Native.SHFILEINFO);

        return Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(),
            Native.SHGFI_SYSICONINDEX) != 0 ? "ok" : "returned 0";
    }

    static string ParseCall(string path)
    {
        var hr = Native.SHParseDisplayName(path, 0, out var pidl, 0, out _);
        if (pidl != 0) Marshal.FreeCoTaskMem(pidl);

        return hr >= 0 ? "ok" : $"0x{hr:X8}";
    }

    static string ItemCall(string path)
    {
        try
        {
            Marshal.ReleaseComObject(ShellApi.Item(path));
            return "ok";
        }
        catch (Exception e) when (e is IOException or COMException)
        {
            return $"0x{e.HResult:X8}";
        }
    }

    static double[] Repeat(int times, Action work)
    {
        work();
        return Enumerable.Range(0, times).Select(_ => Time(work).Ms).ToArray();
    }

    static (T Result, double Ms) Time<T>(Func<T> work)
    {
        var started = Stopwatch.GetTimestamp();
        var result = work();
        return (result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    static (object? Result, double Ms) Time(Action work) => Time<object?>(() => { work(); return null; });

    void Report(string label, double[] samples)
    {
        if (samples.Length == 0) return;

        var sorted = samples.Order().ToArray();
        output.WriteLine(
            $"{label}\tn={sorted.Length}\tmin={sorted[0]:F3}\tmedian={sorted[sorted.Length / 2]:F3}\t" +
            $"p95={sorted[(int)(sorted.Length * 0.95)]:F3}\tmax={sorted[^1]:F3}\ttotal={sorted.Sum():F1} ms");
    }
}
