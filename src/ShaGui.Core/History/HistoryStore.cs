using System.Globalization;
using Microsoft.Data.Sqlite;
using ShaGui.Core.Native;

namespace ShaGui.Core.History;

/// <summary>
/// Reads and writes the <c>runs</c> table. Backed by the connection owned
/// by <see cref="Data.AppDatabase"/>; not independently disposable.
/// </summary>
/// <remarks>
/// Timestamps are stored as ISO-8601 round-trip ("o") text and parsed with
/// <see cref="CultureInfo.InvariantCulture"/> so a database file is
/// portable and a run's time is unambiguous regardless of the machine's
/// locale.
/// </remarks>
public sealed class HistoryStore
{
    private readonly SqliteConnection _connection;
    private readonly object _sync;

    internal HistoryStore(SqliteConnection connection, object sync)
    {
        _connection = connection;
        _sync = sync;
    }

    /// <summary>Number of recorded runs.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM runs;";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Inserts a run and returns the assigned row id.</summary>
    public long Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO runs
                    (recorded_utc, algo, kind, subject, bytes, elapsed_ticks, digest, complete, status)
                VALUES
                    ($recorded, $algo, $kind, $subject, $bytes, $elapsed, $digest, $complete, $status);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$recorded", entry.RecordedUtc.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$algo", (int)entry.Algorithm);
            cmd.Parameters.AddWithValue("$kind", (int)entry.Kind);
            cmd.Parameters.AddWithValue("$subject", entry.Subject);
            cmd.Parameters.AddWithValue("$bytes", entry.Bytes);
            cmd.Parameters.AddWithValue("$elapsed", entry.Elapsed.Ticks);
            cmd.Parameters.AddWithValue("$digest", entry.Digest);
            cmd.Parameters.AddWithValue("$complete", entry.Complete ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", (int)entry.Status);

            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Most recent runs first, newest by recorded time then by insert order.</summary>
    public IReadOnlyList<HistoryEntry> Recent(int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, recorded_utc, algo, kind, subject, bytes, elapsed_ticks, digest, complete, status
                FROM runs
                ORDER BY recorded_utc DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            var rows = new List<HistoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Read(reader));
            }
            return rows;
        }
    }

    /// <summary>Removes every recorded run.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM runs;";
            cmd.ExecuteNonQuery();
        }
    }

    private static HistoryEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        RecordedUtc = DateTimeOffset.Parse(
            r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Algorithm = (ShaAlgo)r.GetInt32(2),
        Kind = (RunKind)r.GetInt32(3),
        Subject = r.GetString(4),
        Bytes = r.GetInt64(5),
        Elapsed = TimeSpan.FromTicks(r.GetInt64(6)),
        Digest = r.GetString(7),
        Complete = r.GetInt32(8) != 0,
        Status = (ShaStatus)r.GetInt32(9),
    };
}
