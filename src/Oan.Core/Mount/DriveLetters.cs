using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Oan.Core.Mount;

/// <summary>The drive letter behind <c>\\.\CdRom1</c> or <c>\\.\PhysicalDrive3</c>, which is all GetVirtualDiskPhysicalPath gives.
/// Every letter is asked which device it sits on (IOCTL_STORAGE_GET_DEVICE_NUMBER), opened with no access: no admin, no media spin-up.</summary>
internal static class DriveLetters
{
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint FileDeviceCdRom = 0x00000002;
    private const uint FileDeviceDisk = 0x00000007;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private static readonly (string Prefix, uint Type)[] Devices =
    [
        (@"\\.\CdRom", FileDeviceCdRom),
        (@"\\.\PhysicalDrive", FileDeviceDisk),
    ];

    /// <summary>"E:", or null when no letter carries that device: the volume takes a moment to appear, and may never get one.</summary>
    internal static string? For(string? physicalPath)
    {
        if (!TryParse(physicalPath, out var device)) return null;

        foreach (var root in Directory.GetLogicalDrives())
        {
            var letter = root[0];
            if (Device($@"\\.\{letter}:") == device) return $"{letter}:";
        }
        return null;
    }

    private static bool TryParse(string? physicalPath, out (uint Type, uint Number) device)
    {
        device = default;
        if (string.IsNullOrEmpty(physicalPath)) return false;

        foreach (var (prefix, type) in Devices)
        {
            // Case-insensitively: the answer measured here is \\.\CDROM0, not \\.\CdRom0.
            if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(physicalPath.AsSpan(prefix.Length), out var number)) continue;
            device = (type, number);
            return true;
        }
        return false;
    }

    private static (uint Type, uint Number)? Device(string path)
    {
        using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);
        if (handle.IsInvalid) return null;

        var number = default(StorageDeviceNumber);
        return DeviceIoControl(handle, IoctlStorageGetDeviceNumber, nint.Zero, 0,
            ref number, (uint)Marshal.SizeOf<StorageDeviceNumber>(), out _, nint.Zero)
            ? (number.DeviceType, number.DeviceNumber)
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public int PartitionNumber;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize,
        ref StorageDeviceNumber outBuffer, uint outSize, out uint returned, nint overlapped);
}
