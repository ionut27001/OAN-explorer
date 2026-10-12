using System.IO;
using System.Runtime.InteropServices;

namespace Oan.Core.Interop;

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint kind, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}

/// <summary>Method order is the COM vtable order and cannot be changed.</summary>
[ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(IFileOperationProgressSink sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    void SetProgressDialog(nint dialog);
    void SetProperties(nint properties);
    void SetOwnerWindow(nint owner);
    void ApplyPropertiesToItem(IShellItem item);
    void ApplyPropertiesToItems(nint items);
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
    void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
    void MoveItems(nint items, IShellItem destination);
    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IFileOperationProgressSink? sink);
    void CopyItems(nint items, IShellItem destination);
    void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
    void DeleteItems(nint items);
    void NewItem(IShellItem destinationFolder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

/// <summary>Method order is the COM vtable order and cannot be changed.</summary>
[ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int result);
    void PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    void PreDeleteItem(uint flags, IShellItem item);
    void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
    void PreNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    void PostNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint attributes, int result, IShellItem? created);
    void UpdateProgress(uint workTotal, uint workSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}

internal static class FileOperationFlags
{
    public const uint RenameOnCollision = 0x0008;
    public const uint AllowUndo = 0x0040;
    public const uint NoConfirmMkDir = 0x0200;
    public const uint WantNukeWarning = 0x4000;
    public const uint ShowElevationPrompt = 0x0004_0000;
    public const uint RecycleOnDelete = 0x0008_0000;
    public const uint AddUndoRecord = 0x2000_0000;
}

internal static class HResult
{
    public const int Abort = unchecked((int)0x8000_4004);
    public const int Cancelled = unchecked((int)0x8007_04C7);

    /// <summary>COPYENGINE_S_USER_IGNORED: the user skipped this item.</summary>
    public const int UserIgnored = 0x0027_0005;
}

internal static class ShellApi
{
    private const uint InprocServer = 1;
    private const uint SigdnFileSysPath = 0x8005_8000;
    private const uint SigdnNormalDisplay = 0;

    private static readonly Guid FileOperationClass = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid FileOperationId = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public static IFileOperation CreateFileOperation()
    {
        var hr = CoCreateInstance(in FileOperationClass, 0, InprocServer, in FileOperationId, out IFileOperation operation);
        if (hr < 0) throw new COMException($"The shell file engine could not be started: {Describe(hr)}", hr);
        return operation;
    }

    public static IShellItem Item(string path)
    {
        var hr = SHCreateItemFromParsingName(path, 0, in ShellItemId, out IShellItem item);
        if (hr < 0) throw new IOException($"{path}: {Describe(hr)}", hr);
        return item;
    }

    public static string PathOf(IShellItem? item) =>
        item is null ? "" : Name(item, SigdnFileSysPath) ?? Name(item, SigdnNormalDisplay) ?? "";

    /// <summary>IntPtr.Zero keeps the message derived from the HRESULT alone; the one-argument overload picks up
    /// whatever IErrorInfo the thread is carrying, an unrelated reason for the wrong file.</summary>
    public static string Describe(int hr) =>
        Marshal.GetExceptionForHR(hr, IntPtr.Zero)?.Message ?? $"HRESULT 0x{hr:X8}";

    private static string? Name(IShellItem item, uint kind)
    {
        nint buffer = 0;
        try
        {
            return item.GetDisplayName(kind, out buffer) < 0 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != 0) Marshal.FreeCoTaskMem(buffer);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IFileOperation instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
