using System.Runtime.InteropServices;

namespace ShaGui.Core.Native;

/// <summary>
/// Owns a native <c>sha_cancel_t</c> in <em>unmanaged</em> memory.
/// </summary>
/// <remarks>
/// The flag must have a stable address for the entire duration of a
/// <c>sha_hash_file()</c> call (minutes for large inputs), and the native
/// side reads it from the hashing thread while the UI thread sets it. A
/// managed heap object would be both movable and subject to torn reads
/// without the DLL's InterlockedExchange, so the storage is allocated with
/// <see cref="Marshal.AllocHGlobal"/> and mutated only through the DLL's
/// exported functions — never with <see cref="Marshal.WriteInt32"/>, which
/// has no ordering guarantee against a concurrent native read.
/// </remarks>
public sealed class ShaCancelSource : IDisposable
{
    private IntPtr _native;
    private bool _disposed;

    public ShaCancelSource()
    {
        // sizeof(sha_cancel_t) == sizeof(volatile long) == 4 on Windows;
        // allocate 8 so the slot is over-sized even if the C type changes.
        _native = Marshal.AllocHGlobal(8);
        Marshal.WriteInt32(_native, 0);
    }

    /// <summary>Address to hand to native code, or <see cref="IntPtr.Zero"/> after disposal.</summary>
    internal IntPtr DangerousGetHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _native;
    }

    /// <summary>Requests cancellation. Safe to call from any thread; idempotent.</summary>
    public void Cancel()
    {
        if (_disposed)
        {
            return;   // cancelling a finished operation is not an error
        }
        NativeMethods.sha_cancel_request(_native);
    }

    /// <summary>Clears a previous request so the source can be reused.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeMethods.sha_cancel_clear(_native);
    }

    /// <summary>Snapshot of the flag; read through the DLL for ordering.</summary>
    public bool IsCancellationRequested =>
        !_disposed && NativeMethods.sha_cancelled(_native) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~ShaCancelSource()
    {
        // Best-effort: a missed free here is 8 bytes, and touching the CRT
        // from the finalizer thread during shutdown is worse than leaking.
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }
    }
}
