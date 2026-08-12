using System;
using DiskUtility.Models;

namespace DiskUtility.Services;

/// <summary>Last-line safety checks performed immediately before any destructive write.</summary>
public static class DiskSafety
{
    /// <summary>
    /// Throws unless the target can be positively proven not to be the system disk. Re-runs the
    /// lookup at write time so a stale UI snapshot can't slip a wipe past us, and refuses outright
    /// when the system disk cannot be identified at all — an unproven target is treated as unsafe,
    /// not as safe.
    /// </summary>
    public static void EnsureNotSystemDisk(PhysicalDisk disk)
    {
        if (disk.IsSystemDisk)
            throw new InvalidOperationException(
                "Refusing to write to the system disk (the drive Windows is running from).");

        int? systemIndex = DriveEnumerator.GetSystemDiskIndex();

        if (systemIndex is null)
            throw new InvalidOperationException(
                "Refusing to write: could not determine which physical disk hosts Windows, so this "
                + "target cannot be proven safe. This can happen with Storage Spaces, dynamic disks, "
                + "or some RAID configurations.");

        if (disk.Index == systemIndex)
            throw new InvalidOperationException(
                "Refusing to write to the system disk (verified at write time).");
    }
}
