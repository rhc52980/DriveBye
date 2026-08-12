using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>Fills a buffer for the next write. Returns the number of meaningful bytes (0 to stop).</summary>
public delegate int ChunkProducer(byte[] buffer, int maxCount);

/// <summary>
/// Opens a physical disk for raw writing. On open it dismounts every volume on the disk (via
/// <see cref="VolumeLock"/>) so writes are not blocked, and it guarantees every write is
/// sector-aligned. DESTRUCTIVE: callers must run safety checks before constructing one.
/// </summary>
public sealed class RawDiskWriter : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly VolumeLock _volumeLock;

    public long Length { get; }
    public int SectorSize { get; }
    public int DismountedVolumes => _volumeLock.LockedVolumeCount;

    private RawDiskWriter(SafeFileHandle handle, VolumeLock volumeLock, long length, int sectorSize)
    {
        _handle = handle;
        _volumeLock = volumeLock;
        Length = length;
        SectorSize = sectorSize;
    }

    public static RawDiskWriter Open(string devicePath, int diskIndex)
    {
        VolumeLock volumeLock = VolumeLock.ForDisk(diskIndex);

        SafeFileHandle handle = NativeMethods.CreateFileW(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            volumeLock.Dispose();
            throw new IOException(
                $"Could not open {devicePath} for writing (Win32 error {err}). Run elevated (as Administrator).");
        }

        long length = QueryLength(handle);
        int sectorSize = QuerySectorSize(handle);
        return new RawDiskWriter(handle, volumeLock, length, sectorSize);
    }

    /// <summary>
    /// Writes <paramref name="totalBytes"/> to the device from the start, pulling data from
    /// <paramref name="producer"/>. Every physical write is padded up to a sector boundary.
    /// </summary>
    public void WriteAll(
        long totalBytes,
        ChunkProducer producer,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        // Restart at the beginning of the device (important for multi-pass wipes).
        NativeMethods.SetFilePointerEx(_handle, 0, out _, NativeMethods.FILE_BEGIN);

        var buffer = new byte[ChunkSize];
        var stopwatch = Stopwatch.StartNew();
        var reportTimer = Stopwatch.StartNew();
        long written = 0;
        long lastReportBytes = 0;

        while (written < totalBytes)
        {
            cancellation.ThrowIfCancellationRequested();

            int want = (int)Math.Min(ChunkSize, totalBytes - written);
            int produced = producer(buffer, want);
            if (produced <= 0) break;

            int toWrite = produced;

            // Raw device writes must be a whole number of sectors — pad the tail with zeros.
            if (toWrite % SectorSize != 0)
            {
                int padded = ((toWrite / SectorSize) + 1) * SectorSize;
                if (padded > buffer.Length) padded = buffer.Length;
                Array.Clear(buffer, toWrite, padded - toWrite);
                toWrite = padded;
            }

            if (!NativeMethods.WriteFile(_handle, buffer, (uint)toWrite, out uint wrote, IntPtr.Zero) || wrote == 0)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"WriteFile failed at offset {written:N0} (Win32 error {err}).");
            }

            written += wrote;

            if (progress != null && reportTimer.ElapsedMilliseconds >= 250)
            {
                double bps = (written - lastReportBytes) / reportTimer.Elapsed.TotalSeconds;
                progress.Report(new DiskProgress(written, totalBytes, bps, stopwatch.Elapsed));
                lastReportBytes = written;
                reportTimer.Restart();
            }
        }

        NativeMethods.FlushFileBuffers(_handle);

        progress?.Report(new DiskProgress(
            written, totalBytes, written / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));
    }

    private static long QueryLength(SafeFileHandle handle)
    {
        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, out long length, (uint)sizeof(long), out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Could not query device length (Win32 error {err}).");
        }
        return length;
    }

    private static int QuerySectorSize(SafeFileHandle handle)
    {
        var geo = new NativeMethods.DISK_GEOMETRY();
        if (NativeMethods.DeviceIoControl(
                handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                IntPtr.Zero, 0, ref geo, (uint)Marshal.SizeOf<NativeMethods.DISK_GEOMETRY>(),
                out _, IntPtr.Zero)
            && geo.BytesPerSector > 0)
        {
            return (int)geo.BytesPerSector;
        }
        return 512;
    }

    public void Dispose()
    {
        _handle.Dispose();
        _volumeLock.Dispose();
    }
}
