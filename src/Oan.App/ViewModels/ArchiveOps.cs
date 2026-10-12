using System.IO;
using System.IO.Compression;
using Oan.Core.Archives;
using Oan.Core.Shell;

namespace Oan.App.ViewModels;

/// <summary>Unpacking archives onto the disk, and packing a selection into a zip. Nothing here overwrites.</summary>
public sealed class ArchiveOps(IFileOpsHost host, ArchiveViewModel inside)
{
    public const int ExtractToFolderId = 10, ExtractHereId = 11, CompressId = 12;

    /// <summary>By name only: asked on every selection change and before every menu, so it must not touch the disk.</summary>
    public static bool IsArchive(EntryViewModel row) => !row.Entry.IsDirectory && ArchiveReader.IsArchiveName(row.Entry.FullPath);

    public IReadOnlyList<OwnMenuItem> MenuItems()
    {
        if (!host.CanWriteHere || host.Selection.Count == 0) return [];

        var items = new List<OwnMenuItem>();
        var archives = SelectedArchives();
        if (archives.Count == 1)
            items.Add(new OwnMenuItem(ExtractToFolderId, $"Extract to \"{Path.GetFileNameWithoutExtension(archives[0])}\\\""));
        else if (archives.Count > 1)
            items.Add(new OwnMenuItem(ExtractToFolderId, "Extract each to its own folder"));
        if (archives.Count > 0) items.Add(new OwnMenuItem(ExtractHereId, "Extract here"));

        items.Add(new OwnMenuItem(CompressId, "Compress to zip"));
        return items;
    }

    public Task RunAsync(int id) => id switch
    {
        ExtractToFolderId => ExtractAsync(ownFolder: true),
        ExtractHereId => ExtractAsync(ownFolder: false),
        CompressId => CompressAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Outside an archive: every selected archive, unpacked. Inside one: the selection, or all of it,
    /// to a new folder beside the archive.
    /// </summary>
    public async Task ExtractAsync(bool ownFolder)
    {
        var notes = new List<string>();
        var clean = true;

        if (inside.ArchivePath is { } open)
        {
            var folder = Free(Path.Combine(Path.GetDirectoryName(open)!, Path.GetFileNameWithoutExtension(open)));
            var names = host.Selection.Count == 0 ? null : inside.EntriesUnder(host.Selection.Select(r => inside.Inside(r.Entry.FullPath)));
            var result = await Unlocking(open, inside.Password, p => ArchiveReader.ExtractAsync(open, folder, names, password: p));
            clean = result.Ok;
            notes.Add(Describe(open, result));
        }
        else
        {
            foreach (var archive in SelectedArchives())
            {
                var folder = ownFolder
                    ? Free(Path.Combine(host.CurrentPath, Path.GetFileNameWithoutExtension(archive)))
                    : host.CurrentPath;
                var result = await Unlocking(archive, null, p => ArchiveReader.ExtractAsync(archive, folder, password: p));
                clean &= result.Ok;
                notes.Add(Describe(archive, result));
            }
            await host.ReloadAsync();
        }

        host.Notice = clean ? null : string.Join("  ", notes);
        if (clean) host.Info = string.Join("  ", notes);
    }

    public async Task CompressAsync()
    {
        var sources = host.Selection.Select(r => r.Entry.FullPath).ToArray();
        if (sources.Length == 0) return;

        var stem = sources.Length == 1 ? Path.GetFileNameWithoutExtension(sources[0]) : Path.GetFileName(host.CurrentPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(stem)) stem = "Archive";
        var zip = Free(Path.Combine(host.CurrentPath, stem + ".zip"));

        try
        {
            var skipped = await Task.Run(() => Pack(zip, sources));
            await host.ReloadAsync();
            host.Notice = skipped == 0 ? null : $"Created {Path.GetFileName(zip)} — {skipped} item(s) could not be read and are not in it.";
            if (skipped == 0) host.Info = $"Created {Path.GetFileName(zip)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(zip); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            host.Notice = "The zip could not be written: " + e.Message;
        }
    }

    /// <summary>Asks for a password for as long as one is needed and the user keeps offering.</summary>
    public async Task<ExtractResult> Unlocking(string archive, string? password, Func<string?, Task<ExtractResult>> attempt)
    {
        while (true)
        {
            var result = await attempt(password);
            if (!result.NeedsPassword) return result;

            password = host.AskPassword?.Invoke(Path.GetFileName(archive));
            if (string.IsNullOrEmpty(password)) return result;
        }
    }

    private static int Pack(string zip, IReadOnlyList<string> sources)
    {
        var skipped = 0;
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);

        void Add(string path, string name)
        {
            try { archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }

        foreach (var source in sources)
        {
            if (!Directory.Exists(source)) { Add(source, Path.GetFileName(source)); continue; }

            // Relative to the folder's parent, so the folder itself is in the zip. An empty folder gets its own entry.
            var root = Path.GetDirectoryName(source.TrimEnd('\\'))!;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

            archive.CreateEntry(Path.GetRelativePath(root, source).Replace('\\', '/') + "/");
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", options))
            {
                var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Directory.Exists(path)) archive.CreateEntry(name + "/");
                else Add(path, name);
            }
        }
        return skipped;
    }

    private static string Describe(string archive, ExtractResult result)
    {
        var name = Path.GetFileName(archive);
        if (result.Error is { } error && result.Extracted == 0) return $"{name}: {error}";

        var text = $"{name}: {result.Extracted:N0} extracted";
        if (result.Refused.Count > 0) text += $", {result.Refused.Count:N0} skipped ({result.Refused[0].Message})";
        if (result.Error is { } stopped) text += ". " + stopped;
        return text + ".";
    }

    private IReadOnlyList<string> SelectedArchives() => [.. host.Selection.Where(IsArchive).Select(r => r.Entry.FullPath)];

    /// <summary>The path itself, or the first "name (2)" that nothing has.</summary>
    private static string Free(string path)
    {
        if (!Path.Exists(path)) return path;

        var (folder, stem, extension) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }
}
