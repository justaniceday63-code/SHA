using System.Diagnostics;
using ShaGui.Core.Native;

namespace ShaGui.Core.Hashing;

/// <summary>
/// Public entry points for hashing text, buffers and files through
/// <c>shaNative.dll</c>. Folder recursion, manifests and archives are
/// layered on top of these in <c>ShaGui.Core.Storage</c> (managed side
/// owns enumeration; C owns only the read+hash loop).
/// </summary>
public static class ShaHasher
{
    /// <summary>Default read buffer, mirrored from <c>SHA_DEFAULT_BUFFER_SIZE</c>.</summary>
    public const ulong DefaultBufferSize = 1u << 20;

    /// <summary>Native ABI version, asserted once at startup by the app.</summary>
    public static uint AbiVersion => NativeMethods.sha_abi_version();

    /// <summary>Validates that the loaded DLL matches what this build expects.</summary>
    public static void EnsureAbi(uint expected = 1)
    {
        uint actual = NativeMethods.sha_abi_version();
        if (actual != expected)
        {
            throw new ShaNativeException(
                ShaStatus.Unsupported,
                $"native ABI version mismatch: expected {expected}, found {actual}");
        }
    }

    public static int DigestLength(ShaAlgo algo) => (int)NativeMethods.sha_digest_length(algo);

    public static string AlgorithmName(ShaAlgo algo)
        => NativeMethods.ReadCString(NativeMethods.sha_algo_name(algo));

    // ------------------------------------------------------------ in-memory

    /// <summary>One-shot hash of a byte buffer.</summary>
    public static HashResult Hash(ShaAlgo algo, ReadOnlySpan<byte> data)
    {
        var sw = Stopwatch.StartNew();
        byte[] digest = new byte[DigestLength(algo)];
        var status = NativeMethods.sha_hash_buffer(
            algo, data, (ulong)data.Length, digest, (nuint)digest.Length, out nuint written);
        sw.Stop();

        if (status != ShaStatus.Ok)
        {
            throw new ShaNativeException(status, "sha_hash_buffer failed");
        }
        if (written != (nuint)digest.Length)
        {
            throw new ShaNativeException(ShaStatus.State,
                $"digest length mismatch: wrote {written}, expected {digest.Length}");
        }
        return HashResult.FromNative(algo, digest, sw.Elapsed);
    }

    /// <summary>Hashs UTF-8 text. Encoding is explicit so results are reproducible across machines.</summary>
    public static HashResult HashText(ShaAlgo algo, string text)
        => Hash(algo, HashResult.Utf8(text));

    /// <summary>Hashs a string after UTF-8 encoding — convenience for tests and the text tab.</summary>
    public static HashResult HashString(ShaAlgo algo, string text)
        => HashText(algo, text);

    // ----------------------------------------------------------------- file

    /// <summary>
    /// Streams a file through the native read+hash loop with progress and
    /// cancellation. Expected non-success outcomes (missing file, access
    /// denied, changed mid-read, cancelled) are returned as
    /// <see cref="HashResult.Status"/>, not thrown, so the Verify UI can
    /// render them directly.
    /// </summary>
    public static HashResult HashFile(string path, ShaAlgo algo, HashFileOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new HashFileOptions();

        byte[] digest = new byte[DigestLength(algo)];

        // Bridge CancellationToken -> native cancel object. A source is
        // created only when something might actually cancel, so the common
        // path allocates no unmanaged memory.
        ShaCancelSource? ownedCancel = null;
        ShaCancelSource? cancel = options.Cancel;
        CancellationTokenRegistration registration = default;
        if (cancel is null && options.CancellationToken.CanBeCanceled)
        {
            ownedCancel = new ShaCancelSource();
            cancel = ownedCancel;
            registration = options.CancellationToken.Register(static s =>
                ((ShaCancelSource)s!).Cancel(), ownedCancel);
        }

        // The delegate must stay alive across the P/Invoke; a field + KeepAlive
        // below is the documented pattern (a closure local alone is not
        // guaranteed to survive a long native call).
        NativeMethods.ProgressCallback? callback = null;
        try
        {
            if (options.Progress is not null || options.ShouldCancel is not null ||
                options.CancellationToken.CanBeCanceled)
            {
                var sink = options;
                callback = (in ShaProgress p, IntPtr user) =>
                {
                    sink.Progress?.Invoke(new HashProgress
                    {
                        Stage = p.Stage,
                        BytesDone = p.FileBytesDone,
                        BytesTotal = p.FileBytesTotal,
                        Path = p.CurrentPath.GetString(),
                        PathTruncated = p.IsPathTruncated,
                    });
                    // Non-zero return cancels the run natively.
                    return sink.ShouldStop() ? 1 : 0;
                };
            }

            IntPtr cancelPtr = cancel?.DangerousGetHandle() ?? IntPtr.Zero;

            var status = NativeMethods.sha_hash_file(
                path, algo, options.BufferSize,
                callback, IntPtr.Zero, cancelPtr,
                digest, (nuint)digest.Length, out ulong elapsed100ns);

            GC.KeepAlive(callback);
            GC.KeepAlive(cancel);

            if (status is ShaStatus.Cancelled or ShaStatus.Io or ShaStatus.Access
                or ShaStatus.Changed or ShaStatus.Utf8 or ShaStatus.PathTooLong)
            {
                // Expected outcomes: report, do not throw. One TimeSpan tick
                // == 100 ns, which is exactly the native unit.
                return HashResult.FromNative(
                    algo, Array.Empty<byte>(),
                    TimeSpan.FromTicks((long)elapsed100ns), status);
            }
            if (status != ShaStatus.Ok)
            {
                throw new ShaNativeException(status, "sha_hash_file failed");
            }

            return HashResult.FromNative(
                algo, digest, TimeSpan.FromTicks((long)elapsed100ns));
        }
        finally
        {
            registration.Dispose();
            ownedCancel?.Dispose();
        }
    }

    /// <summary>Async wrapper: runs <see cref="HashFile"/> on the thread pool.</summary>
    public static Task<HashResult> HashFileAsync(
        string path, ShaAlgo algo, HashFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Link the caller's token with any token already in the options.
        if (cancellationToken.CanBeCanceled &&
            options?.CancellationToken == default)
        {
            options = new HashFileOptions
            {
                BufferSize = options?.BufferSize ?? 0,
                Progress = options?.Progress,
                Cancel = options?.Cancel,
                ShouldCancel = options?.ShouldCancel,
                CancellationToken = cancellationToken,
            };
        }
        else if (cancellationToken.CanBeCanceled && options is null)
        {
            options = new HashFileOptions { CancellationToken = cancellationToken };
        }

        return Task.Run(() => HashFile(path, algo, options), CancellationToken.None);
    }
}
