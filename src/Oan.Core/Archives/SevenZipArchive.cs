using System.Runtime.InteropServices;

namespace Oan.Core.Archives;

/// <summary>Thrown by the readers when an archive will not open, or list, without a password.</summary>
internal sealed class PasswordNeededException(bool wrong)
    : IOException(wrong ? "That password does not open this archive." : "This archive is encrypted and needs a password.");

/// <summary>One archive opened through 7z.dll. 7-Zip pushes: it calls back with each wanted entry's bytes in file order, which is
/// the only way a solid archive is read once, so extraction hands the callbacks to <see cref="ArchiveExtraction"/>.</summary>
internal sealed class SevenZipArchive : IDisposable
{
    private const int Ok = 0, NotThisFormat = 1, Abort = unchecked((int)0x80004004);

    private readonly IInArchive _archive;
    private readonly OpenCallback _opened;

    private SevenZipArchive(IInArchive archive, OpenCallback opened) => (_archive, _opened) = (archive, opened);

    public static SevenZipArchive Open(SevenZipLibrary library, string path, string? password)
    {
        var archive = library.HandlerFor(path) ?? throw new IOException("7-Zip has no reader for this file.");
        var callback = new OpenCallback(path, password);
        try
        {
            ulong scan = 1 << 23;
            var result = archive.Open(new InStream(callback.First), ref scan, callback);
            if (result == Ok) return new SevenZipArchive(archive, callback);

            if (callback.PasswordAsked) throw new PasswordNeededException(wrong: password is not null);
            throw new IOException(result == NotThisFormat ? "It is damaged, or not the format it claims to be." : $"7-Zip error 0x{result:X8}.");
        }
        catch
        {
            Marshal.FinalReleaseComObject(archive);
            callback.Dispose();
            throw;
        }
    }

    public List<SourceEntry> Entries()
    {
        _archive.GetNumberOfItems(out var count);
        var entries = new List<SourceEntry>((int)count);
        for (uint i = 0; i < count; i++) entries.Add(Entry(i));
        return entries;
    }

    public void Extract(ArchiveExtraction run, CancellationToken ct)
    {
        var entries = Entries();
        var callback = new ExtractCallback(entries, run, _opened.Password, ct);

        // Null means everything; otherwise the indices, which 7-Zip wants ascending.
        uint[]? wanted = run.Selective ? [.. Enumerable.Range(0, entries.Count).Where(i => run.Wants(entries[i].Name)).Select(i => (uint)i)] : null;
        if (wanted is { Length: 0 }) return;

        var result = _archive.Extract(wanted, wanted is null ? uint.MaxValue : (uint)wanted.Length, 0, callback);
        callback.Finish();

        ct.ThrowIfCancellationRequested();
        if (callback.Locked) run.StopLocked();
        if (result != Ok && !run.Stopped) run.Stop($"Stopped: 7-Zip could not read past this point (0x{result:X8}).");
    }

    public void Dispose()
    {
        _archive.Close();
        Marshal.FinalReleaseComObject(_archive);
        _opened.Dispose();
    }

    private SourceEntry Entry(uint index)
    {
        // PropID.h: 3 path, 6 is-folder, 7 size, 8 packed size, 9 attributes, 12 modified,
        // 15 encrypted, 54 symlink target.
        var path = (Text(index, 3) ?? "").Replace('\\', '/');

        // A bare .gz or .zst holds one nameless stream; 7-Zip names it after the archive.
        if (path.Length == 0) path = Path.GetFileNameWithoutExtension(_opened.Name);

        var folder = Read(index, 6).Flag;
        var reparse = ((Read(index, 9).Number ?? 0) & (long)FileAttributes.ReparsePoint) != 0;

        return new SourceEntry(
            path,
            folder,
            folder ? null : Read(index, 7).Number,
            folder ? null : Read(index, 8).Number,
            Read(index, 12).Time,
            Read(index, 15).Flag,
            reparse || !string.IsNullOrEmpty(Text(index, 54)),
            Open: null);
    }

    private string? Text(uint index, uint id)
    {
        var value = new PropVariant();
        try
        {
            _archive.GetProperty(index, id, ref value);
            return value.Text;
        }
        finally { value.Clear(); }
    }

    /// <summary>Only for the kinds that own no memory, so there is nothing to clear.</summary>
    private PropVariant Read(uint index, uint id)
    {
        var value = new PropVariant();
        _archive.GetProperty(index, id, ref value);
        return value;
    }

    private static string Describe(int result) => result switch
    {
        1 => "it uses a compression method this 7-Zip does not have",
        2 => "its data is damaged",
        3 => "its checksum does not match — the data is damaged",
        5 => "the archive ends before it does",
        9 => "the password is wrong",
        _ => $"7-Zip could not unpack it (result {result})",
    };

