using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using DiskUtility.Models;

namespace DiskUtility.Services;

public enum WipeMethod
{
    Zeros,      // single pass of 0x00
    Random,     // single pass of random bytes
    DoD5220,    // 3 passes: 0x00, 0xFF, random
}

public sealed class WipeResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public int PassesRun { get; init; }
    public long BytesPerPass { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>
    /// True when read-back verification was asked for — whether or not it actually ran. It only
    /// runs for the zeros method, and a request that quietly did nothing must still be reported.
    /// </summary>
    public bool VerifyRequested { get; init; }

    public bool VerifiedZero { get; init; }
    public long? FirstNonZeroOffset { get; init; }

    /// <summary>
    /// Regions that could not be read back during verification. The reader zero-fills
    /// unreadable sectors, so these are indistinguishable from genuinely erased ones and
    /// prove nothing — a wipe with any of these is inconclusive, not verified.
    /// </summary>
    public int UnverifiedRegionCount { get; init; }
    public long UnverifiedBytes { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Overwrites a physical disk. Refuses the system disk unconditionally, and refuses to
/// pretend an overwrite securely erases an SSD unless the caller explicitly acknowledges it.
/// </summary>
public static class WipeEngine
{
    public static WipeResult Wipe(
        PhysicalDisk disk,
        WipeMethod method,
        bool allowSsdOverwrite,
        bool verifyZeros,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            DiskSafety.EnsureNotSystemDisk(disk);

            if (disk.Media == MediaKind.SSD && !allowSsdOverwrite)
                throw new InvalidOperationException(
                    "Target is an SSD. Overwrite passes don't reliably erase solid-state media "
                    + "(wear-levelling relocates data). Use ATA Secure Erase / NVMe Sanitize instead, "
                    + "or explicitly acknowledge the overwrite.");

            byte[] passes = method switch
            {
                WipeMethod.Zeros => new byte[] { 0x00 },
                WipeMethod.Random => new byte[] { 0xFF }, // marker; random handled below
                WipeMethod.DoD5220 => new byte[] { 0x00, 0xFF, 0xFF }, // 3rd is random
                _ => new byte[] { 0x00 },
            };

            long bytesPerPass;
            int passesRun = 0;

            using (var writer = RawDiskWriter.Open(disk.DeviceId, disk.Index))
            {
                bytesPerPass = writer.Length;

                for (int pass = 0; pass < passes.Length; pass++)
                {
                    bool isRandom =
                        (method == WipeMethod.Random) ||
                        (method == WipeMethod.DoD5220 && pass == passes.Length - 1);

                    ChunkProducer producer = isRandom
                        ? MakeRandomProducer()
                        : MakeFillProducer(passes[pass]);

                    writer.WriteAll(writer.Length, producer, progress, cancellation);
                    passesRun++;
                }
            }

            // Optional verification only makes sense when the final pass wrote zeros.
            bool verified = false;
            long? firstNonZero = null;
            int unverifiedRegions = 0;
            long unverifiedBytes = 0;

            if (verifyZeros && method == WipeMethod.Zeros)
            {
                (firstNonZero, unverifiedRegions, unverifiedBytes) =
                    ScanForNonZero(disk.DeviceId, progress, cancellation);

                // A sector we could not read back was zero-filled by the reader, so it looks
                // exactly like a successfully erased one. Only claim verification when every
                // byte was actually read AND was zero.
                verified = firstNonZero is null && unverifiedRegions == 0;
            }

            stopwatch.Stop();
            return new WipeResult
            {
                Completed = true,
                PassesRun = passesRun,
                BytesPerPass = bytesPerPass,
                Elapsed = stopwatch.Elapsed,
                VerifyRequested = verifyZeros,
                VerifiedZero = verified,
                FirstNonZeroOffset = firstNonZero,
                UnverifiedRegionCount = unverifiedRegions,
                UnverifiedBytes = unverifiedBytes,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new WipeResult { Cancelled = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new WipeResult { Error = ex.Message, Elapsed = stopwatch.Elapsed };
        }
    }

    private static ChunkProducer MakeFillProducer(byte value) =>
        (buffer, maxCount) =>
        {
            // Array.Clear zeros; for non-zero, fill explicitly.
            if (value == 0) Array.Clear(buffer, 0, maxCount);
            else for (int i = 0; i < maxCount; i++) buffer[i] = value;
            return maxCount;
        };

    /// <summary>
    /// Cryptographic RNG, not <see cref="Random"/>: a pass whose bytes are predictable from the
    /// seed undercuts the point of a random overwrite, and of the DoD label on it.
    /// </summary>
    private static ChunkProducer MakeRandomProducer() =>
        (buffer, maxCount) =>
        {
            RandomNumberGenerator.Fill(buffer.AsSpan(0, maxCount));
            return maxCount;
        };

    /// <summary>
    /// Reads the whole device back, returning the offset of the first non-zero byte (or null)
    /// plus the regions that could not be read at all. Unreadable regions arrive here already
    /// zero-filled by the reader, so they must be reported separately rather than counted as zeros.
    /// </summary>
    private static (long? firstNonZero, int badRegionCount, long badBytes) ScanForNonZero(
        string devicePath, IProgress<DiskProgress>? progress, CancellationToken cancellation)
    {
        using var reader = RawDiskReader.Open(devicePath);
        long? firstNonZero = null;
        long position = 0;

        IReadOnlyList<BadRegion> bad = reader.ReadAll(
            (buffer, count) =>
            {
                if (firstNonZero is null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != 0)
                        {
                            firstNonZero = position + i;
                            break;
                        }
                    }
                }
                position += count;
            },
            progress,
            cancellation);

        long badBytes = 0;
        foreach (BadRegion r in bad) badBytes += r.Length;

        return (firstNonZero, bad.Count, badBytes);
    }
}
