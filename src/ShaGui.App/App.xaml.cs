using Microsoft.UI.Xaml;
using ShaGui.App.Services;
using ShaGui.Core.Data;
using ShaGui.Core.Settings;

namespace ShaGui.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>The single main window, for pages that need to drive shell navigation.</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>Native-backend status, probed once here so pages can render it without P/Invoke churn.</summary>
    public static NativeHealth NativeHealth { get; private set; } = new(false, 0, "not probed");

    /// <summary>
    /// Local SQLite state (history + settings), or null when the database
    /// could not be opened. Pages must tolerate null: hashing works without
    /// history, so a locked/corrupt database must never stop the app.
    /// </summary>
    public static AppDatabase? Database { get; private set; }

    /// <summary>Current user settings; defaults until <see cref="Database"/> loads them.</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Why <see cref="Database"/> is null, for the History/Settings pages to display.</summary>
    public static string? DatabaseError { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Diag.Log("App.ctor: enter");
        UnhandledException += (_, e) =>
        {
            Diag.Log($"UNHANDLED(WinUI): {e.Message}\n{e.Exception}");
            e.Handled = false;
        };
        InitializeComponent();
        Diag.Log("App.ctor: InitializeComponent done");
        NativeHealth = NativeHealth.Probe();
        Diag.Log($"App.ctor: native probe done ({NativeHealth})");
        OpenDatabase();
        Diag.Log("App.ctor: database open done");
    }

    /// <summary>Invoked when the application is launched.</summary>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Diag.Log("OnLaunched: enter");
        var window = new MainWindow();
        Diag.Log("MainWindow: constructed");
        MainWindow = window;
        _window = window;
        window.Activate();
        Diag.Log("OnLaunched: window activated");
    }

    /// <summary>
    /// Persists settings after a page edits them, keeping the in-memory copy
    /// authoritative even when storage is unavailable.
    /// </summary>
    public static void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        try
        {
            Database?.Settings.Save(settings);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }

    private void OpenDatabase()
    {
        try
        {
            Database = new AppDatabase(ResolveDatabasePath());
            Settings = Database.Settings.Load();
            DatabaseError = null;
        }
        catch (Exception ex)
        {
            Database = null;
            Settings = new AppSettings();
            DatabaseError = ex.Message;
        }
    }

    /// <summary>
    /// Packaged runs keep the database in the package's local folder;
    /// unpackaged runs (and a missing package identity) fall back to
    /// <c>%LOCALAPPDATA%\ShaGui</c>.
    /// </summary>
    private static string ResolveDatabasePath()
    {
        try
        {
            string local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(local, "shagui.db");
        }
        catch
        {
            return AppDatabase.DefaultPath;
        }
    }
}
