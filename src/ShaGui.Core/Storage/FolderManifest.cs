using ShaGui.Core.Hashing;
using ShaGui.Core.Native;

namespace ShaGui.Core.Storage;

/// <summary>Options for <see cref="FolderManifestBuilder.Build"/>.</summary>
public sealed class FolderManifestOptions
{
    /// <summary>Algorithm for every file in the manifest.</summary>
    public ShaAlgo Algorithm { get; init; } = ShaAlgo.Sha256;

    /// <summary>Read buffer per file; 0 selects the native 1 MiB default.</summary>
    public ulong BufferSize { get; init; }

    /// <summary>Include hidden/system files (default: yes — the spec requires it).</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>
    /// Include reparse points (symlinks, junctions). Default false: the
    /// spec says they are skipped, because following them can loop or
    /// escape the tree, and a manifest that silently left the root is worse
    /// than one that documents the skip.
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>Reported at ~10 Hz from the hashing thread.</summary>
    public Action<ManifestProgress>? Progress { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Result of one folder run.</summary>
public sealed record FolderManifest
{
    /// <summary>Absolute root the manifest was built from.</summary>
    public required string Root { get; init; }

    public required ShaAlgo Algorithm { get; init; }

    /// <summary>Entries in canonical (NFC, '/', UTF-8 ordinal) order.</summary>
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }

    /// <summary>Items that could not be hashed. Non-empty means NOT fully verified.</summary>
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }

    /// <summary>UTC timestamp of the run (informational; not part of the hash).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Total files considered (hashed + skipped).</summary>
    public long FilesTotal { get; init; }

    /// <summary>True only when every single item was hashed — the spec forbids claiming "verified" otherwise.</summary>
    public bool IsComplete => Skipped.Count == 0;

    /// <summary>True when cancellation ended the run early.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>
    /// Canonical text form (see docs/MANIFEST.md): one line per entry,
    /// sorted, LF-only, no BOM. Two identical trees produce identical text
    /// on any machine.
    /// </summary>
    public string ToCanonicalText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# shagui-manifest v1 algo=").Append(AlgorithmName()).Append('\n');
        foreach (var e in Entries)
        {
            if (e.Kind == EntryKind.Directory)
            {
                sb.Append("D\t0\t-\t").Append(e.Path).Append('\n');
            }
            else
            {
                sb.Append("F\t").Append(e.Size).Append('\t')
                  .Append(e.Hash).Append('\t').Append(e.Path).Append('\n');
            }
        }
        if (Skipped.Count > 0)
        {
            foreach (var s in Skipped)
            {
                sb.Append("?\t").Append(s.Reason).Append('\t')
                  .Append(s.Detail.Replace('\t', ' ').Replace('\n', ' '))
                  .Append('\t').Append(s.Path).Append('\n');
            }
        }
        return sb.ToString();

        string AlgorithmName() => Algorithm == ShaAlgo.Sha1 ? "sha1" : "sha256";
    }

    /// <summary>Hex digest of <see cref="ToCanonicalText"/> — one number for "did anything change?".</summary>
    public string ContentHash()
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(ToCanonicalText()))).ToLowerInvariant();
}
