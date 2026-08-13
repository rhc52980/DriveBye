using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DiskUtility.Models;

namespace DiskUtility.Services;

public sealed class RestoreResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public long BytesWritten { get; init; }
    public bool Truncated { get; init; }      // image was larger than the target disk
    public TimeSpan Elapsed { get; init; }
    public bool VerifyRequested { get; init; }
    public bool VerifyMatched { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Writes a raw image file back onto a physical disk (the inverse of imaging). Refuses the
/// system disk. If the image is larger than the disk it writes what fits and reports truncation.
/// </summary>
public static class RestoreEngine
{
    public static RestoreResult Restore(
        PhysicalDisk disk,
        string imagePath,
        bool verify,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            DiskSafety.EnsureNotSystemDisk(disk);

            // Refuse containers outright. Nothing can prove a file IS raw, but writing one that
            // demonstrably is not produces a silently unbootable drive and reports success.
            if (ImageFormat.DetectContainer(imagePath) is string container)
                throw new InvalidOperationException(ImageFormat.Explain(imagePath, container));

            long imageLength = new FileInfo(imagePath).Length;
            long bytesToWrite;
            bool truncated;

            using (var image = new FileStream(
                imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4 * 1024 * 1024, FileOptions.SequentialScan))
            using (var writer = RawDiskWriter.Open(disk.DeviceId, disk.Index))
            {
                truncated = imageLength > writer.Length;
                bytesToWrite = Math.Min(imageLength, writer.Length);

                ChunkProducer producer = (buffer, maxCount) =>
                {
                    int total = 0;
                    // Fill the buffer from the file (FileStream.Read may return short reads).
                    while (total < maxCount)
                    {
                        int n = image.Read(buffer, total, maxCount - total);
                        if (n == 0) break;
                        total += n;
                    }
                    return total;
                };

                writer.WriteAll(bytesToWrite, producer, progress, cancellation);
            }

            bool verifyMatched = false;
            if (verify)
            {
                // Hash the image and the region of the disk we just wrote, then compare.
                HashResult imageHash = VerifyEngine.HashFile(
                    imagePath, HashSelection.Sha256, progress, cancellation);
                HashResult diskHash = VerifyEngine.HashDevice(
                    disk.DeviceId, HashSelection.Sha256, progress, cancellation, maxBytes: bytesToWrite);
                verifyMatched = VerifyEngine.Match(imageHash, diskHash);
            }

            stopwatch.Stop();
            return new RestoreResult
            {
                Completed = true,
                BytesWritten = bytesToWrite,
                Truncated = truncated,
                Elapsed = stopwatch.Elapsed,
                VerifyRequested = verify,
                VerifyMatched = verifyMatched,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new RestoreResult { Cancelled = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RestoreResult { Error = ex.Message, Elapsed = stopwatch.Elapsed };
        }
    }
}
