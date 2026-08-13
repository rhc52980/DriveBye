# DriveBye

A Windows disk imaging and erasure utility. Images physical drives sector-for-sector, verifies
images against drives by hash, restores them, and erases drives — by overwriting, or by asking
the drive's own firmware to do it.

Requires elevation: raw device access does not work otherwise, so the manifest requests
administrator and Windows prompts on every launch.

## What it does

- **Create Image** — raw, sector-for-sector, hashed in the same pass (MD5/SHA-1/SHA-256).
  Unreadable sectors are zero-filled and reported rather than aborting the run.
- **Verify vs Image** — hashes a drive and an image and compares them.
- **Hash File** — digests any file.
- **Erase Support** — read-only: asks a drive whether it supports ATA Secure Erase or NVMe
  Sanitize. Safe on any drive, including the system disk.
- **Restore Image** — writes a raw image back to a drive. Refuses container formats (VHD/VHDX,
  VMDK, QCOW2, VDI, WIM, E01, and compressed archives), which would otherwise be written
  header-and-all onto the disk.
- **Wipe** — zeros, random, DoD 5220.22-M, or a custom byte pattern repeated across the drive.
  On drives that support it, ATA Secure Erase and NVMe Sanitize hand the job to the firmware,
  which is the only approach that reaches blocks wear-levelling has moved on an SSD.

Every destructive operation refuses the system disk, dismounts the target's volumes first, and
requires a typed confirmation phrase.

## Building

```
dotnet build DiskUtility.sln
```

Needs the .NET 10 SDK. The result is framework-dependent — it runs only where the .NET 10
Desktop Runtime is installed, and needs the files beside it.

## A portable build

```
dotnet publish Disk_Utility/DiskUtility.csproj -p:PublishProfile=Portable
```

Produces a single self-contained `Disk_Utility/bin/Publish/DriveBye.exe` (~63 MB) that runs on
any x64 Windows machine with no .NET installed — which is the form you want on a USB stick,
next to the machine with the broken drive.

## Icon assets

`app.ico` and `logo.png` are generated from the mascot render. See
[tools/iconmaker](tools/iconmaker) to regenerate them.

## Status

The read paths — enumeration, imaging, hashing, capability probes — are exercised. The paths
that write or erase are not: they need drives that can be destroyed to test honestly. Treat
wipe, restore, ATA Secure Erase and NVMe Sanitize as untested against real hardware, and try
them on a scratch drive first.
