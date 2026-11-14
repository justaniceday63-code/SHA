using ShaGui.Core.Hashing;

namespace ShaGui.App.Services;

/// <summary>
/// Snapshot of native-backend health, probed once at startup and shown on
/// the Home and About pages. A missing/wrong DLL must be loud: hashing would
/// otherwise fail with DllNotFoundException deep inside a worker thread.
/// </summary>
public sealed record NativeHealth(bool IsAvailable, uint AbiVersion, string Detail)
{
    public static NativeHealth Probe()
    {
        try
        {
            ShaHasher.EnsureAbi();
            return new NativeHealth(
                true,
                ShaHasher.AbiVersion,
                $"shaNative.dll loaded; ABI v{ShaHasher.AbiVersion} matches");
        }
        catch (Exception ex)
        {
            return new NativeHealth(false, 0, ex.Message);
        }
    }
}
