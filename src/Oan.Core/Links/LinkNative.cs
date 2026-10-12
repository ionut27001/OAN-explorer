using System.Runtime.InteropServices;
using System.Text;

namespace Oan.Core.Links;

/// <summary>The native calls the links area needs.</summary>
internal static class LinkNative
{
    internal const uint CLSCTX_INPROC_SERVER = 1;
    internal const uint STGM_READ = 0;

    internal const int MAX_PATH = 260;

    /// <summary>GetPath with no flags expands environment variables; RAW keeps "%windir%\..." as written.</summary>
    internal const uint SLGP_RAWPATH = 0x0004;

    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_SHARE_ALL = 0x0007;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

    /// <summary>Without this, opening a OneDrive placeholder downloads the file.</summary>
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;

    internal static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-c000-000000000046");
    internal static readonly Guid IID_IShellLinkW = new("000214f9-0000-0000-c000-000000000046");

    /// <summary>Method order is the COM vtable order and cannot be changed. Resolve is declared and never called:
    /// it puts a search dialog on screen and walks the network.</summary>
    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out] StringBuilder file, int cch, ref WIN32_FIND_DATAW findData, uint flags);
        [PreserveSig] int GetIDList(out nint pidl);
        [PreserveSig] int SetIDList(nint pidl);
        [PreserveSig] int GetDescription([Out] StringBuilder name, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out] StringBuilder dir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([Out] StringBuilder args, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int show);
        [PreserveSig] int SetShowCmd(int show);
        [PreserveSig] int GetIconLocation([Out] StringBuilder icon, int cch, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(nint hwnd, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    /// <summary>Method order is the COM vtable order and cannot be changed.</summary>
    [ComImport, Guid("0000010b-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    /// <summary>Two DWORDs, not a long: FILETIME aligns to 4 in C, and a 64-bit field makes the marshaller pad after the attributes,
    /// so every later field reads from the wrong offset (symptom: a reparse tag that is always zero).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        /// <summary>The reparse tag, when the attributes say reparse point. Read off the directory record; opens nothing.</summary>
        public uint dwReserved0;

        public uint dwReserved1;
        public fixed char cFileName[MAX_PATH];
        public fixed char cAlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindFirstFile(string path, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathName(string path, [Out] char[]? buffer, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetLongPathName(string path, [Out] char[]? buffer, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(nint file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    /// <summary>Whether THIS process escapes MAX_PATH in the Win32 layer: the registry policy and the app manifest must both say yes.</summary>
    [DllImport("ntdll.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool RtlAreLongPathsEnabled();
}
