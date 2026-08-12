using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DiskUtility.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskUtility.Services;

/// <summary>What the drive reports about its ATA security feature set (IDENTIFY DEVICE word 128).</summary>
public sealed class AtaSecurityStatus
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool Locked { get; init; }
    public bool Frozen { get; init; }
    public bool CountExpired { get; init; }
    public bool EnhancedEraseSupported { get; init; }

    /// <summary>Drive's own estimate for a normal erase, in minutes. 0 when it does not say.</summary>
    public int EraseMinutes { get; init; }

    /// <summary>Drive's own estimate for an enhanced erase, in minutes. 0 when it does not say.</summary>
    public int EnhancedEraseMinutes { get; init; }

    /// <summary>Set when the drive could not be asked at all (not ATA, no pass-through support).</summary>
    public string? Error { get; init; }

    /// <summary>True only when a secure erase could actually be issued right now.</summary>
    public bool CanEraseNow =>
        Error is null && Supported && !Frozen && !Locked && !Enabled && !CountExpired;

    /// <summary>A sentence explaining <see cref="CanEraseNow"/>, suitable for the log or a dialog.</summary>
    public string Explain()
    {
        if (Error is not null)
            return $"ATA security could not be queried: {Error}";

        if (!Supported)
            return "This drive does not report the ATA security feature set. NVMe drives in particular "
                 + "use NVMe Format / Sanitize instead, which this tool does not implement.";

        if (Frozen)
            return "The drive is FROZEN: firmware issued SECURITY FREEZE LOCK at boot, and a frozen drive "
                 + "rejects secure erase. Suspending and resuming the machine, or hot-plugging the drive "
                 + "after boot, usually clears it.";

        if (Locked)
            return "The drive is LOCKED by an existing ATA password. Unlock it before erasing.";

        if (Enabled)
            return "The drive already has an ATA password set. Refusing to touch it — erasing would "
                 + "require that password, and setting a second one risks locking the drive for good.";

        if (CountExpired)
            return "The drive's password attempt counter has expired. It needs a power cycle before "
                 + "security commands will be accepted.";

        string mode = EnhancedEraseSupported ? "enhanced" : "normal";
        int minutes = EnhancedEraseSupported ? EnhancedEraseMinutes : EraseMinutes;
        string estimate = minutes > 0 ? $", drive estimates ~{minutes} min" : ", no time estimate given";
        return $"Ready: ATA secure erase available in {mode} mode{estimate}.";
    }
}

