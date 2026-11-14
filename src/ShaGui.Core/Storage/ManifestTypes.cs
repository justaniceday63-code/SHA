namespace ShaGui.Core.Storage;

/// <summary>Why an item was not hashed into the manifest.</summary>
public enum SkipReason
{
    /// <summary>Symbolic link, junction or other reparse point — never followed.</summary>
    ReparsePoint = 0,

    /// <summary>Directory enumeration threw (ACL, sharing violation, ...).</summary>
    Inaccessible = 1,

    /// <summary>The file could not be opened or read while hashing.</summary>
    ReadFailed = 2,

    /// <summary>The file changed size or timestamps while being hashed.</summary>
    ChangedDuringHash = 3,

    /// <summary>Cancellation requested before this item was processed.</summary>
    Cancelled = 4,

    /// <summary>Path exceeded the platform limit.</summary>
    PathTooLong = 5,
}

/// <summary>One item that did not make it into the manifest.</summary>
public sealed record SkippedItem
{
    /// <summary>Path relative to the root, canonical form (may be empty if the root itself failed).</summary>
    public required string Path { get; init; }

    public required SkipReason Reason { get; init; }

    /// <summary>OS error text when available; empty otherwise.</summary>
    public string Detail { get; init; } = string.Empty;

    public override string ToString() => $"{Reason}: {Path} {Detail}";
}

/// <summary>Progress phase for a folder run (managed side emits these).</summary>
public enum ManifestStage
{
    /// <summary>Walking the tree to count work.</summary>
    Enumerating = 0,
    /// <summary>Hashing files one by one.</summary>
    Hashing = 1,
    /// <summary>Sorting and finalizing the manifest.</summary>
    Finalizing = 2,
}

/// <summary>Progress of a folder run.</summary>
public readonly record struct ManifestProgress
{
    public ManifestStage Stage { get; init; }
    public long FilesDone { get; init; }
    public long FilesTotal { get; init; }
    public string CurrentPath { get; init; }

    /// <summary>0..1 over the file-count; enumeration phases report 0.</summary>
    public double Fraction => FilesTotal == 0 ? 0 : (double)FilesDone / FilesTotal;
}