    /// <summary>Answers 7-Zip while it opens: the password, and the other volumes of a split archive, looked for only beside the first.</summary>
    private sealed class OpenCallback(string path, string? password)
        : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword, IDisposable
    {
        private readonly List<Stream> _streams = [];

        public string Name { get; } = Path.GetFileName(path);
        public string? Password => password;
        public bool PasswordAsked { get; private set; }
        public Stream First => Track(SplitVolumes.IsFirst(path) ? new SplitVolumes(path) : File.OpenRead(path));

        public int SetTotal(IntPtr files, IntPtr bytes) => Ok;
        public int SetCompleted(IntPtr files, IntPtr bytes) => Ok;

        public int GetProperty(uint id, ref PropVariant value)
        {
            if (id == 4) value = PropVariant.From(Name);
            return Ok;
        }

        public int GetStream(string name, out IInStream? stream)
        {
            stream = null;
            var volume = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(name));
            if (!File.Exists(volume)) return NotThisFormat;

            try { stream = new InStream(Track(File.OpenRead(volume))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return NotThisFormat; }
            return Ok;
        }

        public int CryptoGetTextPassword(out string? text)
        {
            PasswordAsked = true;
            text = password;
            return password is null ? Abort : Ok;
        }

        public void Dispose() => _streams.ForEach(s => s.Dispose());

        private Stream Track(Stream stream)
        {
            _streams.Add(stream);
            return stream;
        }
    }

    private sealed class ExtractCallback(List<SourceEntry> entries, ArchiveExtraction run, string? password, CancellationToken ct)
        : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private bool _open;
        private uint _index;

        /// <summary>7-Zip asked for a password there is none of; not every format marks entries as encrypted up front.</summary>
        public bool Locked { get; private set; }

        public int SetTotal(ulong total) => Ok;
        public int SetCompleted(IntPtr completed) => ct.IsCancellationRequested || run.Stopped ? Abort : Ok;

        public int GetStream(uint index, out ISequentialOutStream? stream, int mode)
        {
            stream = null;
            if (ct.IsCancellationRequested || run.Stopped) return Abort;
            _index = index;

            // Not "extract": 7-Zip already knows the data is bad (a wrong password looks like this); SetOperationResult carries the news.
            if (mode != 0) return Ok;

            var entry = entries[(int)index];
            if (run.Begin(entry with { Encrypted = entry.Encrypted && password is null }) is not { } file) return Ok;

            _open = true;
            stream = new OutStream(file, ct);
            return Ok;
        }

        public int PrepareOperation(int mode) => Ok;

        public int SetOperationResult(int result)
        {
            if (_open) run.End(result == Ok ? null : Describe(result));
            else if (result != Ok) run.Fail(entries[(int)_index], Describe(result));
            _open = false;
            return Ok;
        }

        /// <summary>An aborted run never reports its last entry; the half-written file still has to go.</summary>
        public void Finish()
        {
            if (_open && Locked) run.End("it is encrypted, and no password was given", RefusalReason.Encrypted);
            else if (_open) run.End("the extraction was stopped");
            _open = false;
        }

        public int CryptoGetTextPassword(out string? text)
        {
            text = password;
            Locked = password is null;
            return Locked ? Abort : Ok;
        }
    }

    private sealed class InStream(Stream stream) : IInStream
    {
        public int Read(byte[] data, uint size, IntPtr processed)
        {
            var read = stream.Read(data, 0, (int)size);
            if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, read);
            return Ok;
        }

        public int Seek(long offset, uint origin, IntPtr position)
        {
            var at = stream.Seek(offset, (SeekOrigin)origin);
            if (position != IntPtr.Zero) Marshal.WriteInt64(position, at);
            return Ok;
        }
    }

    private sealed class OutStream(Stream file, CancellationToken ct) : ISequentialOutStream
    {
        public int Write(byte[] data, uint size, IntPtr processed)
        {
            if (ct.IsCancellationRequested) return Abort;
            try { file.Write(data, 0, (int)size); }
            catch (IOException) { return Abort; }

            if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, (int)size);
            return Ok;
        }
    }
}

// 7-Zip's interfaces are COM in shape only: IUnknown, its own IIDs, no registration. The
// method order is the vtable, so nothing here may be reordered.

[ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig] int Read([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size, IntPtr processed);
    [PreserveSig] int Seek(long offset, uint origin, IntPtr position);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig] int Write([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size, IntPtr processed);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600300000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenVolumeCallback
{
    [PreserveSig] int GetProperty(uint id, ref PropVariant value);
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Interface)] out IInStream? stream);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string? text);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completed);
    [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? stream, int mode);
    [PreserveSig] int PrepareOperation(int mode);
    [PreserveSig] int SetOperationResult(int result);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, ref ulong scan, [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback callback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint count);
    [PreserveSig] int GetProperty(uint index, uint id, ref PropVariant value);
    [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray)] uint[]? indices, uint count, int test, [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback callback);
}