/// <summary>Outcome of a secure erase attempt.</summary>
public sealed class SecureEraseResult
{
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public bool UsedEnhanced { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// True when the drive is still holding the password we set. The drive will refuse access after
    /// its next power cycle until it is unlocked, so this must be reported loudly, never swallowed.
    /// </summary>
    public bool PasswordLeftSet { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Issues ATA SECURITY ERASE UNIT, which asks the drive's own firmware to erase itself. This is the
/// only overwrite-free method that is meaningful on an SSD, because it reaches blocks that
/// wear-levelling has moved out from under any host-visible address.
///
/// The sequence is SET PASSWORD -> ERASE PREPARE -> ERASE UNIT, and the password is the hazard: a
/// drive whose password is set but whose erase did not complete comes back locked and unusable. So
/// the password is a fixed documented string rather than anything random, a failed erase triggers an
/// automatic SECURITY DISABLE PASSWORD, and a drive that still holds a password at the end is
/// reported with the string needed to unlock it by hand.
/// </summary>
public static class AtaSecureErase
{
    /// <summary>
    /// The password set for the duration of the erase. Fixed and documented on purpose: if a drive
    /// is ever left locked, this is what unlocks it (e.g. hdparm --security-disable).
    /// </summary>
    public const string Password = "DiskUtility";

    // IDENTIFY DEVICE word 128 — security status bits.
    private const int SecurityStatusWord = 128;
    private const ushort SecuritySupported = 1 << 0;
    private const ushort SecurityEnabled = 1 << 1;
    private const ushort SecurityLocked = 1 << 2;
    private const ushort SecurityFrozen = 1 << 3;
    private const ushort SecurityCountExpired = 1 << 4;
    private const ushort SecurityEnhancedErase = 1 << 5;

    private const int NormalEraseTimeWord = 89;
    private const int EnhancedEraseTimeWord = 90;

    private const uint ShortCommandTimeoutSeconds = 30;

    /// <summary>Reads the drive's security status. Never throws — failures come back in the status.</summary>
    public static AtaSecurityStatus Query(string devicePath)
    {
        try
        {
            using SafeFileHandle handle = OpenDevice(devicePath);
            return ReadStatus(handle);
        }
        catch (Exception ex)
        {
            return new AtaSecurityStatus { Error = ex.Message };
        }
    }

    /// <summary>
    /// Runs the full erase sequence against the drive. Refuses the system disk, refuses any drive
    /// whose security state is not exactly "supported, idle and unlocked", and dismounts every
    /// volume on the target first.
    /// </summary>
    public static SecureEraseResult Erase(
        PhysicalDisk disk, bool enhanced, CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            DiskSafety.EnsureNotSystemDisk(disk);
            cancellation.ThrowIfCancellationRequested();

            using SafeFileHandle handle = OpenDevice(disk.DeviceId);

            AtaSecurityStatus status = ReadStatus(handle);
            if (!status.CanEraseNow)
                throw new InvalidOperationException(status.Explain());

            if (enhanced && !status.EnhancedEraseSupported)
                throw new InvalidOperationException(
                    "The drive does not support enhanced secure erase.");

            // The firmware is about to invalidate everything on the disk; get the filesystems off it
            // first, and keep them off for the duration.
            using VolumeLock volumes = VolumeLock.ForDisk(disk.Index);

            cancellation.ThrowIfCancellationRequested();

            return RunEraseSequence(handle, enhanced, status, stopwatch);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SecureEraseResult { Cancelled = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SecureEraseResult { Error = ex.Message, Elapsed = stopwatch.Elapsed };
        }
    }

    /// <summary>
    /// The part that must not be interrupted. Once the password is set, every exit path has to
    /// either complete the erase (which clears the password) or take the password back off.
    /// </summary>
    private static SecureEraseResult RunEraseSequence(
        SafeFileHandle handle, bool enhanced, AtaSecurityStatus status, Stopwatch stopwatch)
    {
        bool passwordSet = false;

        try
        {
            SendCommand(handle, NativeMethods.ATA_SECURITY_SET_PASSWORD,
                BuildSecurityBuffer(control: 0x0000), ShortCommandTimeoutSeconds,
                "SECURITY SET PASSWORD");
            passwordSet = true;

            // ERASE PREPARE must immediately precede ERASE UNIT; anything in between voids it.
            SendCommand(handle, NativeMethods.ATA_SECURITY_ERASE_PREPARE,
                data: null, ShortCommandTimeoutSeconds, "SECURITY ERASE PREPARE");

            int minutes = enhanced ? status.EnhancedEraseMinutes : status.EraseMinutes;
            SendCommand(handle, NativeMethods.ATA_SECURITY_ERASE_UNIT,
                BuildSecurityBuffer(control: (ushort)(enhanced ? 0x0002 : 0x0000)),
                EraseTimeoutSeconds(minutes), "SECURITY ERASE UNIT");

            // A successful erase clears the password by itself. Confirm that rather than assume it.
            AtaSecurityStatus after = ReadStatus(handle);
            if (after.Enabled)
            {
                TryDisablePassword(handle);
                after = ReadStatus(handle);
            }

            stopwatch.Stop();

            if (after.Enabled)
            {
                return new SecureEraseResult
                {
                    Error = "The erase reported success but the drive is still holding the password. "
                          + RecoveryAdvice,
                    PasswordLeftSet = true,
                    UsedEnhanced = enhanced,
                    Elapsed = stopwatch.Elapsed,
                };
            }

            return new SecureEraseResult
            {
                Completed = true,
                UsedEnhanced = enhanced,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (Exception ex)
        {
            bool stillLocked = false;

            if (passwordSet)
            {
                // Without this the drive would come back locked on its next power cycle.
                TryDisablePassword(handle);
                stillLocked = SafeReadStatus(handle)?.Enabled ?? true;
            }

            stopwatch.Stop();
            return new SecureEraseResult
            {
                Error = stillLocked ? $"{ex.Message}\n{RecoveryAdvice}" : ex.Message,
                PasswordLeftSet = stillLocked,
                UsedEnhanced = enhanced,
                Elapsed = stopwatch.Elapsed,
            };
        }
    }

    private static string RecoveryAdvice =>
        $"THE DRIVE IS LOCKED with the password \"{Password}\" and will refuse access after its next "
        + "power cycle. Unlock or clear it with that password before powering down — for example "
        + $"`hdparm --user-master u --security-disable {Password} /dev/sdX` from a Linux boot medium.";

    private static bool TryDisablePassword(SafeFileHandle handle)
    {
        try
        {
            SendCommand(handle, NativeMethods.ATA_SECURITY_DISABLE_PASSWORD,
                BuildSecurityBuffer(control: 0x0000), ShortCommandTimeoutSeconds,
                "SECURITY DISABLE PASSWORD");
            return true;
        }
        catch
        {
            return false;   // caller re-reads the status to find out what actually happened
        }
    }

    private static AtaSecurityStatus? SafeReadStatus(SafeFileHandle handle)
    {
        try { return ReadStatus(handle); }
        catch { return null; }
    }

    private static AtaSecurityStatus ReadStatus(SafeFileHandle handle)
    {
        ushort[] id = IdentifyDevice(handle);
        ushort security = id[SecurityStatusWord];

        return new AtaSecurityStatus
        {
            Supported = (security & SecuritySupported) != 0,
            Enabled = (security & SecurityEnabled) != 0,
            Locked = (security & SecurityLocked) != 0,
            Frozen = (security & SecurityFrozen) != 0,
            CountExpired = (security & SecurityCountExpired) != 0,
            EnhancedEraseSupported = (security & SecurityEnhancedErase) != 0,
            EraseMinutes = DecodeEraseMinutes(id[NormalEraseTimeWord]),
            EnhancedEraseMinutes = DecodeEraseMinutes(id[EnhancedEraseTimeWord]),
        };
    }

    /// <summary>
    /// IDENTIFY words 89/90 hold the drive's erase-time estimate in 2-minute units — in the low
    /// byte normally, or in bits 14:0 when bit 15 marks the extended form. Zero means unstated.
    /// </summary>
    private static int DecodeEraseMinutes(ushort word)
    {
        if (word == 0) return 0;
        int units = (word & 0x8000) != 0 ? word & 0x7FFF : word & 0x00FF;
        return units * 2;
    }

    /// <summary>
    /// Allow generously more than the drive's estimate — the erase cannot be restarted if it times
    /// out mid-flight, and an aborted erase is exactly the state that leaves a drive locked.
    /// </summary>
    private static uint EraseTimeoutSeconds(int estimateMinutes)
    {
        const uint minimum = 30 * 60;         // half an hour even for an "instant" SSD erase
        const uint maximum = 12 * 60 * 60;
        if (estimateMinutes <= 0) return maximum;
        uint requested = (uint)estimateMinutes * 60 * 3;
        return Math.Clamp(requested, minimum, maximum);
    }

    private static ushort[] IdentifyDevice(SafeFileHandle handle)
    {
        var raw = new byte[512];
        SendCommand(handle, NativeMethods.ATA_IDENTIFY_DEVICE, raw, ShortCommandTimeoutSeconds,
            "IDENTIFY DEVICE", dataIn: true);

        var words = new ushort[256];
        for (int i = 0; i < words.Length; i++)
            words[i] = (ushort)(raw[i * 2] | (raw[(i * 2) + 1] << 8));
        return words;
    }

    /// <summary>
    /// The 512-byte block the security commands take: a control word, then the password in bytes
    /// 2..33. Control bit 0 selects user (0) over master password, bit 1 selects enhanced erase,
    /// and bit 8 sets Maximum security — left clear, so High security is used and the master
    /// password can still recover the drive.
    /// </summary>
    private static byte[] BuildSecurityBuffer(ushort control)
    {
        var buffer = new byte[512];
        buffer[0] = (byte)(control & 0xFF);
        buffer[1] = (byte)(control >> 8);

        byte[] password = Encoding.ASCII.GetBytes(Password);
        Array.Copy(password, 0, buffer, 2, Math.Min(password.Length, 32));
        return buffer;
    }

    private static void SendCommand(
        SafeFileHandle handle, byte command, byte[]? data, uint timeoutSeconds, string what,
        bool dataIn = false)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var apt = new NativeMethods.ATA_PASS_THROUGH_DIRECT
            {
                Length = (ushort)Marshal.SizeOf<NativeMethods.ATA_PASS_THROUGH_DIRECT>(),
                TimeOutValue = timeoutSeconds,
                AtaFlags = NativeMethods.ATA_FLAGS_DRDY_REQUIRED,
                Command = command,
                DeviceHead = 0xA0,
            };

            if (data is not null)
            {
                buffer = Marshal.AllocHGlobal(data.Length);
                if (!dataIn) Marshal.Copy(data, 0, buffer, data.Length);

                apt.AtaFlags |= dataIn ? NativeMethods.ATA_FLAGS_DATA_IN : NativeMethods.ATA_FLAGS_DATA_OUT;
                apt.DataBuffer = buffer;
                apt.DataTransferLength = (uint)data.Length;
                apt.SectorCount = (byte)(data.Length / 512);
            }

            if (!NativeMethods.DeviceIoControl(
                    handle, NativeMethods.IOCTL_ATA_PASS_THROUGH_DIRECT,
                    ref apt, apt.Length, ref apt, apt.Length, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"{what} could not be sent to the drive (Win32 error {err}). ATA pass-through is "
                    + "not available on every controller — USB bridges and RAID drivers commonly "
                    + "block it, and NVMe drives do not speak ATA at all.");
            }

            // The IOCTL succeeding only means the command reached the drive. Whether the drive
            // accepted it is in the returned Status/Error registers.
            byte status = apt.Command;
            byte error = apt.Features;
            if ((status & (NativeMethods.ATA_STATUS_ERR | NativeMethods.ATA_STATUS_DF)) != 0)
            {
                throw new IOException(
                    $"The drive rejected {what} (status 0x{status:X2}, error 0x{error:X2}).");
            }

            if (dataIn && data is not null)
                Marshal.Copy(buffer, data, 0, data.Length);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

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
