using System.Runtime.InteropServices;

namespace ShaGui.Core.Native;

/// <summary>
/// Owns a native <c>sha_ctx_t*</c>. Uses <see cref="SafeHandle"/> so the
/// context is released exactly once even when an exception unwinds through
/// hashing code — a plain <c>IntPtr</c> field would leak on every failed
/// <c>sha_final</c>.
/// </summary>
internal sealed class ShaContextHandle : SafeHandle
{
    private ShaContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.sha_destroy(handle);
        return true;
    }

    /// <summary>Creates a context for <paramref name="algo"/>, or throws on failure.</summary>
    internal static ShaContextHandle Create(ShaAlgo algo)
    {
        var status = NativeMethods.sha_create(algo, out IntPtr raw);
        if (status != ShaStatus.Ok || raw == IntPtr.Zero)
        {
            throw new ShaNativeException(status, "sha_create failed");
        }

        // SafeHandle's parameterless ctor is private, so set via reflection-free
        // path: construct through the marshaller-friendly factory below.
        var handle = new ShaContextHandle();
        handle.SetHandle(raw);
        return handle;
    }

    /// <summary>Re-arms the context for reuse (native sha_reset keeps trace config).</summary>
    internal void Reset()
    {
        var status = NativeMethods.sha_reset(handle);
        if (status != ShaStatus.Ok)
        {
            throw new ShaNativeException(status, "sha_reset failed");
        }
    }
}
