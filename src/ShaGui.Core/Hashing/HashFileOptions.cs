using ShaGui.Core.Native;

namespace ShaGui.Core.Hashing;

/// <summary>Progress snapshot handed to <see cref="HashFileOptions.Progress"/>.</summary>
public readonly record struct HashProgress
{
    public ShaStage Stage { get; init; }
    public ulong BytesDone { get; init; }
    public ulong BytesTotal { get; init; }
    public string Path { get; init; }
    public bool PathTruncated { get; init; }

    /// <summary>0..1, or 0 when the total is unknown (e.g. a pipe).</summary>
    public double Fraction => BytesTotal == 0 ? 0 : (double)BytesDone / BytesTotal;
}

/// <summary>Options for <see cref="ShaHasher.HashFile"/>.</summary>
public sealed class HashFileOptions
{
    /// <summary>Read buffer in bytes; 0 selects the native default (1 MiB).</summary>
    public ulong BufferSize { get; init; }

    /// <summary>
    /// Progress sink. Invoked from the hashing thread at ~10 Hz (native
    /// throttle). Marshal to the UI thread inside the handler if needed —
    /// the handler here runs off the UI thread by design.
    /// </summary>
    public Action<HashProgress>? Progress { get; init; }

    /// <summary>
    /// Cooperative cancellation. Set before starting to abort early, or
    /// wire a <see cref="CancellationToken"/> via <see cref="CancellationToken"/>.
    /// </summary>
    public ShaCancelSource? Cancel { get; init; }

    /// <summary>
    /// Optional token bridged to <see cref="Cancel"/>. Cancelling the token
    /// sets the native flag through the DLL's InterlockedExchange.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Resolves whether the run should stop. Evaluated at the native
    /// progress callback (~10 Hz). Overrides <see cref="CancellationToken"/>
    /// when both are supplied.
    /// </summary>
    public Func<bool>? ShouldCancel { get; init; }

    internal bool ShouldStop()
    {
        if (ShouldCancel is { } pred && pred())
        {
            return true;
        }
        if (CancellationToken.IsCancellationRequested)
        {
            return true;
        }
        return Cancel?.IsCancellationRequested == true;
    }
}
