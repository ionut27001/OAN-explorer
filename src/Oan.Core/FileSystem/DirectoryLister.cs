using System.IO.Enumeration;
using Oan.Core.Model;

namespace Oan.Core.FileSystem;

public sealed class ListOptions
{
    public bool ShowHidden { get; init; }
}

/// <summary>
/// Reads a folder into <see cref="FileEntry"/> values.
///
/// Uses <see cref="FileSystemEnumerator{T}"/> rather than DirectoryInfo.GetFileSystemInfos:
/// the transform runs against the raw directory record, so name, attributes, size and
/// timestamps all come from the single enumeration pass — no per-entry stat call, no FileInfo
/// allocation.
/// </summary>
public static class DirectoryLister
{
    public static DirectoryListing List(string path, ListOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ListOptions();

        if (string.IsNullOrWhiteSpace(path))
            return DirectoryListing.Failed(path, "No folder was given.");
        if (!Directory.Exists(path))
            return DirectoryListing.Failed(path, File.Exists(path)
                ? "That is a file, not a folder."
                : "This folder no longer exists.");

        // Before the loop, not only inside it: an empty folder never enters the loop, so a
        // request that was cancelled before it started would otherwise come back as a clean
        // listing of nothing.
        ct.ThrowIfCancellationRequested();

        var entries = new List<FileEntry>(256);

        try
        {
            using var enumerator = new Enumerator(path, options.ShowHidden);
            while (enumerator.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(enumerator.Current);
            }
            return new DirectoryListing { Path = path, Entries = entries, Skipped = enumerator.Skipped };
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            return DirectoryListing.Failed(path, "Access to this folder was denied.");
        }
        catch (IOException ex)
        {
            return DirectoryListing.Failed(path, ex.Message);
        }
    }

    public static Task<DirectoryListing> ListAsync(string path, ListOptions? options = null, CancellationToken ct = default) =>
        Task.Run(() => List(path, options, ct), ct);

    /// <summary>
    /// Subclassed for one reason: <see cref="ContinueOnError"/>.
    ///
    /// With EnumerationOptions.IgnoreInaccessible the runtime swallows unreadable entries and
    /// offers no callback, so a folder with denied items enumerates as if those items were not
    /// there — a partial listing indistinguishable from a complete one.
    ///
    /// Turning it off gives us the callback, but then the two failures arrive down the same
    /// path and must be told apart: failing to open the folder at all is an Error, failing on
    /// one entry inside it is a Skip. Nothing has been produced yet when the first is what
    /// happened, so that is the test — and returning false there lets the exception through to
    /// be reported as a refusal instead of as an empty folder.
    /// </summary>
    private sealed class Enumerator : FileSystemEnumerator<FileEntry>
    {
        private readonly FileAttributes _skip;
        private bool _produced;

        // AttributesToSkip must be cleared: it defaults to Hidden|System and is applied by the
        // base enumerator BEFORE ShouldIncludeEntry runs, so leaving it made ShowHidden=true a
        // setting that was read and had no effect. All filtering happens in one place below.
        public Enumerator(string path, bool showHidden)
            : base(path, new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
            })
            => _skip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System;

        public int Skipped { get; private set; }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) =>
            _skip == 0 || (entry.Attributes & _skip) == 0;

        protected override FileEntry TransformEntry(ref FileSystemEntry entry)
        {
            _produced = true;
            return new FileEntry
            {
                Name = entry.FileName.ToString(),
                FullPath = entry.ToFullPath(),
                Attributes = entry.Attributes,
                Size = entry.IsDirectory ? null : entry.Length,
                Modified = entry.LastWriteTimeUtc.LocalDateTime,
            };
        }

        protected override bool ContinueOnError(int error)
        {
            if (!_produced) return false;
            Skipped++;
            return true;
        }
    }
}
