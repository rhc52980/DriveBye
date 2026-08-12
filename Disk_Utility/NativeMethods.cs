using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>
/// Win32 P/Invoke declarations for raw physical-disk and volume access.
/// Device paths look like <c>\\.\PHYSICALDRIVE0</c>; volume paths like <c>\\?\Volume{GUID}</c>.
/// </summary>
internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_BEGIN = 0;

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // Win32 error codes the volume-locking path has to tell apart.
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MEDIA_IN_DRIVE = 1112;

    public const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;

    public const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    public const ushort ATA_FLAGS_DATA_IN = 0x02;
    public const ushort ATA_FLAGS_DATA_OUT = 0x04;

    /// <summary>
    /// ATA command codes used by the security feature set.
    /// </summary>
    public const byte ATA_IDENTIFY_DEVICE = 0xEC;
    public const byte ATA_SECURITY_SET_PASSWORD = 0xF1;
    public const byte ATA_SECURITY_UNLOCK = 0xF2;
    public const byte ATA_SECURITY_ERASE_PREPARE = 0xF3;
    public const byte ATA_SECURITY_ERASE_UNIT = 0xF4;
    public const byte ATA_SECURITY_DISABLE_PASSWORD = 0xF6;

    /// <summary>
    /// ntddscsi.h ATA_PASS_THROUGH_DIRECT. The task-file registers are spelled out as individual
    /// fields rather than two byte[8] blobs, because each slot means something different on the
    /// way in and on the way out: <see cref="Features"/> carries Features inbound and the Error
    /// register outbound, and <see cref="Command"/> carries the command inbound and the Status
    /// register outbound.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;          // seconds
        public uint ReservedAsUlong;
        public IntPtr DataBuffer;

        // PreviousTaskFile[8] — unused, but must occupy its 8 bytes.
        public byte Prev0, Prev1, Prev2, Prev3, Prev4, Prev5, Prev6, Prev7;

        // CurrentTaskFile[8] — inbound registers / outbound results.
        public byte Features;              // out: Error register
        public byte SectorCount;
        public byte LbaLow;
        public byte LbaMid;
        public byte LbaHigh;
        public byte DeviceHead;
        public byte Command;               // out: Status register
        public byte ReservedTaskFile;
    }

    // ATA Status register bits that mean the drive rejected the command.
    public const byte ATA_STATUS_ERR = 0x01;
    public const byte ATA_STATUS_DF = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    // IOCTLs returning a single 64-bit value (length info).
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        out long lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // IOCTLs returning a DISK_GEOMETRY structure.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        ref DISK_GEOMETRY lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // ATA pass-through: the same structure is both input and output buffer.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref ATA_PASS_THROUGH_DIRECT lpInBuffer, uint nInBufferSize,
        ref ATA_PASS_THROUGH_DIRECT lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // General form: raw pointer buffers (volume extents) or no buffers (lock/dismount).
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstVolumeW(StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolumeW(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(IntPtr hFindVolume);
}
