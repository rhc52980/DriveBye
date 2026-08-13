using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>
/// Finds every volume that lives on a given physical disk, then locks and dismounts each one so
/// the disk can be written raw. The volume handles are held open for the lifetime of this object;
/// disposing it unlocks them (Windows remounts the volumes on next access).
///
/// Fails closed. Every step that could leave a mounted filesystem on the target disk — an
/// enumeration that ends early, a volume that cannot be classified, a dismount that does not
/// take — throws instead of skipping the volume, because a skipped volume is indistinguishable
/// from one that was never there. Handles already taken are released before the throw escapes.
/// </summary>
public sealed class VolumeLock : IDisposable
{
    // VOLUME_DISK_EXTENTS: DWORD NumberOfDiskExtents; (4 bytes pad); DISK_EXTENT Extents[];
    // DISK_EXTENT (24 bytes): DWORD DiskNumber; (4 pad); LARGE_INTEGER Start; LARGE_INTEGER Length;
    private const int ExtentsHeaderSize = 8;
    private const int ExtentSize = 24;
    private const int MaxExtentsBuffer = 64 * 1024;

    private readonly List<SafeFileHandle> _handles;

    public int LockedVolumeCount => _handles.Count;

    private VolumeLock(List<SafeFileHandle> handles) => _handles = handles;

    /// <summary>Locks and dismounts all volumes located on the physical disk with the given index.</summary>
    /// <exception cref="IOException">
    /// If any volume on the disk cannot be dismounted, or any volume anywhere cannot be ruled in
    /// or out. The caller must not write to the disk when this throws.
    /// </exception>
    public static VolumeLock ForDisk(int diskIndex)
    {
        var handles = new List<SafeFileHandle>();
        var name = new StringBuilder(260);

        IntPtr find = NativeMethods.FindFirstVolumeW(name, (uint)name.Capacity);
        if (find == NativeMethods.INVALID_HANDLE_VALUE)
        {
            throw new IOException(
                $"Could not enumerate volumes (Win32 error {Marshal.GetLastWin32Error()}), so no volume "
                + $"on disk {diskIndex} can be shown to be dismounted. Refusing to continue.");
        }

        try
        {
            do
            {
                // FindFirstVolume yields e.g. \\?\Volume{guid}\ — CreateFile needs it without the trailing slash.
                string volumePath = name.ToString().TrimEnd('\\');

                if (IsOnDisk(volumePath, diskIndex))
                    handles.Add(LockAndDismount(volumePath, diskIndex));
            }
            while (NativeMethods.FindNextVolumeW(find, name, (uint)name.Capacity));

            // The loop exits on the first false from FindNextVolumeW, so this is its error.
            // Anything but "no more volumes" means we saw a partial list and cannot vouch for it.
            int lastError = Marshal.GetLastWin32Error();
            if (lastError != NativeMethods.ERROR_NO_MORE_FILES)
            {
                throw new IOException(
                    $"Volume enumeration ended early (Win32 error {lastError}); the volume list may be "
                    + $"incomplete, so disk {diskIndex} cannot be shown to be clear. Refusing to continue.");
            }
        }
        catch
        {
            // Don't leave volumes locked behind a failed setup.
            foreach (SafeFileHandle h in handles)
            {
                Unlock(h);
                h.Dispose();
            }
            throw;
        }
        finally
        {
            NativeMethods.FindVolumeClose(find);
        }

        return new VolumeLock(handles);
    }

    /// <summary>
    /// Decides whether a volume sits on the target disk, using a query-only handle so that
    /// volumes we could never open for writing (in use, BitLocker-locked) can still be ruled
    /// in or out rather than silently skipped.
    /// </summary>
    private static bool IsOnDisk(string volumePath, int diskIndex)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            volumePath,
            0,                                  // query attributes only — no read/write access needed
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (IsAbsentMedia(err))
                return false;   // an empty removable drive holds no extents on any disk

