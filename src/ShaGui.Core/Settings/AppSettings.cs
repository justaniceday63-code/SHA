using ShaGui.Core.Hashing;
using ShaGui.Core.Native;

namespace ShaGui.Core.Settings;

/// <summary>
/// User-adjustable options for hashing runs, persisted in the local SQLite
/// database. Defaults mirror the native buffer default and the manifest
/// defaults in <c>FolderManifestOptions</c> so the app behaves the same
/// before the user changes anything.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Algorithm the Verify page starts on.</summary>
    public ShaAlgo DefaultAlgorithm { get; set; } = ShaAlgo.Sha256;

    /// <summary>Read buffer per file in bytes; the native default is 1 MiB.</summary>
    public ulong BufferSize { get; set; } = ShaHasher.DefaultBufferSize;

    /// <summary>Include hidden and system files in folder runs.</summary>
    public bool IncludeHidden { get; set; } = true;

    /// <summary>Follow symbolic links and junctions in folder runs.</summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>Record finished runs to the history database.</summary>
    public bool RecordHistory { get; set; } = true;

    public AppSettings Clone() => new()
    {
        DefaultAlgorithm = DefaultAlgorithm,
        BufferSize = BufferSize,
        IncludeHidden = IncludeHidden,
        FollowReparsePoints = FollowReparsePoints,
        RecordHistory = RecordHistory,
    };
}
