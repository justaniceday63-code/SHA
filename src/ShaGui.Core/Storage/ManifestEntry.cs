namespace ShaGui.Core.Storage;

/// <summary>Kind of a manifest entry.</summary>
public enum EntryKind
{
    /// <summary>Regular file: has a size and a content hash.</summary>
    File = 0,

    /// <summary>
    /// Directory: size 0, no hash. Emitted even when empty so the on-disk
    /// structure can be recreated from the manifest alone.
    /// </summary>
    Directory = 1,
}

/// <summary>
/// One row of a folder manifest. Immutable and value-comparable so two
/// manifests can be diffed without reference identity surprises.
/// </summary>
public sealed record ManifestEntry
{
    public required EntryKind Kind { get; init; }

    /// <summary>
    /// Path relative to the manifest root, NFC-normalized, using '/' as the
    /// separator on every platform, with no leading './'. Sorting this
    /// string with <see cref="ManifestPathComparer"/> is the canonical
    /// order (see docs/MANIFEST.md).
    /// </summary>
    public required string Path { get; init; }

    /// <summary>File size in bytes; always 0 for <see cref="EntryKind.Directory"/>.</summary>
    public long Size { get; init; }

    /// <summary>Lower-case hex digest; always null for directories.</summary>
    public string? Hash { get; init; }

    /// <summary>Algorithm that produced <see cref="Hash"/>.</summary>
    public ShaAlgoForManifest Algorithm { get; init; }

    public override string ToString() => Kind switch
    {
        EntryKind.Directory => $"D {Path}",
        _ => $"F {Size,12} {Hash} {Path}",
    };
}

/// <summary>Algorithms usable in a manifest (subset alias kept out of the ABI namespace).</summary>
public enum ShaAlgoForManifest
{
    Sha1 = 1,
    Sha256 = 2,
}
