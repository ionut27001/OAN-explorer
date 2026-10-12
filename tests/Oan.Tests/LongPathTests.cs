using System.Runtime.InteropServices;
using Oan.App.Platform;
using Oan.Core.Interop;
using Oan.Core.Links;
using Oan.Core.Shell;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What actually happens past 260 characters, measured rather than assumed — and the measurement
/// came out the opposite way round from the usual advice. On this machine every shell call this
/// project makes takes a long PLAIN path and refuses the same path with a <c>\\?\</c> prefix, so
/// the helper's job is to take a prefix off, never to put one on.
///
/// The native assertions are deliberate. If a future Windows changes any of this, the suite
/// should go red and the helper should be rewritten around whatever the new answer is, rather
/// than carrying a workaround nobody re-measured.
///
/// The full table, and the one call that could not be measured, is in
/// docs/shortcuts-and-links.md.
/// </summary>
[Collection(DiskBound.Name)]
public class LongPathTests(ITestOutputHelper output)
{
    [Fact]
    public void A_path_at_the_limit_counts_as_long_because_the_terminator_counts_too()
    {
        Assert.False(LongPath.IsLong(new string('a', 259)));
        Assert.True(LongPath.IsLong(new string('a', 260)));
    }

    [Fact]
    public void The_extended_form_round_trips()
    {
        Assert.Equal(@"\\?\C:\one\two", LongPath.Extended(@"C:\one\two"));
        Assert.Equal(@"C:\one\two", LongPath.Plain(@"\\?\C:\one\two"));
        Assert.Equal(@"\\?\UNC\server\share\file", LongPath.Extended(@"\\server\share\file"));
        Assert.Equal(@"\\server\share\file", LongPath.Plain(@"\\?\UNC\server\share\file"));
    }

    [Fact]
    public void An_already_extended_path_is_left_alone() =>
        Assert.Equal(@"\\?\C:\a\..\b", LongPath.Extended(@"\\?\C:\a\..\b"));

    /// <summary>
    /// The prefix switches off normalisation as well as the limit, so a relative step left in
    /// place would become a folder literally named "..".
    /// </summary>
    [Fact]
    public void A_relative_step_is_resolved_before_the_prefix_goes_on() =>
        Assert.DoesNotContain("..", LongPath.Extended(@"C:\one\two\..\three"));

    [Fact]
    public void Whether_this_process_escapes_MaxPath_is_recorded_with_every_measurement() =>
        output.WriteLine($"RtlAreLongPathsEnabled (this process) = {LongPath.EnabledForThisProcess}");

    [Fact]
    public void A_short_path_is_handed_to_the_shell_unchanged()
    {
        using var temp = new TempFolder();
        var file = temp.AddFile("a.txt");

        Assert.Equal(file, LongPath.ForShell(file));
    }

    [Fact]
    public void An_empty_path_is_null_rather_than_an_empty_answer()
    {
        Assert.Null(LongPath.ForShell(""));
        Assert.Null(LongPath.Short(""));
    }

    /// <summary>
    /// The runtime writes it, and the shell reads it. Both halves matter: the first is why a long
    /// path can exist at all, the second is why nothing in this project needs a workaround for
    /// one on a machine set up like this.
    /// </summary>
    [Fact]
    public void Every_shell_call_this_project_makes_reads_a_long_plain_path()
    {
        using var deep = new DeepFolder();

        Assert.True(LongPath.IsLong(deep.Deepest), $"{deep.Deepest.Length} characters is not past MAX_PATH.");
        Assert.True(File.Exists(deep.Deepest), "The runtime could not create the file, so nothing below means anything.");

        var plain = Attempts(deep.Deepest);
        foreach (var (call, result) in plain) output.WriteLine($"{deep.Deepest.Length} chars\tplain\t{call}\t{result}");

        Assert.All(plain, attempt => Assert.Equal("ok", attempt.Result));
    }

