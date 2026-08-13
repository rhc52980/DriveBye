using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace DiskUtility.Services;

/// <summary>Which digests to compute during a read.</summary>
[Flags]
public enum HashSelection
{
    None = 0,
    Md5 = 1,
    Sha1 = 2,
    Sha256 = 4,
    All = Md5 | Sha1 | Sha256,
}

/// <summary>A progress snapshot emitted periodically during any disk read.</summary>
public readonly record struct DiskProgress(
    long BytesProcessed,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double PercentComplete => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100.0 : 0;

    public TimeSpan Eta => BytesPerSecond > 0 && TotalBytes > BytesProcessed
        ? TimeSpan.FromSeconds((TotalBytes - BytesProcessed) / BytesPerSecond)
        : TimeSpan.Zero;
}

/// <summary>Outcome of an imaging run. Exactly one of Completed / Cancelled / Error is meaningful.</summary>
public sealed class ImagingResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public long BytesWritten { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public int BadRegionCount { get; init; }
    public long BadBytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Creates a raw (dd-style) image of a physical disk, hashing each block as it is read
/// so the source and image share a single pass. Delegates device access to RawDiskReader.
/// </summary>
public static class ImagingEngine
{
    private const int WriteBuffer = 4 * 1024 * 1024;

    public static ImagingResult CreateImage(
        string devicePath,
        string outputPath,
        HashSelection hashes = HashSelection.All,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        (IncrementalHash? md5, IncrementalHash? sha1, IncrementalHash? sha256) = HashUtil.CreateSet(hashes);

        long written = 0;

        try
        {
            using var reader = RawDiskReader.Open(devicePath);
            using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                WriteBuffer, FileOptions.SequentialScan);

            IReadOnlyList<BadRegion> bad = reader.ReadAll(
                (buffer, count) =>
                {
                    output.Write(buffer, 0, count);
                    md5?.AppendData(buffer, 0, count);
                    sha1?.AppendData(buffer, 0, count);
                    sha256?.AppendData(buffer, 0, count);
                    written += count;
                },
                progress,
                cancellation);

            output.Flush();
            stopwatch.Stop();

            long badBytes = 0;
            foreach (BadRegion r in bad) badBytes += r.Length;

            return new ImagingResult
            {
                Completed = true,
                BytesWritten = written,
                Elapsed = stopwatch.Elapsed,
                Md5 = HashUtil.ToHex(md5),
                Sha1 = HashUtil.ToHex(sha1),
                Sha256 = HashUtil.ToHex(sha256),
                BadRegionCount = bad.Count,
                BadBytes = badBytes,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ImagingResult { Cancelled = true, BytesWritten = written, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ImagingResult { Error = ex.Message, BytesWritten = written, Elapsed = stopwatch.Elapsed };
        }
        finally
        {
            md5?.Dispose();
            sha1?.Dispose();
            sha256?.Dispose();
        }
    }
}
