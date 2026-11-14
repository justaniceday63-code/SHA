using Microsoft.Data.Sqlite;
using ShaGui.Core.History;
using ShaGui.Core.Settings;

namespace ShaGui.Core.Data;

/// <summary>
/// Owns the single SQLite connection behind the app's local state: the run
/// history and the user settings. One connection (no shared cache, no pool
/// of writers) keeps the <c>:memory:</c> form valid for tests and matches a
/// single-user desktop app where writes are rare and short.
/// </summary>
/// <remarks>
/// All statements from the <see cref="History"/> and <see cref="Settings"/>
/// stores are serialized on <see cref="SyncRoot"/>, because
/// <see cref="SqliteConnection"/> is not safe for concurrent use and a run
/// finished on a worker thread may add a row while the History page reads.
/// </remarks>
public sealed class AppDatabase : IDisposable
{
    /// <summary>Connection string data source that keeps the database in memory (tests only).</summary>
    public const string MemoryPath = ":memory:";

    private readonly SqliteConnection _connection;
    private bool _disposed;

    public AppDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        DatabasePath = databasePath == MemoryPath
            ? databasePath
            : Path.GetFullPath(databasePath);

        if (DatabasePath != MemoryPath)
        {
            string? dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        };

        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();

        if (DatabasePath != MemoryPath)
        {
            // WAL keeps the History page's read from blocking a finished
            // run's write without the app serializing them itself. It is not
            // available for an in-memory database.
            ExecuteScalar("PRAGMA journal_mode=WAL;");
        }

        EnsureSchema();

        History = new HistoryStore(_connection, SyncRoot);
        Settings = new SettingsStore(_connection, SyncRoot);
    }

    /// <summary>Absolute path of the database file, or <see cref="MemoryPath"/>.</summary>
    public string DatabasePath { get; }

    /// <summary>Run history (one row per completed hashing run).</summary>
    public HistoryStore History { get; }

    /// <summary>Persisted user settings.</summary>
    public SettingsStore Settings { get; }

    /// <summary>Guard for every statement issued through this database.</summary>
    internal object SyncRoot { get; } = new();

    /// <summary>
    /// Where the app keeps its database by default. Packaged runs get the
    /// package's local folder; anything else (tests, unpackaged dev) falls
    /// back to <c>%LOCALAPPDATA%\ShaGui</c>.
    /// </summary>
    public static string DefaultPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShaGui",
            "shagui.db");

    private void EnsureSchema()
    {
        ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS runs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_utc  TEXT    NOT NULL,
                algo          INTEGER NOT NULL,
                kind          INTEGER NOT NULL,
                subject       TEXT    NOT NULL,
                bytes         INTEGER NOT NULL,
                elapsed_ticks INTEGER NOT NULL,
                digest        TEXT    NOT NULL,
                complete      INTEGER NOT NULL,
                status        INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery("CREATE INDEX IF NOT EXISTS ix_runs_recorded ON runs(recorded_utc DESC);");
        ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    private void ExecuteNonQuery(string sql)
    {
        lock (SyncRoot)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private void ExecuteScalar(string sql)
    {
        lock (SyncRoot)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteScalar();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (SyncRoot)
        {
            _connection.Dispose();
        }
    }
}
