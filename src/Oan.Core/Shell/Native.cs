using System.Runtime.InteropServices;

namespace Oan.Core.Shell;

internal static class Native
{
    internal const int SW_SHOWNORMAL = 1;
    internal const int GWLP_WNDPROC = -4;
    internal const int VK_SHIFT = 0x10;

    internal const uint WM_INITMENUPOPUP = 0x0117;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_MEASUREITEM = 0x002C;

    internal const uint SHGFI_SYSICONINDEX = 0x4000;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x0010;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x0080;
    internal const uint ILD_TRANSPARENT = 0x0001;

    internal const int SHIL_LARGE = 0;
    internal const int SHIL_SMALL = 1;
    internal const int SHIL_EXTRALARGE = 2;
    internal const int SHIL_JUMBO = 4;

    internal const uint MF_BYPOSITION = 0x0400;
    internal const uint MF_SEPARATOR = 0x0800;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InsertMenuW(nint menu, uint position, uint flags, nuint id, string? text);

    internal const uint CMF_EXPLORE = 0x0004;
    internal const uint CMF_EXTENDEDVERBS = 0x0100;

    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;

    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const uint CMIC_MASK_PTINVOKE = 0x20000000;

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;

    /// <summary>What the shell reports when the user dismissed a dialog or declined elevation.</summary>
    internal const int ERROR_CANCELLED = 1223;
    internal const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    internal const uint SHOP_FILEPATH = 0x00000002;

    internal const uint OAIF_ALLOW_REGISTRATION = 0x00000001;
    internal const uint OAIF_EXEC = 0x00000004;

    internal static readonly Guid IID_IImageList = new("46eb5926-582e-4017-9fdf-e8998daa0950");
    internal static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
    internal static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
        public nint lpTitle;
        public nint lpVerbW;
        public nint lpParametersW;
        public nint lpDirectoryW;
        public nint lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OPENASINFO
    {
        public string FileName;
        public string? FileClass;
        public uint InFlags;
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bindCtx, in Guid bhid, in Guid riid, out nint ppv);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(in CMINVOKECOMMANDINFOEX invoke);
        [PreserveSig] int GetCommandString(nint idCmd, uint type, nint reserved, nint name, uint max);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(in CMINVOKECOMMANDINFOEX invoke);
        [PreserveSig] int GetCommandString(nint idCmd, uint type, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(in CMINVOKECOMMANDINFOEX invoke);
        [PreserveSig] int GetCommandString(nint idCmd, uint type, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint window, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    internal static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    internal static extern int SHGetImageList(int list, in Guid riid, out nint imageList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(string name, nint bindCtx, out nint pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    internal static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray items);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHOpenWithDialog(nint parent, in OPENASINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SHObjectProperties(nint parent, uint type, string obj, string? page);

    [DllImport("comctl32.dll")]
    internal static extern nint ImageList_GetIcon(nint imageList, int index, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    // 32-bit user32 exports no SetWindowLongPtrW, so an x86 build would throw on the first
    // right-click. The narrow call is correct there: a pointer fits in a LONG.
    internal static nint SetWindowLongPtr(nint window, int index, nint value) =>
        Environment.Is64BitProcess
            ? SetWindowLongPtr64(window, index, value)
            : SetWindowLong32(window, index, (int)value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern nint CallWindowProc(nint previous, nint window, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int key);

    // COM alone is not enough for the thread a shell menu runs on: Cut and Copy hand an
    // IDataObject to the clipboard, which is the OLE part.
    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();
}
