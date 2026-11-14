namespace ShaGui.Core.Native;

/// <summary>Algorithm selector. Mirrors <c>sha_algo_t</c>.</summary>
public enum ShaAlgo
{
    Sha1 = 1,
    Sha256 = 2,
}

/// <summary>Status codes returned by the native backend. Mirrors <c>sha_status_t</c>.</summary>
/// <remarks>
/// Precedence when several conditions apply at once (what the Verify UI
/// renders): InvalidArg &gt; Cancelled &gt; Io &gt; Access &gt; Changed &gt; Ok.
/// A folder run that skipped anything never reports <see cref="Ok"/>.
/// </remarks>
public enum ShaStatus
{
    Ok = 0,
    InvalidArg = 1,
    Unsupported = 2,
    Io = 3,
    Cancelled = 4,
    Changed = 5,
    Access = 6,
    NoMem = 7,
    State = 8,
    Overflow = 9,
    TooLargeForTrace = 10,
    PathTooLong = 11,
    Utf8 = 12,
}

/// <summary>Progress phase. Mirrors <c>sha_stage_t</c>.</summary>
public enum ShaStage
{
    /// <summary>Emitted by the managed folder walker, not by C.</summary>
    Enumerating = 0,
    /// <summary>Emitted by <c>sha_hash_file()</c>.</summary>
    Hashing = 1,
    Finalizing = 2,
}

/// <summary>Trace stage. Mirrors <c>sha_trace_stage_t</c> (the 7 visualization stages).</summary>
public enum ShaTraceStage
{
    None = 0,
    /// <summary>Stage 1b: raw block bytes decoded into big-endian words.</summary>
    InputWords = 1,
    /// <summary>Stage 2: padding sequence.</summary>
    Padding = 2,
    /// <summary>Stage 3: block split.</summary>
    BlockSplit = 3,
    /// <summary>Stage 4: message schedule.</summary>
    Schedule = 4,
    /// <summary>Stage 5: compression rounds.</summary>
    Compression = 5,
    /// <summary>Stage 6: working-register update.</summary>
    StateUpdate = 6,
    /// <summary>Stage 7: final digest serialization.</summary>
    Digest = 7,
}
