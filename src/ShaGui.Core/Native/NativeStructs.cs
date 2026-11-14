using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ShaGui.Core.Native;

/// <summary>Fixed-capacity inline array of eight 32-bit words (A..H or W[0..7]).</summary>
/// <remarks>
/// .NET 8 inline arrays are blittable, so <see cref="ShaTraceStep"/> stays
/// blittable and can be read straight out of DLL-owned memory as a span —
/// no per-field marshalling and no copy per step.
/// </remarks>
[InlineArray(8)]
public struct Word8
{
    private uint _element0;

    /// <summary>Convenience: copies the eight words into a new array.</summary>
    public uint[] ToArray()
    {
        var result = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            result[i] = this[i];
        }
        return result;
    }
}

/// <summary>Fixed-capacity inline array of UTF-8 path bytes (not NUL-terminated by contract).</summary>
[InlineArray(PathCapacity)]
public struct Utf8Path
{
    public const int PathCapacity = 1024;
    private byte _element0;

    /// <summary>Decodes the buffer up to the first NUL byte as UTF-8.</summary>
    public readonly string GetString()
    {
        ReadOnlySpan<byte> span = this;
        int end = span.IndexOf((byte)0);
        if (end < 0)
        {
            end = span.Length;
        }
        return Encoding.UTF8.GetString(span[..end]);
    }
}

/// <summary>Progress report passed to the native progress callback. Mirrors <c>sha_progress_t</c> (1072 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ShaProgress
{
    /// <summary>Caller sets this to <c>sizeof(sha_progress_t)</c> before the first call.</summary>
    public uint StructSize;
    public ShaStage Stage;
    public ulong FileBytesDone;
    public ulong FileBytesTotal;
    public ulong BytesDone;
    public ulong BytesTotal;
    public uint PathTruncated;
    public uint Reserved;
    public Utf8Path CurrentPath;

    /// <summary>True when the native side had to cut the path for the fixed buffer.</summary>
    public readonly bool IsPathTruncated => PathTruncated != 0;
}

/// <summary>One visualization step. Mirrors <c>sha_trace_step_t</c> — exactly 128 bytes on x64.</summary>
/// <remarks>
/// Frozen at <c>SHAGUI_ABI_VERSION 1</c>; the native tests and the managed
/// layout test both assert the size so a drift fails the build rather than
/// silently corrupting the animation.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct ShaTraceStep
{
    /// <summary><see cref="ShaTraceStage"/> value.</summary>
    public uint Stage;
    /// <summary>0-based block index this step belongs to.</summary>
    public uint Block;
    /// <summary>Round 0..79 / 0..63, else <see cref="NoIndex"/>.</summary>
    public uint Round;
    /// <summary>Word index the step refers to, else <see cref="NoIndex"/>.</summary>
    public uint WIndex;
    /// <summary>A..H <em>after</em> this step (SHA-1 uses [0..4]).</summary>
    public Word8 Words;
    /// <summary>A..H <em>before</em> this step.</summary>
    public Word8 WordsBefore;
    /// <summary>Stage-dependent operands; see the header comment in shagui_native.h.</summary>
    public Word8 Aux;
    /// <summary>Stage 2/7: original message length in bits.</summary>
    public ulong MsgBits;
    /// <summary>Stage 2: padding bytes written.</summary>
    public uint PadLen;
    public uint Flags;

    /// <summary>Sentinel the native side uses for "not applicable".</summary>
    public const uint NoIndex = 0xFFFFFFFFu;

    public ShaTraceStage TraceStage => (ShaTraceStage)Stage;
    public bool IsLastRound => Round == NoIndex;

    /// <summary>Reinterpret-casts the struct to a span over native memory (caller must keep the memory alive).</summary>
    internal static unsafe ReadOnlySpan<ShaTraceStep> FromNative(void* ptr, int count)
        => new(ptr, count);
}
