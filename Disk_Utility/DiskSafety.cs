using System;
using DiskUtility.Models;

namespace DiskUtility.Services;

/// <summary>Last-line safety checks performed immediately before any destructive write.</summary>
public static class DiskSafety
{
    /// <summary>
    /// Throws if the target is the system disk. Checks both the flag captured at enumeration
    /// AND a fresh independent lookup, so a stale UI state can't slip a wipe past us.
    /// </summary>
    public static void EnsureNotSystemDisk(PhysicalDisk disk)
    {
        if (disk.IsSystemDisk)
            throw new InvalidOperationException(
                "Refusing to write to the system disk (the drive Windows is running from).");

        int systemIndex = DriveEnumerator.GetSystemDiskIndex();
        if (systemIndex >= 0 && disk.Index == systemIndex)
            throw new InvalidOperationException(
                "Refusing to write to the system disk (verified at write time).");
    }
}
