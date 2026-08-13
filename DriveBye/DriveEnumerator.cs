using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using DiskUtility.Models;

namespace DiskUtility.Services;

/// <summary>
/// Enumerates physical disks via WMI and annotates each with its media type
/// (SSD/HDD) and whether it is the running system disk.
/// </summary>
public static class DriveEnumerator
{
    public static IReadOnlyList<PhysicalDisk> Enumerate()
    {
        int? systemDiskIndex = GetSystemDiskIndex();
        Dictionary<int, MediaKind> mediaByIndex = GetMediaTypes();

        var disks = new List<PhysicalDisk>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Index, Model, SerialNumber, Size, InterfaceType FROM Win32_DiskDrive");

        foreach (ManagementObject mo in searcher.Get())
        {
            int index = ToInt(mo["Index"]);

            disks.Add(new PhysicalDisk
            {
                DeviceId = (mo["DeviceID"] as string) ?? $@"\\.\PHYSICALDRIVE{index}",
                Index = index,
                Model = (mo["Model"] as string)?.Trim() ?? "Unknown",
                SerialNumber = (mo["SerialNumber"] as string)?.Trim(),
                SizeBytes = ToLong(mo["Size"]),
                InterfaceType = (mo["InterfaceType"] as string)?.Trim() ?? "Unknown",
                Media = mediaByIndex.TryGetValue(index, out MediaKind m) ? m : MediaKind.Unknown,
                IsSystemDisk = systemDiskIndex is int sys && index == sys,
            });
        }

        disks.Sort((a, b) => a.Index.CompareTo(b.Index));
        return disks;
    }

    /// <summary>
    /// Reads MSFT_PhysicalDisk from the Storage namespace to classify each disk as SSD/HDD/SCM.
    /// Win32_DiskDrive alone cannot reliably tell SSDs from HDDs.
    /// </summary>
    private static Dictionary<int, MediaKind> GetMediaTypes()
    {
        var result = new Dictionary<int, MediaKind>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject mo in searcher.Get())
            {
                // DeviceId here is a string equal to the physical drive index.
                if (!int.TryParse(mo["DeviceId"] as string, out int idx))
                    continue;

                result[idx] = ToUShort(mo["MediaType"]) switch
                {
                    3 => MediaKind.HDD,
                    4 => MediaKind.SSD,
                    5 => MediaKind.SCM,
                    _ => MediaKind.Unknown,
                };
            }
        }
        catch
        {
            // Storage namespace may be unavailable (e.g. older systems). Leave media Unknown.
        }
        return result;
    }

    /// <summary>
    /// Resolves the physical drive index that hosts the running Windows installation
    /// by walking LogicalDisk -> Partition -> DiskDrive associations.
    /// Returns null when the answer cannot be determined — which callers guarding a
    /// destructive write MUST treat as "unsafe", never as "not the system disk".
    /// The association walk legitimately comes back empty on Storage Spaces, dynamic
    /// disks, and some RAID configurations.
    /// </summary>
    internal static int? GetSystemDiskIndex()
    {
        try
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string? root = Path.GetPathRoot(windowsDir);   // "C:\"
            if (string.IsNullOrEmpty(root))
                return null;

            string sysDrive = root.TrimEnd('\\');          // "C:"

            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{sysDrive}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");

            foreach (ManagementObject part in partitions.Get())
            {
                string partId = (part["DeviceID"] as string) ?? string.Empty;

                using var drives = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                foreach (ManagementObject drive in drives.Get())
                    return ToInt(drive["Index"]);
            }
        }
        catch
        {
            // Fall through to null — "unknown", which callers must treat conservatively.
        }
        return null;
    }

    // ---- WMI value coercion helpers ----

    private static int ToInt(object? o) => o is null ? -1 : Convert.ToInt32(o);
    private static long ToLong(object? o) => o is null ? 0L : Convert.ToInt64(o);
    private static ushort ToUShort(object? o) => o is null ? (ushort)0 : Convert.ToUInt16(o);
}
