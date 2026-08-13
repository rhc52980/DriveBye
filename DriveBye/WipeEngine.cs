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
    Custom,     // single pass of a caller-supplied byte pattern, repeated
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
    /// runs for methods that write a known pattern, and a request that quietly did nothing must
    /// still be reported.
    /// </summary>
    public bool VerifyRequested { get; init; }

    /// <summary>True only when every byte was read back AND matched what was written.</summary>
    public bool Verified { get; init; }

    /// <summary>Offset of the first byte that did not match the pattern that was written.</summary>
    public long? FirstMismatchOffset { get; init; }

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
    /// <param name="customPattern">
    /// Bytes to repeat across the whole drive, for <see cref="WipeMethod.Custom"/>. Ignored by
    /// every other method, and required by that one.
    /// </param>
    public static WipeResult Wipe(
        PhysicalDisk disk,
        WipeMethod method,
        bool allowSsdOverwrite,
        bool verify,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default,
        byte[]? customPattern = null)
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

            if (method == WipeMethod.Custom && (customPattern is null || customPattern.Length == 0))
                throw new ArgumentException("A custom wipe needs a pattern to write.", nameof(customPattern));

            byte[] passes = method switch
            {
                WipeMethod.Zeros => new byte[] { 0x00 },
                WipeMethod.Random => new byte[] { 0xFF }, // marker; random handled below
                WipeMethod.DoD5220 => new byte[] { 0x00, 0xFF, 0xFF }, // 3rd is random
                WipeMethod.Custom => new byte[] { 0x00 }, // marker; pattern handled below
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

                    ChunkProducer producer =
                        method == WipeMethod.Custom ? MakePatternProducer(customPattern!)
                        : isRandom ? MakeRandomProducer()
                        : MakeFillProducer(passes[pass]);

                    writer.WriteAll(writer.Length, producer, progress, cancellation);
                    passesRun++;
                }
            }

            // Verification needs to know what the drive should now contain, so it only applies to
            // the methods that write something predictable. Zeros is just a one-byte pattern.
            byte[]? expected = method switch
            {
                WipeMethod.Zeros => new byte[] { 0x00 },
                WipeMethod.Custom => customPattern,
                _ => null,
            };

            bool verified = false;
            long? firstMismatch = null;
            int unverifiedRegions = 0;
            long unverifiedBytes = 0;

            if (verify && expected is not null)
            {
                (firstMismatch, unverifiedRegions, unverifiedBytes) =
                    ScanForPattern(disk.DeviceId, expected, progress, cancellation);

                // A sector we could not read back was zero-filled by the reader, so it looks
                // exactly like a successfully written one. Only claim verification when every
                // byte was actually read AND matched.
                verified = firstMismatch is null && unverifiedRegions == 0;
            }

            stopwatch.Stop();
            return new WipeResult
            {
                Completed = true,
                PassesRun = passesRun,
                BytesPerPass = bytesPerPass,
                Elapsed = stopwatch.Elapsed,
                VerifyRequested = verify,
                Verified = verified,
                FirstMismatchOffset = firstMismatch,
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
    /// Repeats a byte pattern across the drive. The offset is tracked across calls so the pattern
    /// tiles unbroken over the whole device — otherwise it would restart every 4 MiB chunk and
    /// a phrase would be chopped up at each boundary.
    /// </summary>
    private static ChunkProducer MakePatternProducer(byte[] pattern)
    {
        long position = 0;
        return (buffer, maxCount) =>
        {
            for (int i = 0; i < maxCount; i++)
                buffer[i] = pattern[(int)((position + i) % pattern.Length)];

            position += maxCount;
            return maxCount;
        };
    }

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
    /// Reads the whole device back, returning the offset of the first byte that does not match the
    /// expected repeating pattern (or null) plus the regions that could not be read at all.
    /// Unreadable regions arrive here already zero-filled by the reader, so they must be reported
    /// separately rather than counted as matches.
    /// </summary>
    private static (long? firstMismatch, int badRegionCount, long badBytes) ScanForPattern(
        string devicePath, byte[] expected, IProgress<DiskProgress>? progress,
        CancellationToken cancellation)
    {
        using var reader = RawDiskReader.Open(devicePath);
        long? firstMismatch = null;
        long position = 0;

        IReadOnlyList<BadRegion> bad = reader.ReadAll(
            (buffer, count) =>
            {
                if (firstMismatch is null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != expected[(int)((position + i) % expected.Length)])
                        {
                            firstMismatch = position + i;
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

        return (firstMismatch, bad.Count, badBytes);
    }
}
