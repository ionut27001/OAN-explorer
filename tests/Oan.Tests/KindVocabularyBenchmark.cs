using Oan.Core.FileSystem;
using Oan.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What the "Type" column actually says, counted over real folders on this machine.
///
/// Sorting a column is only worth offering if the column holds more than one value. <see
/// cref="Format.Kind"/> answers "File folder" for every directory regardless of attributes, so
/// this prints the vocabulary the column really has — every distinct string and how many rows
/// produce it, folders and files kept apart, with reparse points (junctions, symlinks, OneDrive
/// placeholders) counted separately so it is visible whether they say anything different.
///
/// Kept out of the normal pass by Category=Benchmark, which
/// tests/Oan.Tests/Oan.Tests.csproj excludes. Run it on purpose:
///   dotnet test --filter "Category=Benchmark" --logger "console;verbosity=detailed"
/// Point it elsewhere with OAN_KIND_BENCH_ROOTS (semicolon-separated).
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(DiskBound.Name)]
public sealed class KindVocabularyBenchmark(ITestOutputHelper output)
{
    static string[] Roots =>
        (Environment.GetEnvironmentVariable("OAN_KIND_BENCH_ROOTS")
         ?? $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};D:\dev")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The listing the user is looking at when they click the header. One folder, no recursion:
    /// this is the exact set of rows a sort has to reorder.
    /// </summary>
    [Fact]
    public void The_folder_a_user_is_standing_in()
    {
        foreach (var root in Roots) Report(root, "flat", Flat(root));
    }

    /// <summary>
    /// The same question over everything underneath, so the file side of the vocabulary is a real
    /// sample and not whatever half-dozen files happen to sit in the root.
    /// </summary>
    [Fact]
    public void Everything_underneath()
    {
        foreach (var root in Roots) Report(root, "deep", Deep(root));
    }

    void Report(string root, string scope, IEnumerable<FileEntry> entries)
    {
        if (!Directory.Exists(root))
        {
            output.WriteLine($"missing\t{root}");
            return;
        }

        var folders = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        var reparseFolders = new Dictionary<string, int>(StringComparer.Ordinal);
        var reparseFiles = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var kind = Format.Kind(entry);
            var reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

            Bump(entry.IsDirectory ? folders : files, kind);
            if (reparse) Bump(entry.IsDirectory ? reparseFolders : reparseFiles, kind);
        }

        output.WriteLine(
            $"summary\t{scope}\t{root}\tfolders={folders.Values.Sum()}\tfiles={files.Values.Sum()}\t" +
            $"folder_kinds={folders.Count}\tfile_kinds={files.Count}\t" +
            $"reparse_folders={reparseFolders.Values.Sum()}\treparse_files={reparseFiles.Values.Sum()}");

        Lines("folder", folders, reparseFolders);
        Lines("file", files, reparseFiles);

        void Lines(string what, Dictionary<string, int> all, Dictionary<string, int> reparse)
        {
            foreach (var (kind, count) in all.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
                output.WriteLine(
                    $"{what}\t{scope}\t{root}\tkind=\"{kind}\"\tcount={count}\t" +
                    $"reparse={reparse.GetValueOrDefault(kind)}");
        }
    }

    static void Bump(Dictionary<string, int> tally, string key) =>
        tally[key] = tally.GetValueOrDefault(key) + 1;

    /// <summary>Hidden and system items included: AppData is most of a user profile.</summary>
    static IEnumerable<FileEntry> Flat(string path) =>
        DirectoryLister.List(path, new ListOptions { ShowHidden = true }).Entries;

    /// <summary>
    /// The same lister, driven down the tree by hand. Reparse points are listed but never
    /// descended into — the profile's compatibility junctions (Application Data, My Documents)
    /// point back at their own ancestors, and following one never terminates.
    /// </summary>
    static IEnumerable<FileEntry> Deep(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Flat(folder))
            {
                yield return entry;

                if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(entry.FullPath);
            }
        }
    }
}
