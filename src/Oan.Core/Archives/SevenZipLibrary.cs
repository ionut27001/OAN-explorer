using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oan.Core.Archives;

/// <summary>7z.dll, loaded once, and the formats it says it can open. Not shipped (7-Zip is LGPL): the user's installed copy is used,
/// and when none is found this is null and the managed readers carry on alone.</summary>
internal sealed unsafe class SevenZipLibrary
{
    /// <summary>Enough to reach an ISO's descriptor at 0x8001, the deepest signature there is.</summary>
    private const int HeadBytes = 64 * 1024;

    private static readonly Lock Gate = new();
    private static SevenZipLibrary? _loaded;
    private static bool _looked;

    private readonly delegate* unmanaged[Stdcall]<Guid*, Guid*, IntPtr*, int> _createObject;
    private readonly Format[] _formats;

    private SevenZipLibrary(IntPtr module, string path)
    {
        Location = path;
        _createObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, IntPtr*, int>)NativeLibrary.GetExport(module, "CreateObject");
        _formats = ReadFormats(module);
    }

    /// <summary>Looked for once; see <see cref="LookAgain"/>.</summary>
    public static SevenZipLibrary? Instance
    {
        get
        {
            lock (Gate)
            {
                if (!_looked) (_loaded, _looked) = (Load(), true);
                return _loaded;
            }
        }
    }

    public string Location { get; }

    public static void LookAgain()
    {
        lock (Gate) _looked = _loaded is not null;
    }

    private sealed record Format(Guid ClassId, string[] Extensions, byte[][] Signatures, int Offset);

    /// <summary>A handler for the file, or null. The bytes decide and the extension only breaks a tie.
    /// One handler is chosen and one is tried: never every parser in the DLL over a hostile file.</summary>
    public IInArchive? HandlerFor(string path)
    {
        var head = new byte[HeadBytes];
        int read;
        using (var file = File.OpenRead(path))
            read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        // backup.7z.001 is a 7z, cut into pieces: the name that counts is the one underneath.
        if (SplitVolumes.IsFirst(path)) path = Path.ChangeExtension(path, null);

        var extension = Path.GetExtension(path).TrimStart('.');
        Format? best = null;
        var bestScore = 0;

        foreach (var format in _formats)
        {
            var named = format.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            var matched = format.Signatures
                .Where(s => s.Length > 0 && format.Offset + s.Length <= read && head.AsSpan(format.Offset, s.Length).SequenceEqual(s))
                .Select(s => s.Length)
                .DefaultIfEmpty(0)
                .Max();

            var score = matched > 0 ? matched * 2 + (named ? 1 : 0) : 0;
            if (score > bestScore) (best, bestScore) = (format, score);
        }

        best ??= _formats.FirstOrDefault(f => f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        return best is null ? null : Create(best.ClassId);
    }

    private IInArchive? Create(Guid classId)
    {
        var iid = typeof(IInArchive).GUID;
        IntPtr unknown;
        if (_createObject(&classId, &iid, &unknown) != 0 || unknown == IntPtr.Zero) return null;

        try { return (IInArchive)Marshal.GetObjectForIUnknown(unknown); }
        finally { Marshal.Release(unknown); }
    }

    private static SevenZipLibrary? Load()
    {
        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                return new SevenZipLibrary(NativeLibrary.Load(path), path);
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                // Wrong architecture, or not 7-Zip at all. The next candidate, or none.
            }
        }
        return null;
    }

    /// <summary>One dropped beside the exe, then the installed 7-Zip. Absolute paths only — never the search order.</summary>
    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "7z.dll");

        string? installed = null;
        try { installed = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip", "Path", null) as string; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException) { }

        if (!string.IsNullOrEmpty(installed)) yield return Path.Combine(installed, "7z.dll");
    }

    private static Format[] ReadFormats(IntPtr module)
    {
        var count = (delegate* unmanaged[Stdcall]<uint*, int>)NativeLibrary.GetExport(module, "GetNumberOfFormats");
        var property = (delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int>)NativeLibrary.GetExport(module, "GetHandlerProperty2");

        uint total;
        if (count(&total) != 0) return [];

        var formats = new List<Format>();
        for (uint i = 0; i < total; i++)
        {
            // NArchive::NHandlerPropID: 1 class id, 2 extensions, 6 signature, 7 several
            // signatures each behind a length byte, 8 where in the file they sit.
            var classId = Read(property, i, 1, v => v.Bytes);
            if (classId.Length != 16) continue;

            var single = Read(property, i, 6, v => v.Bytes);
            var several = Read(property, i, 7, v => v.Bytes);

            formats.Add(new Format(
                new Guid(classId),
                Read(property, i, 2, v => v.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                single.Length > 0 ? [single] : Split(several),
                (int)(Read(property, i, 8, v => v.Number) ?? 0)));
        }
        return [.. formats];
    }

    private static T Read<T>(delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int> property,
        uint index, uint id, Func<PropVariant, T> take)
    {
        var value = new PropVariant();
        try { return property(index, id, &value) == 0 ? take(value) : take(default); }
        finally { value.Clear(); }
    }

    private static byte[][] Split(byte[] several)
    {
        var signatures = new List<byte[]>();
        for (var at = 0; at < several.Length && at + 1 + several[at] <= several.Length; at += 1 + several[at])
            signatures.Add(several[(at + 1)..(at + 1 + several[at])]);
        return [.. signatures];
    }
}

/// <summary>PROPVARIANT, as far as 7-Zip uses it: strings, numbers, a flag and a file time.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] private ushort _type;
    [FieldOffset(8)] private IntPtr _pointer;
    [FieldOffset(8)] private long _number;

    private const ushort Bstr = 8, Bool = 11, FileTime = 64;

    public readonly string? Text => _type == Bstr && _pointer != IntPtr.Zero ? Marshal.PtrToStringBSTR(_pointer) : null;

    /// <summary>7-Zip hands binary over in a BSTR too; its byte length sits in the four bytes before it.</summary>
    public readonly byte[] Bytes
    {
        get
        {
            if (_type != Bstr || _pointer == IntPtr.Zero) return [];
            var bytes = new byte[Marshal.ReadInt32(_pointer, -4)];
            Marshal.Copy(_pointer, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    public readonly bool Flag => _type == Bool && (short)_number != 0;

    /// <summary>Null when the archive does not say — which is not the same as zero.</summary>
    public readonly long? Number => _type switch
    {
        16 => (sbyte)_number,
        17 => (byte)_number,
        2 => (short)_number,
        18 => (ushort)_number,
        3 => (int)_number,
        19 => (uint)_number,
        20 or 21 => _number,
        _ => null,
    };

    public readonly DateTime? Time
    {
        get
        {
            if (_type != FileTime || _number <= 0) return null;
            try { return DateTime.FromFileTime(_number); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    public static PropVariant From(string text) => new() { _type = Bstr, _pointer = Marshal.StringToBSTR(text) };

    public void Clear() => PropVariantClear(ref this);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
