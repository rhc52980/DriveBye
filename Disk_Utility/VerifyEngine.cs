using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace DiskUtility.Services;

/// <summary>Result of hashing a single source (a file or a device).</summary>
public sealed class HashResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public long BytesHashed { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public int BadRegionCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Hashing and verification. Hashing a device reuses the same bad-sector-tolerant
/// reader the imaging engine uses, so a device hash matches its image hash byte-for-byte
/// (including any zero-filled bad regions).
/// </summary>
public static class VerifyEngine
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>Computes the requested digests over a file (e.g. an existing .img).</summary>
    public static HashResult HashFile(
        string path,
        HashSelection hashes = HashSelection.All,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        (IncrementalHash? md5, IncrementalHash? sha1, IncrementalHash? sha256) = HashUtil.CreateSet(hashes);

        long processed = 0;

        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);

            long total = fs.Length;
            var buffer = new byte[BufferSize];
            long lastReportBytes = 0;
            var reportTimer = Stopwatch.StartNew();

            int count;
            while ((count = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();

                md5?.AppendData(buffer, 0, count);
                sha1?.AppendData(buffer, 0, count);
                sha256?.AppendData(buffer, 0, count);
                processed += count;

                if (progress != null && reportTimer.ElapsedMilliseconds >= 250)
                {
                    double bps = (processed - lastReportBytes) / reportTimer.Elapsed.TotalSeconds;
                    progress.Report(new DiskProgress(processed, total, bps, stopwatch.Elapsed));
                    lastReportBytes = processed;
                    reportTimer.Restart();
                }
            }

            stopwatch.Stop();
            progress?.Report(new DiskProgress(
                processed, total, processed / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));

            return new HashResult
            {
                Completed = true,
                BytesHashed = processed,
                Elapsed = stopwatch.Elapsed,
                Md5 = HashUtil.ToHex(md5),
                Sha1 = HashUtil.ToHex(sha1),
                Sha256 = HashUtil.ToHex(sha256),
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new HashResult { Cancelled = true, BytesHashed = processed, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new HashResult { Error = ex.Message, Elapsed = stopwatch.Elapsed };
        }
        finally
        {
            md5?.Dispose();
            sha1?.Dispose();
            sha256?.Dispose();
        }
    }

    /// <summary>
    /// Computes the requested digests over a physical device. Pass <paramref name="maxBytes"/>
    /// to hash only the leading N bytes — the restore path needs this to compare a disk
    /// against an image smaller than the disk itself.
    /// </summary>
    public static HashResult HashDevice(
        string devicePath,
        HashSelection hashes = HashSelection.All,
        IProgress<DiskProgress>? progress = null,
        CancellationToken cancellation = default,
        long? maxBytes = null)
    {
        var stopwatch = Stopwatch.StartNew();
        (IncrementalHash? md5, IncrementalHash? sha1, IncrementalHash? sha256) = HashUtil.CreateSet(hashes);

        long processed = 0;

        try
        {
            using var reader = RawDiskReader.Open(devicePath);

            var bad = reader.ReadAll(
                (buffer, count) =>
                {
                    md5?.AppendData(buffer, 0, count);
                    sha1?.AppendData(buffer, 0, count);
                    sha256?.AppendData(buffer, 0, count);
                    processed += count;
                },
                progress,
                cancellation,
                maxBytes);

            stopwatch.Stop();

            return new HashResult
            {
                Completed = true,
                BytesHashed = processed,
                Elapsed = stopwatch.Elapsed,
                Md5 = HashUtil.ToHex(md5),
                Sha1 = HashUtil.ToHex(sha1),
                Sha256 = HashUtil.ToHex(sha256),
                BadRegionCount = bad.Count,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new HashResult { Cancelled = true, BytesHashed = processed, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new HashResult { Error = ex.Message, Elapsed = stopwatch.Elapsed };
        }
        finally
        {
            md5?.Dispose();
            sha1?.Dispose();
            sha256?.Dispose();
        }
    }

    /// <summary>
    /// Compares two hash results. Returns true only if every digest present in BOTH
    /// matches, and at least one digest was compared.
    /// </summary>
    public static bool Match(HashResult a, HashResult b)
    {
        bool anyCompared = false;

        if (a.Sha256 is not null && b.Sha256 is not null)
        {
            anyCompared = true;
            if (!string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (a.Sha1 is not null && b.Sha1 is not null)
        {
            anyCompared = true;
            if (!string.Equals(a.Sha1, b.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (a.Md5 is not null && b.Md5 is not null)
        {
            anyCompared = true;
            if (!string.Equals(a.Md5, b.Md5, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return anyCompared;
    }
}
