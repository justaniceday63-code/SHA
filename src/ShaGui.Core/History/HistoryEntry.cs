using ShaGui.Core.Native;

namespace ShaGui.Core.History;

/// <summary>What a recorded run hashed.</summary>
public enum RunKind
{
    Text = 0,
    File = 1,
    Folder = 2,
}

/// <summary>
/// One recorded hashing run. Immutable; the store assigns <see cref="Id"/>
/// on insert. Throughput is derived, never stored, so it can never drift
/// from the size and elapsed fields the chart is drawn from.
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>Row id; 0 until the store has inserted the entry.</summary>
    public long Id { get; init; }

    /// <summary>When the run finished (UTC).</summary>
    public DateTimeOffset RecordedUtc { get; init; } = DateTimeOffset.UtcNow;

    public ShaAlgo Algorithm { get; init; } = ShaAlgo.Sha256;

    public RunKind Kind { get; init; }

    /// <summary>File/folder path, or a short preview made from the text that was hashed.</summary>
    public required string Subject { get; init; }

    /// <summary>Bytes of content hashed (folder: sum of file sizes).</summary>
    public long Bytes { get; init; }

    /// <summary>Time spent in the read+hash work only.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Lower-case hex digest of the run.</summary>
    public required string Digest { get; init; }

    /// <summary>False when the run skipped or cancelled something (folders only).</summary>
    public bool Complete { get; init; } = true;

    /// <summary>Outcome; <see cref="ShaStatus.Ok"/> on success.</summary>
    public ShaStatus Status { get; init; } = ShaStatus.Ok;

    /// <summary>MiB/s over <see cref="Elapsed"/>; 0 when the time or size is unknown.</summary>
    public double ThroughputMiBPerSecond
        => Elapsed.TotalSeconds <= 0 || Bytes <= 0
            ? 0
            : Bytes / (1024.0 * 1024) / Elapsed.TotalSeconds;

    /// <summary>"sha256" / "sha1", lower-case — matches the manifest header.</summary>
    public string AlgorithmName => Algorithm == ShaAlgo.Sha1 ? "sha1" : "sha256";
}
