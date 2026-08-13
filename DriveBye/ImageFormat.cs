using System;
using System.IO;
using System.Text;

namespace DiskUtility.Services;

/// <summary>
/// Recognises disk-image containers that are not raw sector dumps.
///
/// A raw image has no signature to check for — it is whatever the drive held — so nothing here
/// can confirm a file IS raw. What it can do is spot files that definitely are not, which is the
/// dangerous case: restoring a VHDX or a gzip writes that file's own headers onto the drive and
/// reports success, leaving something silently unbootable.
/// </summary>
internal static class ImageFormat
{
    /// <summary>Signatures that appear at the very start of the file.</summary>
    private static readonly (byte[] Magic, string Name)[] HeaderSignatures =
    {
        (Ascii("vhdxfile"), "VHDX (Hyper-V disk)"),
        (Ascii("KDMV"), "VMDK (VMware disk)"),
        (Ascii("# Disk DescriptorFile"), "VMDK descriptor (VMware disk)"),
        (new byte[] { 0x51, 0x46, 0x49, 0xFB }, "QCOW/QCOW2 (QEMU disk)"),
        (Ascii("MSWIM\0\0\0"), "WIM (Windows image)"),
        (new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 }, "E01 (EnCase forensic image)"),
        (Ascii("EVF2"), "Ex01 (EnCase forensic image)"),
        (new byte[] { 0x1F, 0x8B }, "gzip archive"),
        (Ascii("PK\x03\x04"), "ZIP archive"),
        (new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, "xz archive"),
        (Ascii("BZh"), "bzip2 archive"),
        (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, "7-Zip archive"),
        (new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "zstd archive"),
        (Ascii("Rar!\x1A\x07"), "RAR archive"),
    };

    /// <summary>VHD keeps its identity in a 512-byte footer rather than a header.</summary>
    private const int VhdFooterSize = 512;

    /// <summary>
    /// Returns the name of the container format this file appears to be, or null when nothing is
    /// recognised — which is the expected answer for a genuine raw image. Unreadable files also
    /// return null, so the caller fails on its own terms rather than on a guess made here.
    /// </summary>
    public static string? DetectContainer(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);

            var header = new byte[64];
            int read = file.Read(header, 0, header.Length);

            foreach ((byte[] magic, string name) in HeaderSignatures)
                if (StartsWith(header, read, magic))
                    return name;

            // VirtualBox writes a human-readable banner before its binary header.
            if (read >= 16 && Encoding.ASCII.GetString(header, 0, read).Contains("VirtualBox Disk Image"))
                return "VDI (VirtualBox disk)";

            if (file.Length >= VhdFooterSize)
            {
                var footer = new byte[8];
                file.Seek(-VhdFooterSize, SeekOrigin.End);
                if (file.Read(footer, 0, footer.Length) == footer.Length
                    && StartsWith(footer, footer.Length, Ascii("conectix")))
                {
                    return "VHD (Hyper-V disk)";
                }
            }

            return null;
        }
        catch
        {
            // Not our error to report — let the restore fail with its own message.
            return null;
        }
    }

    /// <summary>The refusal shown when <see cref="DetectContainer"/> finds something.</summary>
    public static string Explain(string path, string container) =>
        $"{Path.GetFileName(path)} looks like a {container}, not a raw disk image. Writing it would "
        + "put that file's own headers and metadata onto the drive instead of the disk contents it "
        + "describes, and the result would not boot. Convert it to raw first — for example "
        + "`qemu-img convert -O raw input output.img`, or decompress it — and restore that.";

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static bool StartsWith(byte[] buffer, int length, byte[] magic)
    {
        if (length < magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (buffer[i] != magic[i]) return false;
        return true;
    }
}
