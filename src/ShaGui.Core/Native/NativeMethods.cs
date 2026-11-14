using System.Runtime.InteropServices;

namespace ShaGui.Core.Native;

/// <summary>
/// Raw P/Invoke declarations for <c>shaNative.dll</c>. Mirrors
/// <c>native/include/shagui_native.h</c> one-for-one; nothing else in the
/// codebase may declare imports of this library.
/// </summary>
/// <remarks>
/// Calling convention is cdecl, declared explicitly. On x64 Windows there is
/// effectively one convention, but stating it keeps the intent visible and
/// protects the boundary if x86 is ever added.
/// All <c>char*</c> parameters and returns are UTF-8 — the header mandates
/// it. Returned strings are static DLL storage; the marshaller copies them,
/// which is exactly what we want (never freed, never cached by us).
/// </remarks>
internal static partial class NativeMethods
{
    internal const string DllName = "shaNative";

    // ---------------------------------------------------------------- core

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ShaStatus sha_create(ShaAlgo algo, out IntPtr ctx);

    [LibraryImport(DllName)]
    internal static partial void sha_destroy(IntPtr ctx);

    [LibraryImport(DllName)]
    internal static partial ShaStatus sha_reset(IntPtr ctx);

    [LibraryImport(DllName)]
    internal static partial ShaStatus sha_update(IntPtr ctx, ReadOnlySpan<byte> data, ulong len);

    [LibraryImport(DllName)]
    internal static partial ShaStatus sha_final(IntPtr ctx, Span<byte> outDigest, nuint cap, out nuint written);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ShaStatus sha_hash_buffer(
        ShaAlgo algo, ReadOnlySpan<byte> data, ulong len,
        Span<byte> outDigest, nuint cap, out nuint written);

    [LibraryImport(DllName)]
    internal static partial nuint sha_digest_length(ShaAlgo algo);

    // These two return static DLL-owned storage (never freed, per header).
    // They are declared as IntPtr rather than string on purpose: a marshalled
    // string return has ambiguous ownership, and we will not risk the runtime
    // attempting to free a pointer into the DLL's read-only data. Conversion
    // happens once, explicitly, in ReadCString.
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr sha_algo_name(ShaAlgo algo);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr sha_status_string(ShaStatus status);

    [LibraryImport(DllName)]
    internal static partial uint sha_abi_version();

    /// <summary>Reads a C <c>const char*</c> returned by the DLL (static storage, never freed).</summary>
    internal static string ReadCString(IntPtr ptr)
        => ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;

    // -------------------------------------------------------- cancellation

    // The cancel object is caller-allocated plain storage, but its address
    // must stay stable for the entire sha_hash_file() call (which can run
    // for minutes), so it lives in unmanaged memory owned by
    // ShaCancelSource — never on the managed heap, which the GC may move.

    [LibraryImport(DllName)]
    internal static partial void sha_cancel_request(IntPtr cancel);

    [LibraryImport(DllName)]
    internal static partial void sha_cancel_clear(IntPtr cancel);

    [LibraryImport(DllName)]
    internal static partial int sha_cancelled(IntPtr cancel);

    // -------------------------------------------------------------- file

    /// <param name="cancel">sha_cancel_t* or <see cref="IntPtr.Zero"/>.</param>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ShaStatus sha_hash_file(
        string utf8Path, ShaAlgo algo, ulong bufSize,
        ProgressCallback? cb, IntPtr cbUser,
        IntPtr cancel,
        Span<byte> outDigest, nuint cap,
        out ulong elapsed100ns);

    /// <summary>
    /// Native progress hook. Return non-zero to cancel. The delegate must be
    /// kept alive for the whole call — the wrapper holds a field reference
    /// and calls <c>GC.KeepAlive</c> after the import returns.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(in ShaProgress progress, IntPtr user);

    // ------------------------------------------------------------- trace

    [LibraryImport(DllName)]
    internal static partial ShaStatus sha_trace_configure(
        IntPtr ctx, int enable, uint firstBlock, uint blockCount);

    [LibraryImport(DllName)]
    internal static partial nuint sha_trace_count(IntPtr ctx);

    /// <summary>Contiguous array owned by the DLL; valid until the next trace call or <c>sha_destroy</c>.</summary>
    [LibraryImport(DllName)]
    internal static partial IntPtr sha_trace_data(IntPtr ctx);

    [LibraryImport(DllName)]
    internal static partial ShaStatus sha_trace_get(IntPtr ctx, nuint index, out ShaTraceStep step);
}
