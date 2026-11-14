using ShaGui.Core.Native;

namespace ShaGui.Core.Hashing;

/// <summary>
/// Captures the educational 7-stage trace for one input, scoped to a block
/// range, and exposes it as an immutable snapshot.
/// </summary>
/// <remarks>
/// Tracing must never change the digest (asserted by test_trace.c), and the
/// snapshot is copied out of DLL-owned memory immediately — the pointer
/// from <c>sha_trace_data</c> dies at the next trace call or at
/// <c>sha_destroy</c>, so holding it across calls would be a use-after-free.
/// </remarks>
public sealed class ShaTraceSession : IDisposable
{
    private ShaContextHandle? _ctx;
    private ShaTraceStep[] _steps = Array.Empty<ShaTraceStep>();
    private bool _finalized;

    public ShaAlgo Algorithm { get; }

    /// <summary>First block of the traced range (inclusive).</summary>
    public uint FirstBlock { get; }

    /// <summary>Block count; 0 means "to the end, capped by SHA_TRACE_MAX_STEPS".</summary>
    public uint BlockCount { get; }

    public ShaTraceSession(ShaAlgo algo, uint firstBlock = 0, uint blockCount = 1)
    {
        Algorithm = algo;
        FirstBlock = firstBlock;
        BlockCount = blockCount;

        _ctx = ShaContextHandle.Create(algo);
        var status = NativeMethods.sha_trace_configure(
            _ctx.DangerousGetHandle(), enable: 1, firstBlock, blockCount);
        if (status != ShaStatus.Ok)
        {
            throw new ShaNativeException(status, "sha_trace_configure failed");
        }
    }

    /// <summary>Steps recorded so far (live view; call <see cref="Snapshot"/> for a copy).</summary>
    public int Count => _ctx is null ? 0 : (int)NativeMethods.sha_trace_count(_ctx.DangerousGetHandle());

    /// <summary>Feeds more input. Invalid after <see cref="Finalize"/>.</summary>
    public void Update(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_ctx is null, this);
        if (_finalized)
        {
            throw new InvalidOperationException("cannot update after finalize");
        }
        var status = NativeMethods.sha_update(
            _ctx.DangerousGetHandle(), data, (ulong)data.Length);
        if (status != ShaStatus.Ok)
        {
            throw new ShaNativeException(status, "sha_update failed");
        }
    }

    public void UpdateText(string text) => Update(HashResult.Utf8(text));

    /// <summary>
    /// Finishes the hash and returns the digest, which is computed from the
    /// same state the trace describes (stage 6 -> 7).
    /// </summary>
    public HashResult Finalize()
    {
        ObjectDisposedException.ThrowIf(_ctx is null, this);
        if (_finalized)
        {
            throw new InvalidOperationException("already finalized");
        }

        byte[] digest = new byte[ShaHasher.DigestLength(Algorithm)];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var status = NativeMethods.sha_final(
            _ctx.DangerousGetHandle(), digest, (nuint)digest.Length, out nuint written);
        sw.Stop();
        if (status != ShaStatus.Ok)
        {
            throw new ShaNativeException(status, "sha_final failed");
        }
        if (written != (nuint)digest.Length)
        {
            throw new ShaNativeException(ShaStatus.State, "digest length mismatch");
        }

        _finalized = true;
        CaptureSteps();
        return HashResult.FromNative(Algorithm, digest, sw.Elapsed);
    }

    /// <summary>Copies the trace out of DLL memory into a managed array.</summary>
    private unsafe void CaptureSteps()
    {
        if (_ctx is null)
        {
            return;
        }
        IntPtr raw = NativeMethods.sha_trace_data(_ctx.DangerousGetHandle());
        int count = Count;
        if (raw == IntPtr.Zero || count == 0)
        {
            _steps = Array.Empty<ShaTraceStep>();
            return;
        }
        _steps = ShaTraceStep.FromNative((void*)raw, count).ToArray();
    }

    /// <summary>
    /// The captured steps. Empty until <see cref="Finalize"/> has run, because
    /// the native side only exposes the array coherently after the run ends.
    /// </summary>
    public IReadOnlyList<ShaTraceStep> Steps => _steps;

    /// <summary>Steps belonging to a single stage (e.g. all compression rounds).</summary>
    public IEnumerable<ShaTraceStep> StepsOf(ShaTraceStage stage)
        => _steps.Where(s => s.Stage == (uint)stage);

    /// <summary>Count of steps in a stage — used to assert 80 vs 64 rounds.</summary>
    public int CountOf(ShaTraceStage stage)
    {
        int n = 0;
        foreach (var s in _steps)
        {
            if (s.Stage == (uint)stage)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Rebuilds the chaining value from traced STATE_UPDATE steps the same
    /// way the compression function does: per block, add the final working
    /// registers into the IV. Proves the trace is sufficient to reconstruct
    /// the digest the UI displays.
    /// </summary>
    public byte[] ReconstructDigest()
    {
        int nwords = Algorithm == ShaAlgo.Sha1 ? 5 : 8;
        uint[] iv = Algorithm == ShaAlgo.Sha1
            ? new uint[] { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u }
            : new uint[] { 0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
                           0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u };

        var acc = new uint[8];
        Array.Copy(iv, acc, nwords);

        var pending = new uint[8];
        uint curBlock = 0;
        bool havePending = false;

        foreach (var step in _steps)
        {
            if (step.Stage != (uint)ShaTraceStage.StateUpdate)
            {
                continue;
            }
            if (!havePending)
            {
                curBlock = step.Block;
                havePending = true;
            }
            else if (step.Block != curBlock)
            {
                for (int w = 0; w < nwords; w++)
                {
                    acc[w] += pending[w];
                }
                curBlock = step.Block;
            }
            for (int w = 0; w < nwords; w++)
            {
                pending[w] = step.Words[w];
            }
        }
        if (havePending)
        {
            for (int w = 0; w < nwords; w++)
            {
                acc[w] += pending[w];
            }
        }

        var digest = new byte[nwords * 4];
        for (int w = 0; w < nwords; w++)
        {
            digest[w * 4] = (byte)(acc[w] >> 24);
            digest[w * 4 + 1] = (byte)(acc[w] >> 16);
            digest[w * 4 + 2] = (byte)(acc[w] >> 8);
            digest[w * 4 + 3] = (byte)acc[w];
        }
        return digest;
    }

    /// <summary>One-shot helper: hash text and return both digest and trace.</summary>
    public static (HashResult Result, ShaTraceSession Session) TraceText(
        ShaAlgo algo, string text, uint firstBlock = 0, uint blockCount = 1)
    {
        var session = new ShaTraceSession(algo, firstBlock, blockCount);
        try
        {
            session.UpdateText(text);
            var result = session.Finalize();
            return (result, session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _ctx?.Dispose();
        _ctx = null;
        _steps = Array.Empty<ShaTraceStep>();
        GC.SuppressFinalize(this);
    }
}
