using System.Runtime.CompilerServices;

namespace ShaGui.App;

/// <summary>
/// Temporary startup diagnostics for the portable single-file build.
/// Writes every stage and any unhandled exception to
/// %TEMP%\shagui-diag.log so a crash before/inside the WinUI startup can be
/// diagnosed (the portable exe died with exit 0xC000027B and no WER entry).
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "shagui-diag.log");

    /// <summary>Runs when ShaGui.App.dll is loaded — before generated Main.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log($"UNHANDLED(AppDomain): {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                Log($"UNHANDLED(Task): {e.Exception}");

            Log($"module-init: exe={Environment.ProcessPath}");
            Log($"  baseDir={AppContext.BaseDirectory}");
            Log($"  cwd={Environment.CurrentDirectory}");
        }
        catch
        {
            // Diagnostics must never be the thing that crashes the app.
        }
    }

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
            catch
            {
                // ignore
            }
        }
    }
}
