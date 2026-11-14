using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShaGui.Core.Settings;

/// <summary>
/// Key/value persistence for <see cref="AppSettings"/> on top of the
/// connection owned by <see cref="Data.AppDatabase"/>. The payload is a
/// single JSON document under one key, which keeps the schema trivial and
/// lets new options be added without a migration.
/// </summary>
public sealed class SettingsStore
{
    private const string SettingsKey = "app";

    private readonly SqliteConnection _connection;
    private readonly object _sync;

    internal SettingsStore(SqliteConnection connection, object sync)
    {
        _connection = connection;
        _sync = sync;
    }

    /// <summary>Loads the stored settings, or defaults when nothing has been saved.</summary>
    public AppSettings Load()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", SettingsKey);

            if (cmd.ExecuteScalar() is not string json || json.Length == 0)
            {
                return new AppSettings();
            }

            try
            {
                // Source-generated contract: reflection serialization is
                // compiled out of the trimmed portable build.
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // A corrupt or older payload must not stop the app from
                // starting; fall back to defaults rather than throwing.
                return new AppSettings();
            }
        }
    }

    /// <summary>Inserts or replaces the stored settings.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", SettingsKey);
            cmd.Parameters.AddWithValue("$value", json);
            cmd.ExecuteNonQuery();
        }
    }
}
