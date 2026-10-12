namespace Oan.Core.Archives;

/// <summary>Writes entries to disk under a destination they are not allowed to leave. Every refusal runs before a byte is written;
/// an archive that expands past its cap deletes what it had started. Fed through <see cref="Begin"/> and <see cref="End"/>,
/// so the same rules hold whether OAN pulls the bytes or 7-Zip pushes them.</summary>
internal sealed class ArchiveExtraction(string root, IReadOnlyList<string>? only, ArchiveLimits limits, long archiveBytes)
{
    // Matched by removal: what is left at the end was asked for and is not in the archive.
    private readonly HashSet<string>? _wanted = only is null ? null : new(only, StringComparer.OrdinalIgnoreCase);
    private readonly List<RefusedEntry> _refused = [];
    private readonly long _budget = Budget(archiveBytes, limits);
    private long _written;
    private int _extracted;
    private int _seen;
    private string? _stopped;
    private bool _locked;
    private (SourceEntry Entry, string Full, CappedFile File)? _current;

    public bool Stopped => _stopped is not null;
    public bool Selective => _wanted is not null;

    public bool Wants(string name) => _wanted is null || _wanted.Contains(name);
    public void Stop(string why) => _stopped ??= why;

    /// <summary>The reader was asked for a password nobody gave.</summary>
    public void StopLocked()
    {
        _locked = true;
        Stop("Stopped: this archive needs a password.");
    }

    /// <summary>For readers that hand out a stream per entry. 7-Zip drives Begin and End itself.</summary>
    public void Pull(IEnumerable<SourceEntry> source, CancellationToken ct)
    {
        foreach (var entry in Guarded(source, Stop))
        {
            ct.ThrowIfCancellationRequested();
            if (Stopped) break;
            if (Begin(entry) is not { } output) continue;

            try
            {
                using (var input = entry.Open!()) Copy(input, output, ct);
                End(null);
            }
            catch (Exception e)
            {
                // End first: a half-written file that looks finished is worse than no file.
                End(e.Message);
                if (!ArchiveSource.Recoverable(e)) throw;
            }
        }
    }

    /// <summary>Where this entry's bytes go, or null when it gets none. Every stream handed out must be given back to <see cref="End"/>.</summary>
    public Stream? Begin(SourceEntry entry)
    {
        if (Stopped) return null;
        if (++_seen > limits.MaxEntries)
        {
            Stop($"Stopped: this archive holds more than {limits.MaxEntries:N0} entries.");
            return null;
        }
        if (_wanted is not null && !_wanted.Remove(entry.Name)) return null;

        var (full, refusal) = Prepare(entry, root, _budget - _written);
        if (refusal is not null)
        {
            _refused.Add(refusal);
            return null;
        }
        if (entry.IsDirectory)
        {
            _extracted++;
            return null;
        }

        try
        {
            var file = new CappedFile(full!, _budget - _written);
            _current = (entry, full!, file);
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _refused.Add(new RefusedEntry(entry.Name, RefusalReason.Failed, e.Message));
            return null;
        }
    }

    /// <summary><paramref name="failure"/> is why the bytes did not all arrive, or null when they did.</summary>
    public void End(string? failure, RefusalReason reason = RefusalReason.Failed)
    {
        if (_current is not var (entry, full, file)) return;
        _current = null;
        file.Dispose();

        if (file.Over)
        {
            // A bomb does not get a second attempt with the next entry.
            Discard(full);
            _refused.Add(new RefusedEntry(entry.Name, RefusalReason.TooLarge, "it expands past what the cap allows"));
            Stop($"Stopped after {_written:N0} bytes: this archive expands past the cap.");
        }
        else if (failure is not null)
        {
            Discard(full);
            _refused.Add(new RefusedEntry(entry.Name, reason, failure));
        }
        else
        {
            if (entry.Modified is { } when) Touch(full, when);
            _written += file.Written;
            _extracted++;
        }
    }

