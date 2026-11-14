using System.Security.Cryptography;
using System.Text;
using ShaGui.Core.Native;

namespace ShaGui.Core.Hashing;

/// <summary>Immutable result of one hashing operation.</summary>
public readonly record struct HashResult
{
    public ShaAlgo Algorithm { get; init; }

    /// <summary>Raw digest bytes (20 for SHA-1, 32 for SHA-256). Never null on success.</summary>
    public byte[] Hash { get; init; }

    /// <summary>Lower-case hex form of <see cref="Hash"/>.</summary>
    public string Hex { get; init; }

    /// <summary>
    /// Time spent in the read+hash work only (native QPC around
    /// <c>sha_hash_file</c>), or the measured span for in-memory hashing.
    /// Excludes UI, enumeration and rendering.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Status of the operation; <see cref="ShaStatus.Ok"/> on success.</summary>
    public ShaStatus Status { get; init; }

    public bool Success => Status == ShaStatus.Ok;

    public static HashResult FromNative(ShaAlgo algo, byte[] hash, TimeSpan elapsed, ShaStatus status = ShaStatus.Ok)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return new HashResult
        {
            Algorithm = algo,
            Hash = hash,
            Hex = Convert.ToHexString(hash).ToLowerInvariant(),
            Elapsed = elapsed,
            Status = status,
        };
    }

    /// <summary>Compares against another hex digest, case-insensitively.</summary>
    public bool MatchesHex(string expectedHex)
        => string.Equals(Hex, expectedHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>Constant-time-ish comparison against raw bytes.</summary>
    public bool Matches(byte[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return CryptographicOperations.FixedTimeEquals(Hash, expected);
    }

    /// <summary>UTF-8 convenience used by the text tab.</summary>
    public static byte[] Utf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetBytes(text);
    }
}
