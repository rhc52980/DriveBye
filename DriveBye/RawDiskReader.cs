using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>A byte range on the source device that could not be read and was zero-filled.</summary>
public readonly record struct BadRegion(long Offset, long Length);

/// <summary>
/// Opens a physical disk for raw sequential reading and streams it block-by-block.
/// On a read failure it retries sector-by-sector, zero-filling any unreadable sector and
/// recording it, so the consumer always receives the full device length and a single bad
/// sector never aborts the whole operation. Read-only with respect to the device.
/// </summary>
public sealed class RawDiskReader : IDisposable
{
    // 4 MiB — a multiple of both 512 and 4096, so every read stays sector-aligned.
    private const int ChunkSize = 4 * 1024 * 1024;

    private readonly SafeFileHandle _handle;

    public long Length { get; }
    public int SectorSize { get; }

    private RawDiskReader(SafeFileHandle handle, long length, int sectorSize)
    {
        _handle = handle;
        Length = length;
        SectorSize = sectorSize;
    }

    public static RawDiskReader Open(string devicePath)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            devicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not open {devicePath} (Win32 error {err}). " +
                "The app must run elevated (as Administrator).");
        }

        long length = QueryLength(handle);
        int sectorSize = QuerySectorSize(handle);
        return new RawDiskReader(handle, length, sectorSize);
    }

    /// <summary>
    /// Reads the device, invoking <paramref name="onBlock"/> with each block (buffer, count).
    /// Pass <paramref name="maxBytes"/> to stop after the leading N bytes instead of reading
    /// to the end. Returns the list of zero-filled bad regions encountered.
    /// </summary>
    public IReadOnlyList<BadRegion> ReadAll(
        Action<byte[], int> onBlock,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default,
        long? maxBytes = null)
    {
        // How much we hand to the consumer. Reads themselves stay full-chunk (and therefore
        // sector-aligned, which raw device reads require) — only the final block is shortened.
        long limit = maxBytes is null ? Length : Math.Clamp(maxBytes.Value, 0, Length);

        var badRegions = new List<BadRegion>();
        var stopwatch = Stopwatch.StartNew();
        var reportTimer = Stopwatch.StartNew();

        var buffer = new byte[ChunkSize];
        long processed = 0;
        long lastReportBytes = 0;

        while (processed < limit)
        {
            cancellation.ThrowIfCancellationRequested();

            long remaining = Length - processed;
            int toRead = (int)Math.Min(ChunkSize, remaining);

            if (NativeMethods.ReadFile(_handle, buffer, (uint)toRead, out uint bytesRead, IntPtr.Zero)
                && bytesRead > 0)
            {
                int deliver = (int)Math.Min(bytesRead, limit - processed);
                onBlock(buffer, deliver);
                processed += deliver;
            }
            else
            {
                // Slow path: re-read this chunk one sector at a time, zero-filling failures.
                ReadChunkWithRecovery(buffer, processed, toRead, badRegions);
                int deliver = (int)Math.Min(toRead, limit - processed);
                onBlock(buffer, deliver);
                processed += deliver;
            }

            if (progress != null && reportTimer.ElapsedMilliseconds >= 250)
            {
                double bps = (processed - lastReportBytes) / reportTimer.Elapsed.TotalSeconds;
                progress.Report(new DiskProgress(processed, limit, bps, stopwatch.Elapsed));
                lastReportBytes = processed;
                reportTimer.Restart();
            }
        }

        progress?.Report(new DiskProgress(
            processed, limit,
            processed / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            stopwatch.Elapsed));

        return badRegions;
    }

    private void ReadChunkWithRecovery(byte[] buffer, long chunkOffset, int chunkLength, List<BadRegion> bad)
    {
        int sector = SectorSize;
        var sectorBuf = new byte[sector];

        for (int off = 0; off < chunkLength; off += sector)
        {
            int thisLen = Math.Min(sector, chunkLength - off);
            long absOffset = chunkOffset + off;

            if (NativeMethods.SetFilePointerEx(_handle, absOffset, out _, NativeMethods.FILE_BEGIN)
                && NativeMethods.ReadFile(_handle, sectorBuf, (uint)thisLen, out uint got, IntPtr.Zero)
                && got > 0)
            {
                Array.Copy(sectorBuf, 0, buffer, off, (int)got);
                if (got < thisLen)
                {
                    Array.Clear(buffer, off + (int)got, thisLen - (int)got);
                    RecordBad(bad, absOffset + got, thisLen - got);
                }
            }
            else
            {
                Array.Clear(buffer, off, thisLen);
                RecordBad(bad, absOffset, thisLen);
            }
        }

        // Resync the sequential file pointer to the end of this chunk.
        NativeMethods.SetFilePointerEx(_handle, chunkOffset + chunkLength, out _, NativeMethods.FILE_BEGIN);
    }

    private static void RecordBad(List<BadRegion> regions, long offset, long length)
    {
        // Coalesce contiguous bad ranges into one region.
        if (regions.Count > 0)
        {
            BadRegion last = regions[^1];
            if (last.Offset + last.Length == offset)
            {
                regions[^1] = new BadRegion(last.Offset, last.Length + length);
                return;
            }
        }
        regions.Add(new BadRegion(offset, length));
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
        return 512; // conservative default
    }

    public void Dispose() => _handle.Dispose();
}
