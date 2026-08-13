using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DiskUtility.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>
/// The erase methods an NVMe Sanitize command can perform, valued as the SANACT field of CDW10.
/// </summary>
public enum SanitizeAction
{
    BlockErase = 2,     // erase every block
    Overwrite = 3,      // write a pattern over the media
    CryptoErase = 4,    // discard the media encryption key — instant, and the strongest option
}

/// <summary>What an NVMe drive reports about its sanitize capability and current state.</summary>
public sealed class NvmeSanitizeStatus
{
    /// <summary>True when the drive answered Identify Controller, i.e. it really is NVMe.</summary>
    public bool IsNvme { get; init; }

    public bool CryptoEraseSupported { get; init; }
    public bool BlockEraseSupported { get; init; }
    public bool OverwriteSupported { get; init; }

    /// <summary>A sanitize started earlier is still running; a second one cannot be issued.</summary>
    public bool SanitizeInProgress { get; init; }

    /// <summary>Most recent sanitize outcome, from SSTAT bits 2:0 of the sanitize status log.</summary>
    public int LastStatus { get; init; }

    /// <summary>Progress of an in-flight sanitize, 0.0 to 1.0.</summary>
    public double Progress { get; init; }

    public uint CryptoEraseSeconds { get; init; }
    public uint BlockEraseSeconds { get; init; }
    public uint OverwriteSeconds { get; init; }

    public string? Error { get; init; }

    public bool AnyActionSupported => CryptoEraseSupported || BlockEraseSupported || OverwriteSupported;

    public bool CanSanitizeNow => IsNvme && Error is null && AnyActionSupported && !SanitizeInProgress;

    /// <summary>
    /// Crypto erase first: it discards the media encryption key, so it is both instant and more
    /// thorough than rewriting blocks. Block erase next, overwrite only as a last resort.
    /// </summary>
    public SanitizeAction? PreferredAction =>
        CryptoEraseSupported ? SanitizeAction.CryptoErase
        : BlockEraseSupported ? SanitizeAction.BlockErase
        : OverwriteSupported ? SanitizeAction.Overwrite
        : null;

    public uint EstimatedSeconds(SanitizeAction action) => action switch
    {
        SanitizeAction.CryptoErase => CryptoEraseSeconds,
        SanitizeAction.BlockErase => BlockEraseSeconds,
        SanitizeAction.Overwrite => OverwriteSeconds,
        _ => 0,
    };

    /// <summary>A sentence explaining <see cref="CanSanitizeNow"/>, for the log or a dialog.</summary>
    public string Explain()
    {
        if (!IsNvme)
            return "This drive did not answer NVMe Identify Controller, so it is not an NVMe device "
                 + "— or its driver does not expose NVMe pass-through. For a SATA drive this is the "
                 + "expected answer; see the ATA line instead."
                 + (Error is null ? string.Empty : $" ({Error})");

        if (SanitizeInProgress)
            return $"A sanitize is already running on this drive ({Progress * 100:0.0}% complete). "
                 + "Wait for it to finish.";

        if (!AnyActionSupported)
            return "This NVMe drive reports no sanitize capability at all (SANICAP is empty). "
                 + "Some consumer drives omit the Sanitize command entirely.";

        SanitizeAction action = PreferredAction!.Value;
        uint seconds = EstimatedSeconds(action);
        string estimate = seconds is 0 or 0xFFFFFFFF
            ? ", no time estimate given"
            : $", drive estimates ~{FormatSeconds(seconds)}";

        string previous = LastStatus == 3
            ? " NOTE: the drive's last sanitize FAILED, which can leave it in a restricted state."
            : string.Empty;

        return $"Ready: NVMe sanitize available, {Describe(action)}{estimate}.{previous}";
    }

    internal static string Describe(SanitizeAction action) => action switch
    {
        SanitizeAction.CryptoErase => "crypto erase",
        SanitizeAction.BlockErase => "block erase",
        SanitizeAction.Overwrite => "overwrite",
        _ => "unknown",
    };

    private static string FormatSeconds(uint seconds) =>
        seconds < 60 ? $"{seconds} s"
        : seconds < 3600 ? $"{seconds / 60} min"
        : $"{seconds / 3600.0:0.#} h";
}

