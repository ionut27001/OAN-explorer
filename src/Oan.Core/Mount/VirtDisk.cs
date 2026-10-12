using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Oan.Core.Mount;

/// <summary>virtdisk.dll, what Windows itself mounts an ISO with. It loads no shell extension, so a hung .iso handler cannot be reached.</summary>
internal static class VirtDisk
{
    /// <summary>VIRTDISK_ERROR_NOT_ATTACHED: what an unattached disk says when asked for its physical path, or when detached.</summary>
    internal const uint NotAttached = 0xC03A0015;

    private const uint DeviceIso = 1;
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    private const uint AccessAttachReadOnly = 0x00010000;
    private const uint AccessDetach = 0x00040000;
    private const uint AccessGetInfo = 0x00080000;

    /// <summary>Read-only, which is why there is no UAC prompt. Measured: 203 attaches on a filtered admin token, zero prompts.</summary>
    private const uint AttachReadOnly = 0x00000001;

    internal static uint Open(string path, out SafeFileHandle handle)
    {
        var type = new StorageType { DeviceId = DeviceIso, VendorId = VendorMicrosoft };
        return OpenVirtualDisk(ref type, path,
            AccessAttachReadOnly | AccessDetach | AccessGetInfo, 0, nint.Zero, out handle);
    }

    /// <summary>No PERMANENT_LIFETIME, deliberately: the attach is undone when the last handle closes, so a crashed OAN leaves nothing mounted.</summary>
    internal static uint Attach(SafeFileHandle handle) =>
        AttachVirtualDisk(handle, nint.Zero, AttachReadOnly, 0, nint.Zero, nint.Zero);

    internal static uint Detach(SafeFileHandle handle) => DetachVirtualDisk(handle, 0, 0);

    /// <summary>\\.\CdRom1 or \\.\PhysicalDrive3, or null when the disk is not attached.</summary>
    internal static string? PhysicalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var bytes = (uint)(buffer.Capacity * sizeof(char));
        return GetVirtualDiskPhysicalPath(handle, ref bytes, buffer) == 0 ? buffer.ToString() : null;
    }

    /// <summary>Win32Exception renders the 0xC03A… virtdisk codes as "Unknown error", so those are shown as the number.</summary>
    internal static string Explain(uint code, string what) =>
        $"{what} ({(code < 0x10000 ? new Win32Exception((int)code).Message : $"0x{code:X8}")})";

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [DllImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref StorageType type, string path, uint accessMask,
        uint flags, nint parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle handle, nint securityDescriptor,
        uint flags, uint providerFlags, nint parameters, nint overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerFlags);

    [DllImport("virtdisk.dll", EntryPoint = "GetVirtualDiskPhysicalPath", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint bytes, StringBuilder path);
}
