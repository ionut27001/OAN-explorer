using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Oan.Core.Archives;

/// <summary>One entry as the format handed it over, before OAN has any opinion about it.</summary>
internal readonly record struct SourceEntry(
    string Name,
    bool IsDirectory,
    long? Size,
    long? Compressed,
    DateTime? Modified,
    bool Encrypted,
    bool IsLink,
    Func<Stream> Open);

/// <summary>
/// Opens an archive and hands its entries over in one vocabulary, whichever reader produced them.
///
/// Zip goes through System.IO.Compression: it is in the box, it seeks, and since .NET 10 it
/// reports the encryption flag. 7z, rar and tar go to SharpCompress, which has a table of
/// contents for each. A .gz, .bz2 or .xz has no table of contents — measured, its reader walks
/// straight into the tar inside and lists that, which is what those files actually are.
/// </summary>
internal static class ArchiveSource
{
    /// <summary>
    /// <paramref name="use"/> must finish with the entries before it returns: the compressed
    /// kinds are read forward only, and the stream closes behind it.
    /// </summary>
    public static T Open<T>(string path, ArchiveKind kind, Func<IEnumerable<SourceEntry>, T> use)
    {
        switch (kind)
        {
            case ArchiveKind.Zip:
            {
                using var zip = ZipFile.OpenRead(path);
                return use(From(zip));
            }
            case ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz:
            {
                using var stream = File.OpenRead(path);
                using var reader = ReaderFactory.Open(stream);
                return use(From(reader));
            }
            default:
            {
                using var archive = ArchiveFactory.Open(path);
                return use(From(archive, kind));
            }
        }
    }

    /// <summary>
    /// Everything a parser can throw at a file it did not like, minus the two that must travel.
    /// The net is wide deliberately: every byte these readers see was chosen by whoever built
    /// the archive, and a file manager that dies on a bad download is worse than one that says
    /// it cannot read it.
    /// </summary>
    public static bool Recoverable(Exception e) => e is not (OperationCanceledException or OutOfMemoryException);

    private static IEnumerable<SourceEntry> From(ZipArchive zip)
    {
        foreach (var entry in zip.Entries)
        {
            // Name is empty exactly for the "folder/" entries, which is how the BCL itself
            // tells them apart.
            var directory = entry.Name.Length == 0;

            yield return new SourceEntry(
                entry.FullName,
                directory,
                directory ? null : entry.Length,
                directory ? null : entry.CompressedLength,
                entry.LastWriteTime.LocalDateTime,
                entry.IsEncrypted,
                IsUnixLink(entry.ExternalAttributes),
                entry.Open);
        }
    }

    private static IEnumerable<SourceEntry> From(IArchive archive, ArchiveKind kind) =>
        archive.Entries.Select(entry => Read(entry, kind, entry.OpenEntryStream));

    private static IEnumerable<SourceEntry> From(IReader reader)
    {
        while (reader.MoveToNextEntry())
            yield return Read(reader.Entry, ArchiveKind.None, reader.OpenEntryStream);
    }

    private static SourceEntry Read(SharpCompress.Common.IEntry entry, ArchiveKind kind, Func<Stream> open) =>
        new(entry.Key ?? "",
            entry.IsDirectory,
            entry.IsDirectory ? null : Recorded(entry.Size, mayOmit: kind is ArchiveKind.SevenZip),
            entry.IsDirectory ? null : Recorded(entry.CompressedSize, mayOmit: true),
            entry.LastModifiedTime,
            entry.IsEncrypted,
            !string.IsNullOrEmpty(entry.LinkTarget),
            open);

    /// <summary>
    /// SharpCompress reports a size it was not given as zero, so zero means two different things
    /// and the format decides which. Every format records the size of a member, and an empty
    /// member really is zero bytes — but 7z may leave it out, and none of them record a
    /// per-entry compressed size once the entries share one block. Unknown is null.
    /// </summary>
    private static long? Recorded(long value, bool mayOmit) => value > 0 ? value : mayOmit ? null : value;

    /// <summary>The high half of a zip's external attributes is the unix mode; 0xA000 is a symlink.</summary>
    private static bool IsUnixLink(int externalAttributes) => ((externalAttributes >> 16) & 0xF000) == 0xA000;
}
