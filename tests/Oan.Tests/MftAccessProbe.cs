using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace Oan.Tests;

/// <summary>
/// What a WizTree-style index would need, measured on this machine at whatever integrity level
/// the test run happens to have: the raw volume handle, the NTFS volume data, the USN journal,
/// and the two privileges that would change the answer.
///
/// Nothing here elevates, enables a privilege, or writes anything. Every call is an open or a
/// read-only FSCTL; what is recorded is the Win32 error it came back with. A probe that fails
/// is a result, not a broken test, so nothing here asserts an outcome.
///
/// Kept out of the normal pass by Category=Benchmark, which Oan.Tests.csproj excludes:
/// dotnet test --filter "Category=Benchmark" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class MftAccessProbe(ITestOutputHelper output)
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadData = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetNtfsFileRecord = 0x00090068;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlReadFileUsnData = 0x000900EB;

    /// <summary>The four ways of asking for the volume, widest first.</summary>
    private static readonly (string Label, uint Access)[] Accesses =
    [
        ("GENERIC_READ", GenericRead),
        ("FILE_READ_DATA", FileReadData),
        ("FILE_READ_ATTRIBUTES", FileReadAttributes),
        ("0 (query only)", 0),
    ];

    private static string Letter => (Environment.GetEnvironmentVariable("OAN_PROBE_VOLUME")
        ?? Environment.SystemDirectory[..1]).ToUpperInvariant();

    private static string VolumePath => $@"\\.\{Letter}:";

    /// <summary>1. The raw volume handle itself, by requested access.</summary>
    [Fact]
    public void Volume_handle()
    {
        Header();
        foreach (var (label, access) in Accesses)
        {
            using var probe = Open(VolumePath, access);
            output.WriteLine($"open\t{VolumePath}\taccess={label}\t{probe.Result}");
        }

        // The same request through the other names for the volume and for the MFT itself, in
        // case the device namespace is the only thing being refused.
        foreach (var (path, flags, note) in Alternates())
        {
            using var probe = Open(path, GenericRead, flags);
            output.WriteLine($"open\t{path}\taccess=GENERIC_READ\t{probe.Result}\t{note}");
        }
    }

    /// <summary>
    /// 2. FSCTL_GET_NTFS_VOLUME_DATA, and the call that would read the MFT record by record,
    /// on every handle that opened at all — the device handle and the root directory handle.
    /// </summary>
    [Fact]
    public void Ntfs_volume_data()
    {
        Header();
        foreach (var (label, probe) in Handles())
        {
            using var _ = probe;
            if (probe.Handle.IsInvalid)
            {
                output.WriteLine($"ntfsdata\t{label}\tno handle: {probe.Result}");
                continue;
            }

            var data = new byte[128];
            var call = Control(probe.Handle, FsctlGetNtfsVolumeData, null, data);
            var detail = call.Ok
                ? $"\tbytesPerCluster={BitConverter.ToUInt32(data, 44)}" +
                  $"\tbytesPerRecord={BitConverter.ToUInt32(data, 48)}" +
                  $"\tmftValidDataLength={BitConverter.ToInt64(data, 56)}"
                : string.Empty;
            output.WriteLine($"ntfsdata\t{label}\t{call.Result}{detail}");

            // One raw MFT record, by file reference number: record 5 is the root directory.
            var record = Control(probe.Handle, FsctlGetNtfsFileRecord, BitConverter.GetBytes(5L), new byte[4096]);
            output.WriteLine($"mftrecord\t{label}\t{record.Result}\tbytes={record.Returned}");
        }
    }

    /// <summary>
    /// 3. FSCTL_QUERY_USN_JOURNAL, FSCTL_ENUM_USN_DATA and FSCTL_READ_USN_JOURNAL — query,
    /// the one-shot MFT enumeration, and the incremental tail an index would live on.
    /// </summary>
    [Fact]
    public void Usn_journal()
    {
        Header();
        var enumInput = new byte[24];                                    // MFT_ENUM_DATA_V0
        BitConverter.TryWriteBytes(enumInput.AsSpan(16), long.MaxValue); // HighUsn

        foreach (var (label, probe) in Handles())
        {
            using var _ = probe;
            if (probe.Handle.IsInvalid)
            {
                output.WriteLine($"usn\t{label}\tno handle: {probe.Result}");
                continue;
            }

            var journal = new byte[80];                                  // USN_JOURNAL_DATA_V1
            var query = Control(probe.Handle, FsctlQueryUsnJournal, null, journal);
            output.WriteLine($"usn.query\t{label}\t{query.Result}" + (query.Ok
                ? $"\tjournalId={BitConverter.ToUInt64(journal, 0)}" +
                  $"\tfirstUsn={BitConverter.ToInt64(journal, 8)}\tnextUsn={BitConverter.ToInt64(journal, 16)}"
                : string.Empty));

            var records = Control(probe.Handle, FsctlEnumUsnData, enumInput, new byte[65536]);
            output.WriteLine($"usn.enum\t{label}\t{records.Result}\tbytes={records.Returned}");

            if (!query.Ok) continue;
            var read = new byte[40];                                     // READ_USN_JOURNAL_DATA_V0
            BitConverter.TryWriteBytes(read.AsSpan(0), BitConverter.ToInt64(journal, 8));   // StartUsn
            BitConverter.TryWriteBytes(read.AsSpan(8), uint.MaxValue);                      // ReasonMask
            BitConverter.TryWriteBytes(read.AsSpan(32), BitConverter.ToUInt64(journal, 0)); // UsnJournalID
            var tail = Control(probe.Handle, FsctlReadUsnJournal, read, new byte[65536]);
            output.WriteLine($"usn.read\t{label}\t{tail.Result}\tbytes={tail.Returned}");
        }

        // The one USN call that needs no volume handle: one record, for one file we can open.
        using var file = Open(typeof(MftAccessProbe).Assembly.Location, FileReadAttributes);
        if (!file.Handle.IsInvalid)
        {
            var one = Control(file.Handle, FsctlReadFileUsnData, null, new byte[1024]);
            output.WriteLine($"usn.readfile\thandle=an ordinary file\t{one.Result}\tbytes={one.Returned}");
        }
    }

    /// <summary>Every handle worth asking with: the device, then the root directory.</summary>
    private static IEnumerable<(string Label, Probe Probe)> Handles()
    {
        foreach (var (label, access) in Accesses)
            yield return ($@"handle=\\.\{Letter}:\taccess={label}", Open(VolumePath, access));

        foreach (var (label, access) in Accesses)
            yield return ($@"handle={RootPath} (root dir)\taccess={label}", Open(RootPath, access, BackupSemantics));
    }

    /// <summary>4. The token: elevation, the groups in it, the privileges in it. Read only.</summary>
    [Fact]
    public void Token()
    {
        Header();
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        output.WriteLine($"token\tuser={identity.Name}\tadminRole={principal.IsInRole(WindowsBuiltInRole.Administrator)}");
        output.WriteLine($"token\televationType={Dword(identity, 18)}\t(1=default 2=full 3=limited)");
        output.WriteLine($"token\tisElevated={Dword(identity, 20)}");
        output.WriteLine($"token\tbackupOperatorRole={principal.IsInRole(WindowsBuiltInRole.BackupOperator)}");

        // Straight from TOKEN_GROUPS, not WindowsIdentity.Groups: a group UAC filtered out is
        // still in the token as deny-only, and that is the difference between "not a member"
        // and "a member whose membership this token may not use".
        foreach (var line in TokenGroups(identity)) output.WriteLine($"group\t{line}");

        foreach (var line in Privileges(identity)) output.WriteLine($"privilege\t{line}");
    }

    private static string RootPath => $@"{Letter}:\";

    private static (string Path, uint Flags, string Note)[] Alternates() =>
    [
        ($@"\\?\{Letter}:", 0u, "volume, \\?\\ form"),
        (RootPath, BackupSemantics, "root directory, backup semantics"),
        ($@"\\?\{Letter}:\$MFT", BackupSemantics, "$MFT as a file"),
        ($@"{Letter}:\$Extend\$UsnJrnl:$J", 0u, "journal data stream"),
    ];

    private void Header() => output.WriteLine(
        $"machine\tos={Environment.OSVersion.VersionString}\tvolume={VolumePath}\t" +
        $"64bit={Environment.Is64BitProcess}\tuser={Environment.UserName}");

    private static List<string> TokenGroups(WindowsIdentity identity)
    {
        const int TokenGroupsClass = 2;
        const uint DenyOnly = 0x10;
        const uint Enabled = 0x04;

        GetTokenInformation(identity.Token, TokenGroupsClass, nint.Zero, 0, out var needed);
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(identity.Token, TokenGroupsClass, buffer, needed, out _))
                return [$"could not be read: {Describe(Marshal.GetLastWin32Error())}"];

            var lines = new List<string>();
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var at = buffer + nint.Size + (i * nint.Size * 2);
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(at));
                var attributes = (uint)Marshal.ReadInt32(at + nint.Size);
                lines.Add($"{Name(sid)}\t{sid.Value}\tenabled={(attributes & Enabled) != 0}" +
                    $"\tdenyOnly={(attributes & DenyOnly) != 0}");
            }
            return lines;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Name(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return "(unmapped)";
        }
    }

    private static List<string> Privileges(WindowsIdentity identity)
    {
        const int TokenPrivileges = 3;
        GetTokenInformation(identity.Token, TokenPrivileges, null, 0, out var needed);
        var buffer = new byte[Math.Max(needed, 4)];
        if (!GetTokenInformation(identity.Token, TokenPrivileges, buffer, buffer.Length, out _))
            return [$"could not be read: {Describe(Marshal.GetLastWin32Error())}"];

        var lines = new List<string>();
        var count = BitConverter.ToUInt32(buffer, 0);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + (i * 12);
            var luid = new Luid { Low = BitConverter.ToUInt32(buffer, at), High = BitConverter.ToInt32(buffer, at + 4) };
            var attributes = BitConverter.ToUInt32(buffer, at + 8);
            var name = new char[256];
            var length = name.Length;
            var known = LookupPrivilegeName(null, ref luid, name, ref length);
            lines.Add($"{(known ? new string(name, 0, length) : $"LUID {luid.High}:{luid.Low}")}\t" +
                $"enabled={(attributes & 2) != 0}\tenabledByDefault={(attributes & 1) != 0}\tremoved={(attributes & 4) != 0}");
        }
        return lines;
    }

    private static uint Dword(WindowsIdentity identity, int informationClass)
    {
        var buffer = new byte[4];
        return GetTokenInformation(identity.Token, informationClass, buffer, buffer.Length, out _)
            ? BitConverter.ToUInt32(buffer, 0)
            : uint.MaxValue;
    }

    private static Probe Open(string path, uint access, uint flags = 0)
    {
        var handle = CreateFile(path, access, ShareReadWrite, nint.Zero, OpenExisting, flags, nint.Zero);
        return new Probe(handle, handle.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }

    private static Call Control(SafeFileHandle handle, uint code, byte[]? input, byte[]? outBuffer)
    {
        var ok = DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0),
            outBuffer, (uint)(outBuffer?.Length ?? 0), out var returned, nint.Zero);
        return new Call(ok, ok ? 0 : Marshal.GetLastWin32Error(), returned);
    }

    private static string Describe(int error) => error switch
    {
        0 => "err=0 OK",
        1 => "err=1 ERROR_INVALID_FUNCTION",
        2 => "err=2 ERROR_FILE_NOT_FOUND",
        3 => "err=3 ERROR_PATH_NOT_FOUND",
        5 => "err=5 ERROR_ACCESS_DENIED",
        6 => "err=6 ERROR_INVALID_HANDLE",
        32 => "err=32 ERROR_SHARING_VIOLATION",
        50 => "err=50 ERROR_NOT_SUPPORTED",
        87 => "err=87 ERROR_INVALID_PARAMETER",
        123 => "err=123 ERROR_INVALID_NAME",
        1179 => "err=1179 ERROR_JOURNAL_NOT_ACTIVE",
        _ => $"err={error} {new Win32Exception(error).Message}",
    };

    private sealed class Probe(SafeFileHandle handle, int error) : IDisposable
    {
        public SafeFileHandle Handle => handle;
        public string Result => handle.IsInvalid ? $"FAILED\t{Describe(error)}" : "OPENED\terr=0";
        public void Dispose() => handle.Dispose();
    }

    private readonly record struct Call(bool Ok, int Error, uint Returned)
    {
        public string Result => Ok ? "OK\terr=0" : $"FAILED\t{Describe(Error)}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[]? inBuffer, uint inSize, byte[]? outBuffer, uint outSize, out uint returned, nint overlapped);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass,
        byte[]? information, int length, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass,
        nint information, int length, out int needed);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeName(string? system, ref Luid luid, char[] name, ref int length);
}
