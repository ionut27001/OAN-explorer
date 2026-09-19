namespace Oan.Core.Archives;

/// <summary>
/// Writes entries to disk under a destination they are not allowed to leave.
///
/// The order is deliberate: everything that can refuse an entry runs before a single byte is
/// written, and the one refusal that cannot be known in advance — an archive that expands past
/// its cap — deletes what it had started before it reports.
/// </summary>
internal static class ArchiveExtraction
{
    public static ExtractResult Run(IEnumerable<SourceEntry> source, string root, IReadOnlyList<string>? only,
        ArchiveLimits limits, long archiveBytes, CancellationToken ct)
    {
        // Matching by removal: what is left at the end was asked for and is not in the archive,
        // which is a refusal of its own rather than a quiet success.
        var wanted = only is null ? null : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        var refused = new List<RefusedEntry>();
        var budget = Budget(archiveBytes, limits);
        var written = 0L;
        var extracted = 0;
        var seen = 0;
        string? stopped = null;

        foreach (var entry in Guarded(source, why => stopped = why))
        {
            ct.ThrowIfCancellationRequested();

            if (++seen > limits.MaxEntries)
            {
                stopped = $"Stopped: this archive holds more than {limits.MaxEntries:N0} entries.";
                break;
            }
            if (wanted is not null && !wanted.Remove(entry.Name)) continue;

            var (bytes, refusal, over) = One(entry, root, budget - written, ct);
            if (refusal is not null) refused.Add(refusal);
            if (over)
            {
                stopped = $"Stopped after {written:N0} bytes: this archive expands past the cap.";
                break;
            }
            if (refusal is not null) continue;

            written += bytes;
            extracted++;
        }

        if (wanted is not null)
            refused.AddRange(wanted.Select(name =>
                new RefusedEntry(name, RefusalReason.NotFound, "the archive has no entry with that name")));

        return new ExtractResult
        {
            Destination = root,
            Extracted = extracted,
            Bytes = written,
            Refused = refused,
            Error = stopped,
        };
    }

    /// <summary>
    /// A reader that gives up halfway ends the walk and says so, instead of throwing past the
    /// count of what was already written — "we wrote 40 and then the archive broke" is a
    /// different answer from "we wrote nothing".
    /// </summary>
    /// <summary>
    /// One entry: where it may go, and its bytes. <c>Over</c> means the cap was reached — the
    /// caller stops the whole run, because a bomb does not get a second attempt with the next
    /// entry.
    /// </summary>
    private static (long Bytes, RefusedEntry? Refusal, bool Over) One(
        SourceEntry entry, string root, long left, CancellationToken ct)
    {
        var (full, refusal) = Prepare(entry, root, left);
        if (refusal is not null) return (0, refusal, false);
        if (entry.IsDirectory) return (0, null, false);

        try
        {
            var (bytes, over) = Copy(entry, full!, left, ct);
            return over
                ? (0, new RefusedEntry(entry.Name, RefusalReason.TooLarge, "it expands past what the cap allows"), true)
                : (bytes, null, false);
        }
        catch (Exception e) when (ArchiveSource.Recoverable(e))
        {
            return (0, new RefusedEntry(entry.Name, RefusalReason.Failed, e.Message), false);
        }
    }

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
            return (null, Refuse(RefusalReason.Encrypted, "it is encrypted, and OAN does not ask for passwords"));
        if (entry.IsLink)
            return (null, Refuse(RefusalReason.Link, "it is a link, and a link is not a file"));

        var (full, why) = SafeEntryPath.Under(root, entry.Name);
        if (full is null) return (null, Refuse(RefusalReason.UnsafePath, why!));

        if (SafeEntryPath.Create(root, entry.IsDirectory ? full : Path.GetDirectoryName(full)!) is { } blocked)
            return (null, Refuse(RefusalReason.Link, blocked));

        if (entry.IsDirectory) return (full, null);

        if (entry.Size > remaining)
            return (null, Refuse(RefusalReason.TooLarge, $"it claims {entry.Size:N0} bytes, past what the cap has left"));

        if (File.Exists(full) && SafeEntryPath.IsLink(full))
            return (null, Refuse(RefusalReason.Link, "a link of that name is already in the destination"));

        return (full, null);
    }

    private static (long Bytes, bool OverBudget) Copy(SourceEntry entry, string full, long budget, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;
        var over = false;

        try
        {
            using var source = entry.Open();
            using var output = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                total += read;
                if (total > budget) { over = true; break; }

                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            // Before the cancellation or the failure is allowed to travel: a half-written file
            // that looks finished is worse than no file at all.
            Discard(full);
            throw;
        }

        if (over) { Discard(full); return (total, true); }

        if (entry.Modified is { } when) Touch(full, when);
        return (total, false);
    }

    /// <summary>
    /// The archive's ratio against its own size on disk, which is a fact, rather than against
    /// the sizes in its header, which are whatever it says they are. The floor keeps a 200-byte
    /// zip of a megabyte of zeroes — a real file, not an attack — from being refused.
    /// </summary>
    private static long Budget(long archiveBytes, ArchiveLimits limits)
    {
        var ratio = Math.Max(1, limits.MaxRatio);
        var byRatio = archiveBytes >= limits.MaxTotalBytes / ratio ? limits.MaxTotalBytes : archiveBytes * ratio;
        return Math.Min(limits.MaxTotalBytes, Math.Max(limits.MinTotalBytes, byRatio));
    }

    private static void Touch(string path, DateTime when)
    {
        // The bytes are the file; the timestamp is a courtesy and does not fail the entry.
        try { File.SetLastWriteTime(path, when); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
