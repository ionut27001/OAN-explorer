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
    Func<Stream>? Open);

/// <summary>Opens an archive and hands its entries over in one vocabulary. Zip goes through System.IO.Compression; 7z, rar and tar
/// through SharpCompress; a .gz, .bz2 or .xz lists the tar inside. An installed 7-Zip DLL takes 7z, rar and unknown formats.</summary>
internal static class ArchiveSource
{
    /// <summary><paramref name="use"/> must finish with the entries before it returns: compressed kinds read forward only.</summary>
    public static T Open<T>(string path, ArchiveKind kind, string? password, Func<IEnumerable<SourceEntry>, T> use)
    {
        if (SevenZip(kind, password) is { } library)
        {
            using var archive = SevenZipArchive.Open(library, path, password);
            return use(archive.Entries());
        }

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
                using var archive = ArchiveFactory.Open(path, new ReaderOptions { Password = password });
                return use(From(archive, kind));
            }
        }
    }

    public static void Extract(string path, ArchiveKind kind, string? password, ArchiveExtraction run, CancellationToken ct)
    {
        if (SevenZip(kind, password) is { } library)
        {
            using var archive = SevenZipArchive.Open(library, path, password);
            archive.Extract(run, ct);
        }
        else
        {
            Open(path, kind, password, source => { run.Pull(source, ct); return 0; });
        }
    }

    /// <summary>The framework's zip reader cannot decrypt, so a zip goes to 7-Zip only with a password.
    /// Tar and single-stream kinds stay managed: that reader walks through a .tar.gz in one step.</summary>
    private static SevenZipLibrary? SevenZip(ArchiveKind kind, string? password) => kind switch
    {
        ArchiveKind.SevenZip or ArchiveKind.Rar or ArchiveKind.Other => SevenZipLibrary.Instance,
        ArchiveKind.Zip when password is not null => SevenZipLibrary.Instance,
        _ => null,
    };

    /// <summary>Everything a parser can throw, minus the two that must travel. Wide on purpose: every byte was chosen by the archive's author.</summary>
    public static bool Recoverable(Exception e) => e is not (OperationCanceledException or OutOfMemoryException);

    private static IEnumerable<SourceEntry> From(ZipArchive zip)
    {
        foreach (var entry in zip.Entries)
        {
            // Name is empty exactly for the "folder/" entries.
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

    /// <summary>SharpCompress reports a size it was not given as zero. 7z may omit a member's size, and no format records
    /// a per-entry compressed size inside a shared block; unknown is null.</summary>
    private static long? Recorded(long value, bool mayOmit) => value > 0 ? value : mayOmit ? null : value;

    /// <summary>The high half of a zip's external attributes is the unix mode; 0xA000 is a symlink.</summary>
    private static bool IsUnixLink(int externalAttributes) => ((externalAttributes >> 16) & 0xF000) == 0xA000;
}