    /// <summary>An entry that failed before it was ever given somewhere to go.</summary>
    public void Fail(SourceEntry entry, string why)
    {
        if (_wanted is null || _wanted.Remove(entry.Name))
            _refused.Add(new RefusedEntry(entry.Name, RefusalReason.Failed, why));
    }

    public ExtractResult Result()
    {
        if (_wanted is not null)
            _refused.AddRange(_wanted.Select(name =>
                new RefusedEntry(name, RefusalReason.NotFound, "the archive has no entry with that name")));

        return new ExtractResult
        {
            Destination = root,
            Extracted = _extracted,
            Bytes = _written,
            Refused = _refused,
            Error = _stopped,
            NeedsPassword = _locked,
        };
    }

    private static void Copy(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>A reader that gives up halfway ends the walk and says so, keeping the count of what was already written.</summary>
    private static IEnumerable<SourceEntry> Guarded(IEnumerable<SourceEntry> source, Action<string> stop)
    {
        using var walk = source.GetEnumerator();
        while (true)
        {
            SourceEntry current;
            try
            {
                if (!walk.MoveNext()) break;
                current = walk.Current;
            }
            catch (Exception e) when (ArchiveSource.Recoverable(e))
            {
                stop("Stopped: the archive could not be read past this point. " + e.Message);
                break;
            }
            yield return current;
        }
    }

    /// <summary>Everything that can refuse an entry, and the folders it needs, before any bytes.</summary>
    private static (string? Full, RefusedEntry? Refused) Prepare(SourceEntry entry, string root, long remaining)
    {
        RefusedEntry Refuse(RefusalReason reason, string why) => new(entry.Name, reason, why);

        if (entry.Encrypted)
            return (null, Refuse(RefusalReason.Encrypted, "it is encrypted, and no password was given"));
        if (entry.IsLink)
            return (null, Refuse(RefusalReason.Link, "it is a link, and a link is not a file"));

        var (full, why) = SafeEntryPath.Under(root, entry.Name);
        if (full is null) return (null, Refuse(RefusalReason.UnsafePath, why!));

        if (SafeEntryPath.Create(root, entry.IsDirectory ? full : Path.GetDirectoryName(full)!) is { } blocked)
            return (null, Refuse(RefusalReason.Link, blocked));

        if (entry.IsDirectory) return (full, null);

        if (entry.Size > remaining)
            return (null, Refuse(RefusalReason.TooLarge, $"it claims {entry.Size:N0} bytes, past what the cap has left"));

        if (File.Exists(full))
            return SafeEntryPath.IsLink(full)
                ? (null, Refuse(RefusalReason.Link, "a link of that name is already in the destination"))
                : (null, Refuse(RefusalReason.Exists, "a file of that name is already there"));

        return (full, null);
    }

    /// <summary>The ratio is against the archive's size on disk, never the sizes its header claims.
    /// The floor keeps a 200-byte zip of a megabyte of zeroes from being refused.</summary>
    private static long Budget(long archiveBytes, ArchiveLimits limits)
    {
        var ratio = Math.Max(1, limits.MaxRatio);
        var byRatio = archiveBytes >= limits.MaxTotalBytes / ratio ? limits.MaxTotalBytes : archiveBytes * ratio;
        return Math.Min(limits.MaxTotalBytes, Math.Max(limits.MinTotalBytes, byRatio));
    }

    private static void Touch(string path, DateTime when)
    {
        // The timestamp is a courtesy and does not fail the entry.
        try { File.SetLastWriteTime(path, when); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The cap is measured on bytes actually written. Going over is an IOException so every writer, ours or 7-Zip's, stops the same way.</summary>
    private sealed class CappedFile(string path, long budget)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024)
    {
        public long Written { get; private set; }
        public bool Over { get; private set; }

        // The one override: on a derived FileStream the span overload lands here too.
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Written + count > budget)
            {
                Over = true;
                throw new IOException("The archive expands past the cap.");
            }
            base.Write(buffer, offset, count);
            Written += count;
        }
    }
}
