using System.IO.Enumeration;
using Oan.Core.Model;

namespace Oan.Core.FileSystem;

public sealed class ListOptions
{
    public bool ShowHidden { get; init; }
}

/// <summary>Reads a folder into <see cref="FileEntry"/> values. <see cref="FileSystemEnumerator{T}"/> transforms the raw directory record,
/// so there is no per-entry stat call and no FileInfo allocation.</summary>
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

        // Checked before the loop too: an empty folder never enters it, and would come back as a clean listing of nothing.
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

    /// <summary>Subclassed for <see cref="ContinueOnError"/>: IgnoreInaccessible swallows unreadable entries with no callback, so a partial
    /// listing looks complete. With it off, nothing produced yet means the folder itself failed (an Error); later failures are Skips.</summary>
    private sealed class Enumerator : FileSystemEnumerator<FileEntry>
    {
        private readonly FileAttributes _skip;
        private bool _produced;

        // AttributesToSkip defaults to Hidden|System and is applied BEFORE ShouldIncludeEntry, so it must be cleared
        // or ShowHidden has no effect.
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
                Created = entry.CreationTimeUtc.LocalDateTime,
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
