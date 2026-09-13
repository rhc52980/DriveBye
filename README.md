# DriveBye — disk imaging and erasure for Windows

**Image a drive, prove the copy is good, and erase the original for real.**

DriveBye images physical drives sector-for-sector, verifies an image against a
drive by hash, restores it, and erases drives — either by overwriting them or by
handing the job to the drive's own firmware with ATA Secure Erase or NVMe
Sanitize. It is a single self-contained `.exe` with no installer and no runtime
to install, so it fits on the USB stick you carry to the machine with the broken
disk.

[![Latest release](https://img.shields.io/github/v/release/rhc52980/DriveBye)](https://github.com/rhc52980/DriveBye/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/rhc52980/DriveBye/total)](https://github.com/rhc52980/DriveBye/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)

---

## Why it exists

Overwriting an SSD does not erase it. Wear-levelling moves blocks out from under
the addresses the host can see, so a pass of zeros — or seven passes of anything
— leaves data in cells the drive never let you write to. The only methods that
reach those blocks are the ones built into the drive: **ATA Secure Erase** on
SATA, **NVMe Sanitize** on NVMe.

Most Windows tools either ignore those commands entirely or bury them behind a
bootable Linux image. DriveBye issues them directly, tells you up front whether
the drive will accept one, and explains why when it won't — a SATA drive frozen
by firmware at boot, a drive that reports no sanitize capability, a controller
that blocks pass-through.

The same honesty runs through the rest of it. A wipe that cannot verify says so
instead of claiming success, and a restore refuses an image it can tell is not
raw rather than writing the container's headers onto your disk.

## What it does

- **Create Image** — raw, sector-for-sector, hashed in the same pass
  (MD5 / SHA-1 / SHA-256). Unreadable sectors are zero-filled and reported
  rather than aborting a multi-hour run.
- **Verify vs Image** — hashes a drive and an image and compares them.
- **Hash File** — digests any file.
- **Erase Support** — read-only. Asks a drive whether it supports ATA Secure
  Erase or NVMe Sanitize and reports its exact state. Safe on any drive,
  including the one Windows is running from.
- **Restore Image** — writes a raw image back to a drive, refusing containers
  (VHD/VHDX, VMDK, QCOW2, VDI, WIM, E01, and compressed archives) that would
  otherwise be written header-and-all onto the disk.
- **Wipe** — zeros, random, DoD 5220.22-M, or a custom byte pattern repeated
  across the whole drive. Where the drive supports it, ATA Secure Erase and NVMe
  Sanitize hand the erase to the firmware.

### The safety rules it will not bend

Every destructive operation refuses the system disk, dismounts the target's
volumes first, and needs a confirmation phrase typed in full.

Each guard **fails closed**: if DriveBye cannot prove a target is safe, it
refuses rather than proceeding. If the system disk cannot be identified at all,
every destructive operation is blocked. If a volume cannot be ruled off the
target drive, the write does not start. And read-back verification only reports
a pass when every byte was actually read — sectors that could not be read come
back as `INCONCLUSIVE`, never as success.

## Install

Download `DriveBye.exe` from the
[latest release](https://github.com/rhc52980/DriveBye/releases/latest) and run
it. That is the whole installation: one file, no .NET needed, nothing written to
Program Files or the registry.

Windows will prompt for administrator every launch — raw device access does not
work without it.

To build from source instead:

```
dotnet build DriveBye.sln                                          # needs the .NET 10 SDK
dotnet publish DriveBye/DriveBye.csproj -p:PublishProfile=Portable # the single-file .exe
```

## What it can't do

- **The write paths are untested against real hardware.** Imaging, hashing and
  the capability probes are exercised; wipe, restore, ATA Secure Erase and NVMe
  Sanitize are written from the specifications and have not been run against a
  drive anyone was willing to destroy. Use a scratch drive first.
- **A frozen drive cannot be secure-erased.** Most UEFI firmware issues
  `SECURITY FREEZE LOCK` to every SATA drive at boot, and a frozen drive rejects
  the whole sequence. Suspending and resuming, or hot-plugging the drive after
  boot, usually clears it. No application can clear it for you.
- **Windows may block NVMe Sanitize.** Which NVMe admin commands survive
  pass-through is up to the storage driver.
- **Images are raw and full-size.** No compression and no sparse handling, so
  imaging a 2 TB drive produces a 2 TB file. It can tell that a file is *not* a
  raw image; it cannot tell that a raw image is intact.
- **x64 Windows only**, 10 1607 or newer.

## Requirements

- Windows 10 1607 or newer, or Windows 11 · x64
- Administrator rights
- To build: the .NET 10 SDK

## Licence

MIT — see [LICENSE](LICENSE).
