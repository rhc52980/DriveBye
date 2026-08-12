using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>
/// Finds every volume that lives on a given physical disk, then locks and dismounts each one so
/// the disk can be written raw. The volume handles are held open for the lifetime of this object;
/// disposing it unlocks them (Windows remounts the volumes on next access).
/// </summary>
public sealed class VolumeLock : IDisposable
{
    private readonly List<SafeFileHandle> _handles;

    public int LockedVolumeCount => _handles.Count;

    private VolumeLock(List<SafeFileHandle> handles) => _handles = handles;

    /// <summary>Locks and dismounts all volumes located on the physical disk with the given index.</summary>
    public static VolumeLock ForDisk(int diskIndex)
    {
        var handles = new List<SafeFileHandle>();
        var name = new StringBuilder(260);

        IntPtr find = NativeMethods.FindFirstVolumeW(name, (uint)name.Capacity);
        if (find == NativeMethods.INVALID_HANDLE_VALUE)
            return new VolumeLock(handles);

        try
        {
            do
            {
                // FindFirstVolume yields e.g. \\?\Volume{guid}\ — CreateFile needs it without the trailing slash.
                string volumePath = name.ToString().TrimEnd('\\');

                SafeFileHandle h = NativeMethods.CreateFileW(
                    volumePath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

                if (h.IsInvalid)
                {
                    h.Dispose();
                    continue;
                }

                if (VolumeIsOnDisk(h, diskIndex))
                {
                    // Best-effort lock, then force a dismount so the filesystem releases the sectors.
                    NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_LOCK_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_DISMOUNT_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    handles.Add(h); // keep open to hold the dismount
                }
                else
                {
                    h.Dispose();
                }
            }
            while (NativeMethods.FindNextVolumeW(find, name, (uint)name.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(find);
        }

        return new VolumeLock(handles);
    }

    private static bool VolumeIsOnDisk(SafeFileHandle volume, int diskIndex)
    {
        const int bufferSize = 1024; // room for many extents
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!NativeMethods.DeviceIoControl(
                    volume, NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    IntPtr.Zero, 0, buffer, bufferSize, out _, IntPtr.Zero))
            {
                return false;
            }

            // VOLUME_DISK_EXTENTS: DWORD NumberOfDiskExtents; (4 bytes pad); DISK_EXTENT Extents[];
            // DISK_EXTENT (24 bytes): DWORD DiskNumber; (4 pad); LARGE_INTEGER Start; LARGE_INTEGER Length;
            int count = Marshal.ReadInt32(buffer, 0);
            for (int i = 0; i < count; i++)
            {
                int diskNumber = Marshal.ReadInt32(buffer, 8 + (i * 24));
                if (diskNumber == diskIndex)
                    return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle h in _handles)
        {
            NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_UNLOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            h.Dispose();
        }
        _handles.Clear();
    }
}