    /// <summary>
    /// The prefix that is supposed to be the fix. It is not a parse name, and the shell's parser
    /// rejects it with E_INVALIDARG whether the path is long or not — so a helper that added one
    /// before a shell call would break every path it touched, including the short ones.
    /// </summary>
    [Fact]
    public void The_extended_prefix_is_what_breaks_the_shell_calls_not_the_length()
    {
        using var deep = new DeepFolder();
        using var temp = new TempFolder();

        var onALongPath = Attempts(LongPath.Extended(deep.Deepest));
        var onAShortPath = Attempts(LongPath.Extended(temp.AddFile("short.txt")));

        foreach (var (call, result) in onALongPath) output.WriteLine($"long\t\\\\?\\\t{call}\t{result}");
        foreach (var (call, result) in onAShortPath) output.WriteLine($"short\t\\\\?\\\t{call}\t{result}");

        Assert.All(onALongPath, attempt => Assert.NotEqual("ok", attempt.Result));
        Assert.All(onAShortPath, attempt => Assert.NotEqual("ok", attempt.Result));
    }

    /// <summary>
    /// Not merely "did not fail": the item the shell hands back has to name the same file, or the
    /// call succeeded at the wrong thing. This is the one <see cref="ShellApi"/> makes for every
    /// copy, move and delete.
    ///
    /// It comes back in 8.3 form on a volume that has 8.3 names, which matters because
    /// <c>ShellApi.PathOf</c> is what the progress sink puts in front of the user. A path they
    /// have never seen is not the same thing as a wrong one, but it is not the one they gave us
    /// either — see the wiring note in docs/shortcuts-and-links.md.
    /// </summary>
    [Fact]
    public void The_shell_item_for_a_long_path_names_the_same_file_possibly_in_8_3_form()
    {
        using var deep = new DeepFolder();

        var item = ShellApi.Item(deep.Deepest);
        try
        {
            var returned = ShellApi.PathOf(item);
            output.WriteLine($"asked:    {deep.Deepest}");
            output.WriteLine($"answered: {returned}");

            Assert.True(File.Exists(returned), "The shell answered with a path that names nothing.");

            var acceptable = new[] { deep.Deepest, LongPath.Short(deep.Deepest) };
            Assert.Contains(returned, acceptable, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// The consequence, spelled out: the path the shell answers with for a long item is never the
    /// path it was given — 8.3 on a volume that has short names, <c>\\?\</c>-prefixed on one that
    /// does not. <c>ProgressSink</c> compares exactly this string against the set of items the
    /// caller asked for, so a copy, move or delete of a long path is counted as zero succeeded
    /// even when it worked. This test asserts the ingredient, not the bug; the fix is a wiring
    /// note, because ProgressSink is not this engine's file.
    /// </summary>
    [Fact]
    public void The_path_the_shell_answers_with_does_not_match_the_one_it_was_given()
    {
        using var deep = new DeepFolder();

        var requested = new HashSet<string>(
            [Path.TrimEndingDirectorySeparator(deep.Deepest)], StringComparer.OrdinalIgnoreCase);

        var item = ShellApi.Item(deep.Deepest);
        try
        {
            var asTheShellNamesIt = Path.TrimEndingDirectorySeparator(ShellApi.PathOf(item));

            output.WriteLine($"requested: {deep.Deepest}");
            output.WriteLine($"reported:  {asTheShellNamesIt}");

            Assert.DoesNotContain(asTheShellNamesIt, requested);

            // And it does match once it comes back through FromShell, which is the whole of the
            // fix: take the prefix off, put the long names back.
            Assert.Contains(LongPath.FromShell(asTheShellNamesIt), requested);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// ForShell takes the prefix off and leaves the length alone, because that is what the
    /// measurements above say the shell wants.
    /// </summary>
    [Fact]
    public void ForShell_strips_a_prefix_and_keeps_the_length()
    {
        using var deep = new DeepFolder();

        var forShell = LongPath.ForShell(LongPath.Extended(deep.Deepest));

        Assert.Equal(deep.Deepest, forShell);
        Assert.All(Attempts(forShell!), attempt => Assert.Equal("ok", attempt.Result));
    }

    /// <summary>
    /// The fallback for a machine whose LongPathsEnabled policy is off, which this one's is not,
    /// so what is asserted here is only that the answer is honest: a name that really works, or
    /// null. 8.3 generation is off by default on volumes other than the system one since Windows
    /// 8, and a helper that returned a made-up short name would be worse than one that refused.
    /// </summary>
    [Fact]
    public void The_short_name_is_either_a_working_path_or_an_honest_null()
    {
        using var deep = new DeepFolder();
        var shortened = LongPath.Short(deep.Deepest);

        output.WriteLine($"8.3 for {deep.Deepest.Length} chars: {shortened ?? "<no 8.3 names on this volume>"}");
        if (shortened is null) return;

        Assert.True(File.Exists(shortened), "The 8.3 name does not name the same file.");
        if (LongPath.IsLong(shortened)) return;

        foreach (var (call, result) in Attempts(shortened)) output.WriteLine($"8.3\t{call}\t{result}");
        Assert.All(Attempts(shortened), attempt => Assert.Equal("ok", attempt.Result));
    }

    /// <summary>
    /// The address bar's own resolver, on a folder past MAX_PATH. <c>Paths.Resolve</c> catches
    /// PathTooLongException, which on this runtime is not thrown for length at all — so the
    /// catch is dead and the long folder resolves like any other. Asserted so that a change in
    /// either direction is caught here rather than in the address bar.
    /// </summary>
    [Fact]
    public void The_address_bar_resolves_a_folder_past_MaxPath()
    {
        using var deep = new DeepFolder();
        var folder = Path.GetDirectoryName(deep.Deepest)!;

        Assert.True(LongPath.IsLong(folder));
        Assert.Equal(folder, Paths.Resolve(folder));
        Assert.Equal(folder, Paths.Resolve($"\"{folder}\""));
    }

    // ---- the calls, each reporting "ok" or how it failed ----

    static (string Call, string Result)[] Attempts(string path) =>
    [
        ("SHGetFileInfo", FileInfoCall(path)),
        ("SHParseDisplayName", ParseCall(path)),
        ("SHCreateItemFromParsingName", ItemCall(path)),
    ];

    static string FileInfoCall(string path)
    {
        var info = default(Native.SHFILEINFO);
        var size = (uint)Marshal.SizeOf<Native.SHFILEINFO>();

        return Native.SHGetFileInfo(path, 0, ref info, size, Native.SHGFI_SYSICONINDEX) != 0 ? "ok" : "returned 0";
    }

    static string ParseCall(string path)
    {
        var hr = Native.SHParseDisplayName(path, 0, out var pidl, 0, out _);
        if (pidl != 0) Marshal.FreeCoTaskMem(pidl);

        return hr >= 0 ? "ok" : Describe(hr);
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
            return Describe(e.HResult);
        }
    }

    static string Describe(int hr) => hr switch
    {
        unchecked((int)0x8007_0002) => "0x80070002 cannot find the file",
        unchecked((int)0x8007_0003) => "0x80070003 cannot find the path",
        unchecked((int)0x8007_0057) => "0x80070057 E_INVALIDARG",
        unchecked((int)0x8007_007B) => "0x8007007B the filename or syntax is incorrect",
        unchecked((int)0x8007_00CE) => "0x800700CE the filename is too long",
        _ => $"0x{hr:X8}",
    };

    /// <summary>A real file whose full path is past MAX_PATH, built out of nested folders.</summary>
    sealed class DeepFolder : IDisposable
    {
        readonly TempFolder _temp = new();

        public DeepFolder()
        {
            var at = _temp.Root;
            while (at.Length < 300) at = Path.Combine(at, new string('d', 40));

            Directory.CreateDirectory(at);
            Deepest = Path.Combine(at, "deep.txt");
            File.WriteAllText(Deepest, "past the limit");
        }

        /// <summary>The file at the bottom.</summary>
        public string Deepest { get; }

        public void Dispose() => _temp.Dispose();
    }
}