/// <summary>Outcome of a sanitize run.</summary>
public sealed class SanitizeResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public SanitizeAction Action { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// True when the command was accepted and the drive is still working. The sanitize survives
    /// reboots and cannot be stopped, so this is reported rather than treated as a failure.
    /// </summary>
    public bool StillRunning { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Issues the NVMe Sanitize admin command. This is the NVMe counterpart to ATA Secure Erase and the
/// right tool for an NVMe SSD, which does not speak ATA at all.
///
/// Unlike ATA secure erase there is no password step, so none of the lock-out hazard applies. The
/// command is asynchronous: the drive accepts it and works in the background, reporting progress in
/// the sanitize status log, and the operation survives a reboot and cannot be cancelled.
/// </summary>
public static class NvmeSanitize
{
    // STORAGE_PROTOCOL_COMMAND header field offsets (ntddstor.h).
    private const int Version = 0;
    private const int LengthField = 4;
    private const int ProtocolType = 8;
    private const int Flags = 12;
    private const int ReturnStatus = 16;
    private const int ErrorCodeField = 20;
    private const int CommandLength = 24;
    private const int ErrorInfoLength = 28;
    private const int DataToDeviceTransferLength = 32;
    private const int DataFromDeviceTransferLength = 36;
    private const int TimeOutValue = 40;
    private const int ErrorInfoOffset = 44;
    private const int DataToDeviceBufferOffset = 48;
    private const int DataFromDeviceBufferOffset = 52;
    private const int CommandSpecific = 56;

    /// <summary>FIELD_OFFSET(STORAGE_PROTOCOL_COMMAND, Command) — the header ends here.</summary>
    private const int CommandOffset = 80;

    /// <summary>
    /// sizeof(STORAGE_PROTOCOL_COMMAND) in C: the header plus its one-byte Command[ANYSIZE_ARRAY]
    /// rounded up to ULONG alignment. This is what the Length field wants, not CommandOffset.
    /// </summary>
    private const int StructSize = 84;

    private const int NvmeCommandLength = 64;
    private const int NvmeErrorInfoLength = 64;
    private const int ErrorInfoAt = CommandOffset + NvmeCommandLength;      // 144
    private const int DataAt = ErrorInfoAt + NvmeErrorInfoLength;           // 208

    private const uint StorageProtocolStructureVersion = 1;
    private const uint ProtocolTypeNvme = 3;
    private const uint FlagAdapterRequest = 0x80000000;
    private const uint NvmeAdminCommand = 1;
    private const uint StorageProtocolStatusSuccess = 0;

    // NVMe command byte offsets within the 64-byte command block.
    private const int CdW0 = 0;
    private const int Nsid = 4;
    private const int CdW10 = 40;
    private const int CdW11 = 44;

    private const byte OpcodeGetLogPage = 0x02;
    private const byte OpcodeIdentify = 0x06;
    private const byte OpcodeSanitize = 0x84;

    private const int IdentifyControllerSize = 4096;
    private const int SanitizeLogSize = 512;
    private const byte SanitizeStatusLogId = 0x81;

    /// <summary>SANICAP is a dword at byte 328 of Identify Controller data.</summary>
    private const int SanicapOffset = 328;
    private const uint SanicapCryptoErase = 1 << 0;
    private const uint SanicapBlockErase = 1 << 1;
    private const uint SanicapOverwrite = 1 << 2;

    private const uint ShortTimeoutSeconds = 30;

    /// <summary>Reads sanitize capability and state. Never throws — failures come back in the status.</summary>
    public static NvmeSanitizeStatus Query(string devicePath)
    {
        try
        {
            using SafeFileHandle handle = OpenDevice(devicePath);
            return ReadStatus(handle);
        }
        catch (Exception ex)
        {
            return new NvmeSanitizeStatus { Error = ex.Message };
        }
    }

    /// <summary>
    /// Issues Sanitize and then follows the drive's progress until it finishes. Refuses the system
    /// disk and dismounts every volume on the target first.
    /// </summary>
    public static SanitizeResult Run(
        PhysicalDisk disk,
        SanitizeAction action,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            DiskSafety.EnsureNotSystemDisk(disk);

            // The only honest place to check cancellation: once the drive has the command, nothing
            // can call it back.
            cancellation.ThrowIfCancellationRequested();

            using SafeFileHandle handle = OpenDevice(disk.DeviceId);

            NvmeSanitizeStatus status = ReadStatus(handle);
            if (!status.CanSanitizeNow)
                throw new InvalidOperationException(status.Explain());

            if (!Supports(status, action))
                throw new InvalidOperationException(
                    $"The drive does not support {NvmeSanitizeStatus.Describe(action)}.");

            using VolumeLock volumes = VolumeLock.ForDisk(disk.Index);

            cancellation.ThrowIfCancellationRequested();

            // CDW10: SANACT in bits 2:0. AUSE, OIPBP and NDAS stay clear, and OWPASS keeps the
            // drive's default pass count for overwrite.
            SendAdminCommand(handle, OpcodeSanitize, nsid: 0,
                cdw10: (uint)action, cdw11: 0, dataFromDeviceLength: 0,
                timeoutSeconds: ShortTimeoutSeconds, what: "SANITIZE",
                hint: "Windows restricts which NVMe admin commands may be passed through, and some "
                    + "drivers block Sanitize specifically even when Identify and log reads work.");

            bool finished = WaitForCompletion(handle, progress, out string? failure);

            stopwatch.Stop();

            if (failure is not null)
                return new SanitizeResult { Error = failure, Action = action, Elapsed = stopwatch.Elapsed };

            return new SanitizeResult
            {
                Completed = finished,
                StillRunning = !finished,
                Action = action,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SanitizeResult { Cancelled = true, Action = action, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SanitizeResult { Error = ex.Message, Action = action, Elapsed = stopwatch.Elapsed };
        }
    }

    private static bool Supports(NvmeSanitizeStatus status, SanitizeAction action) => action switch
    {
        SanitizeAction.CryptoErase => status.CryptoEraseSupported,
        SanitizeAction.BlockErase => status.BlockEraseSupported,
        SanitizeAction.Overwrite => status.OverwriteSupported,
        _ => false,
    };

    /// <summary>
    /// Polls the sanitize status log until the drive stops reporting progress. Deliberately takes no
    /// cancellation token: the sanitize cannot be stopped, and abandoning the poll would only hide a
    /// drive that is still erasing itself.
    /// </summary>
    private static bool WaitForCompletion(
        SafeFileHandle handle, IProgress<double>? progress, out string? failure)
    {
        failure = null;

        while (true)
        {
            NvmeSanitizeStatus status = ReadStatus(handle);

            if (status.Error is not null)
            {
                failure = $"Lost contact with the drive while it was sanitizing: {status.Error}";
                return false;
            }

            if (status.SanitizeInProgress)
            {
                progress?.Report(status.Progress);
                Thread.Sleep(1000);
                continue;
            }

            progress?.Report(1.0);

            // SSTAT bits 2:0 — 1 completed, 2 in progress, 3 failed, 4 completed with forced
            // deallocation. Anything else means the drive never ran it.
            switch (status.LastStatus)
            {
                case 1:
                case 4:
                    return true;
                case 3:
                    failure = "The drive reported that the sanitize FAILED. It may be in a restricted "
                            + "state until another sanitize succeeds.";
                    return false;
                default:
                    failure = $"The drive reports sanitize state {status.LastStatus} after the command "
                            + "was accepted, which means it never ran.";
                    return false;
            }
        }
    }

    private static NvmeSanitizeStatus ReadStatus(SafeFileHandle handle)
    {
        byte[] identify = SendAdminCommand(handle, OpcodeIdentify, nsid: 0,
            cdw10: 1 /* CNS = Identify Controller */, cdw11: 0,
            dataFromDeviceLength: IdentifyControllerSize,
            timeoutSeconds: ShortTimeoutSeconds, what: "IDENTIFY CONTROLLER");

        uint sanicap = BitConverter.ToUInt32(identify, SanicapOffset);

        // Get Log Page CDW10: log id in bits 7:0, and the transfer size minus one, in dwords,
        // in bits 27:16.
        uint numberOfDwordsLower = (SanitizeLogSize / 4) - 1;
        byte[] log = SendAdminCommand(handle, OpcodeGetLogPage, nsid: 0xFFFFFFFF,
            cdw10: SanitizeStatusLogId | (numberOfDwordsLower << 16), cdw11: 0,
            dataFromDeviceLength: SanitizeLogSize,
            timeoutSeconds: ShortTimeoutSeconds, what: "GET LOG PAGE (sanitize status)");

        ushort sprog = BitConverter.ToUInt16(log, 0);
        ushort sstat = BitConverter.ToUInt16(log, 2);
        int state = sstat & 0x7;

        return new NvmeSanitizeStatus
        {
            IsNvme = true,
            CryptoEraseSupported = (sanicap & SanicapCryptoErase) != 0,
            BlockEraseSupported = (sanicap & SanicapBlockErase) != 0,
            OverwriteSupported = (sanicap & SanicapOverwrite) != 0,
            SanitizeInProgress = state == 2,
            LastStatus = state,
            Progress = sprog / 65536.0,
            OverwriteSeconds = BitConverter.ToUInt32(log, 8),
            BlockEraseSeconds = BitConverter.ToUInt32(log, 12),
            CryptoEraseSeconds = BitConverter.ToUInt32(log, 16),
        };
    }

    /// <summary>
    /// Builds a STORAGE_PROTOCOL_COMMAND by hand — its trailing command, error-info and data areas
    /// make it variable-length, so the offsets are written explicitly rather than marshalled.
    /// </summary>
    private static byte[] SendAdminCommand(
        SafeFileHandle handle, byte opcode, uint nsid, uint cdw10, uint cdw11,
        int dataFromDeviceLength, uint timeoutSeconds, string what, string? hint = null)
    {
        int total = DataAt + dataFromDeviceLength;
        var input = new byte[total];
        var output = new byte[total];

        Write(input, Version, StorageProtocolStructureVersion);
        Write(input, LengthField, StructSize);
        Write(input, ProtocolType, ProtocolTypeNvme);
        Write(input, Flags, FlagAdapterRequest);
        Write(input, CommandLength, NvmeCommandLength);
        Write(input, ErrorInfoLength, NvmeErrorInfoLength);
        Write(input, DataToDeviceTransferLength, 0);
        Write(input, DataFromDeviceTransferLength, (uint)dataFromDeviceLength);
        Write(input, TimeOutValue, timeoutSeconds);
        Write(input, ErrorInfoOffset, ErrorInfoAt);
        Write(input, DataToDeviceBufferOffset, 0);
        Write(input, DataFromDeviceBufferOffset, dataFromDeviceLength > 0 ? (uint)DataAt : 0);
        Write(input, CommandSpecific, NvmeAdminCommand);

        // The 64-byte NVMe command itself. PRP entries are filled in by the driver.
        input[CommandOffset + CdW0] = opcode;
        Write(input, CommandOffset + Nsid, nsid);
        Write(input, CommandOffset + CdW10, cdw10);
        Write(input, CommandOffset + CdW11, cdw11);

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.IOCTL_STORAGE_PROTOCOL_COMMAND,
                input, (uint)total, output, (uint)total, out _, IntPtr.Zero))
        {
            // A non-NVMe drive lands here too — its driver simply rejects an NVMe protocol command
            // — so this message stays neutral about the cause and callers add their own reading.
            int err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"{what} was not accepted by the storage driver (Win32 error {err})."
                + (hint is null ? string.Empty : $" {hint}"));
        }

        uint status = BitConverter.ToUInt32(output, ReturnStatus);
        if (status != StorageProtocolStatusSuccess)
        {
            uint code = BitConverter.ToUInt32(output, ErrorCodeField);
            throw new IOException(
                $"The drive rejected {what} (protocol status {status}, error code 0x{code:X8}).");
        }

        if (dataFromDeviceLength == 0) return Array.Empty<byte>();

        var data = new byte[dataFromDeviceLength];
        Array.Copy(output, DataAt, data, 0, dataFromDeviceLength);
        return data;
    }

    private static void Write(byte[] buffer, int offset, uint value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    private static SafeFileHandle OpenDevice(string devicePath)
    {
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
            throw new IOException(
                $"Could not open {devicePath} (Win32 error {err}). The app must run elevated (as Administrator).");
        }

        return handle;
    }
}
