namespace ShaGui.Core.Native;

/// <summary>
/// Thrown when the native backend reports a failure that is not part of the
/// normal verify/batch flow. Expected outcomes (missing file, cancellation,
/// "changed while hashing") are returned as <see cref="ShaStatus"/> values
/// by the hashing API instead of being thrown, so the UI can render them.
/// </summary>
public sealed class ShaNativeException : Exception
{
    public ShaStatus Status { get; }

    public ShaNativeException(ShaStatus status, string message)
        : base($"{message} ({status}: {Describe(status)})")
    {
        Status = status;
    }

    public ShaNativeException(ShaStatus status, string message, Exception inner)
        : base($"{message} ({status}: {Describe(status)})", inner)
    {
        Status = status;
    }

    private static string Describe(ShaStatus status) => status switch
    {
        ShaStatus.Ok => "OK",
        ShaStatus.InvalidArg => "invalid argument",
        ShaStatus.Unsupported => "unsupported algorithm",
        ShaStatus.Io => "I/O error",
        ShaStatus.Cancelled => "cancelled",
        ShaStatus.Changed => "file changed while hashing",
        ShaStatus.Access => "access denied",
        ShaStatus.NoMem => "out of memory",
        ShaStatus.State => "invalid context state",
        ShaStatus.Overflow => "message too long",
        ShaStatus.TooLargeForTrace => "input too large to trace",
        ShaStatus.PathTooLong => "path too long",
        ShaStatus.Utf8 => "invalid UTF-8 path",
        _ => "unknown status",
    };
}