            throw new IOException(
                $"Could not open volume {volumePath} to determine which disk it is on "
                + $"(Win32 error {err}). Refusing to write to disk {diskIndex} with a volume unaccounted for.");
        }

        return ExtentsIncludeDisk(handle, volumePath, diskIndex);
    }

    private static bool ExtentsIncludeDisk(SafeFileHandle volume, string volumePath, int diskIndex)
    {
        int bufferSize = ExtentsHeaderSize + (ExtentSize * 16);

        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!NativeMethods.DeviceIoControl(
                        volume, NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();

                    // A spanned volume can hold more extents than we guessed — grow and retry.
                    bool tooSmall = err == NativeMethods.ERROR_MORE_DATA
                                 || err == NativeMethods.ERROR_INSUFFICIENT_BUFFER;
                    if (tooSmall && bufferSize < MaxExtentsBuffer)
                    {
                        bufferSize = Math.Min(bufferSize * 4, MaxExtentsBuffer);
                        continue;
                    }

                    if (IsAbsentMedia(err))
                        return false;

                    throw new IOException(
                        $"Could not determine which disk volume {volumePath} lives on (Win32 error {err}). "
                        + $"Refusing to write to disk {diskIndex} with a volume unaccounted for.");
                }

                int count = Marshal.ReadInt32(buffer, 0);
                int capacity = (bufferSize - ExtentsHeaderSize) / ExtentSize;

                // The call succeeded, so this should never happen. If it does, the buffer does not
                // hold what the count claims — stop rather than scan a truncated extent list and
                // conclude the volume is elsewhere.
                if (count > capacity)
                {
                    throw new IOException(
                        $"Volume {volumePath} reported {count} disk extents but only {capacity} were "
                        + $"returned. Refusing to write to disk {diskIndex} on an incomplete extent list.");
                }

                for (int i = 0; i < count; i++)
                {
                    if (Marshal.ReadInt32(buffer, ExtentsHeaderSize + (i * ExtentSize)) == diskIndex)
                        return true;
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Dismounts one volume known to be on the target disk, and holds it locked. The returned
    /// handle must stay open: closing it lets Windows remount the volume.
    /// </summary>
    private static SafeFileHandle LockAndDismount(string volumePath, int diskIndex)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            volumePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Volume {volumePath} is on disk {diskIndex} but could not be opened to dismount it "
                + $"(Win32 error {err}). Refusing to write underneath a mounted filesystem.");
        }

        try
        {
            // Best-effort: a lock legitimately fails while other handles are still open. The forced
            // dismount below is what actually makes the sectors safe to overwrite.
            TryLock(handle);

            if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new IOException(
                    $"Could not dismount volume {volumePath} on disk {diskIndex} "
                    + $"(Win32 error {Marshal.GetLastWin32Error()}). "
                    + "Refusing to write underneath a mounted filesystem.");
            }

            // The filesystem is gone and our handle is the only one left, so this lock should now
            // succeed. Holding it is what stops anything remounting the volume mid-write.
            if (!TryLock(handle))
            {
                throw new IOException(
                    $"Dismounted volume {volumePath} on disk {diskIndex} but could not lock it "
                    + $"(Win32 error {Marshal.GetLastWin32Error()}); it could be remounted during the "
                    + "write. Refusing to continue.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool TryLock(SafeFileHandle handle) =>
        NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_LOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private static void Unlock(SafeFileHandle handle) =>
        NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_UNLOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    /// <summary>True when the drive simply has no media, which means no extents on any disk.</summary>
    private static bool IsAbsentMedia(int err) =>
        err == NativeMethods.ERROR_NOT_READY || err == NativeMethods.ERROR_NO_MEDIA_IN_DRIVE;

    public void Dispose()
    {
        foreach (SafeFileHandle h in _handles)
        {
            Unlock(h);
            h.Dispose();
        }
        _handles.Clear();
    }
}
