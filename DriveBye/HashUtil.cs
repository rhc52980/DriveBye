using System;
using System.Security.Cryptography;

namespace DiskUtility.Services;

/// <summary>Small helpers shared by the imaging and verify engines.</summary>
internal static class HashUtil
{
    /// <summary>Creates the set of incremental hashers requested by <paramref name="sel"/>.</summary>
    public static (IncrementalHash? md5, IncrementalHash? sha1, IncrementalHash? sha256) CreateSet(HashSelection sel)
        => (
            sel.HasFlag(HashSelection.Md5) ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null,
            sel.HasFlag(HashSelection.Sha1) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null,
            sel.HasFlag(HashSelection.Sha256) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null
        );

    /// <summary>Finalizes a hasher to a lower-case hex string (or null if the hasher is null).</summary>
    public static string? ToHex(IncrementalHash? hash)
        => hash is null ? null : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}
