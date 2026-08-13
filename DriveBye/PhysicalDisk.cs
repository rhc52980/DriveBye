namespace DiskUtility.Models;

/// <summary>How the storage medium is classified. Drives write behaviour depends on this.</summary>
public enum MediaKind
{
    Unknown,
    HDD,   // Spinning disk - overwrite passes are meaningful
    SSD,   // Solid state - overwrite is unreliable; use ATA Secure Erase / NVMe sanitize
    SCM,   // Storage-class memory
}

/// <summary>
/// A physical disk as seen by Windows. Immutable snapshot produced by <c>DriveEnumerator</c>.
/// </summary>
public sealed class PhysicalDisk
{
    /// <summary>Raw device path, e.g. <c>\\.\PHYSICALDRIVE0</c>. This is what you open for raw I/O.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Physical drive number (the N in PHYSICALDRIVEN).</summary>
    public int Index { get; init; }

    public string Model { get; init; } = "Unknown";

    public string? SerialNumber { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Bus/interface type reported by WMI (IDE, SCSI, USB, etc.).</summary>
    public string InterfaceType { get; init; } = "Unknown";

    public MediaKind Media { get; init; } = MediaKind.Unknown;

    /// <summary>
    /// True when this disk holds the running Windows installation. Destructive operations
    /// MUST be blocked against it.
    /// </summary>
    public bool IsSystemDisk { get; init; }

    // ---- Display helpers (bound directly by the UI) ----

    public string SizeDisplay => FormatBytes(SizeBytes);

    public string SystemFlag => IsSystemDisk ? "\u25CF SYSTEM" : string.Empty;

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
