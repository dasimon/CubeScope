using Microsoft.Data.Sqlite;

namespace CubeScope.Core.State;

public sealed record RecentConnection(string Server, string? Catalog, DateTime LastUsedUtc);

public sealed record HistoryEntry(long Id, string Server, string? Catalog, string Mdx,
    bool Success, long DurationMs, int CellCount, string? Error, DateTime ExecutedUtc);

/// <summary>
/// État local dans UN fichier SQLite (décision actée : pas de fichiers de config éparpillés).
/// Par défaut : %LOCALAPPDATA%\CubeScope\cubescope.db. Migrations par PRAGMA user_version.
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CubeScope", "cubescope.db");

    public StateStore(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Migrate();
    }

    private void Migrate()
    {
        long version = (long)Scalar("PRAGMA user_version")!;
        if (version < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS RecentConnection (
                    Server      TEXT NOT NULL,
                    -- '' = pas de catalogue : un NULL dans une PK composite SQLite ne déclenche
                    -- jamais ON CONFLICT (NULL <> NULL) et ferait des doublons
                    Catalog     TEXT NOT NULL DEFAULT '',
                    LastUsedUtc TEXT NOT NULL,
                    PRIMARY KEY (Server, Catalog)
                );
                CREATE TABLE IF NOT EXISTS QueryHistory (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Server      TEXT NOT NULL,
                    Catalog     TEXT NULL,
                    Mdx         TEXT NOT NULL,
                    Success     INTEGER NOT NULL,
                    DurationMs  INTEGER NOT NULL,
                    CellCount   INTEGER NOT NULL,
                    Error       TEXT NULL,
                    ExecutedUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_QueryHistory_ExecutedUtc ON QueryHistory (ExecutedUtc DESC);
                PRAGMA user_version = 1;
                """);
        }
    }

    public void AddRecentConnection(string server, string? catalog)
    {
        Exec("""
            INSERT INTO RecentConnection (Server, Catalog, LastUsedUtc) VALUES ($s, $c, $t)
            ON CONFLICT (Server, Catalog) DO UPDATE SET LastUsedUtc = $t
            """,
            ("$s", server), ("$c", catalog ?? ""), ("$t", DateTime.UtcNow.ToString("O")));
    }

    public IReadOnlyList<RecentConnection> GetRecentConnections(int limit = 10)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT Server, Catalog, LastUsedUtc FROM RecentConnection ORDER BY LastUsedUtc DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<RecentConnection>();
            while (r.Read())
                list.Add(new RecentConnection(r.GetString(0), r.GetString(1) is "" ? null : r.GetString(1),
                    DateTime.Parse(r.GetString(2)).ToUniversalTime()));
            return list;
        }
    }

    public void AddHistory(string server, string? catalog, string mdx, bool success,
        long durationMs, int cellCount, string? error)
    {
        Exec("""
            INSERT INTO QueryHistory (Server, Catalog, Mdx, Success, DurationMs, CellCount, Error, ExecutedUtc)
            VALUES ($s, $c, $m, $ok, $d, $n, $e, $t)
            """,
            ("$s", server), ("$c", (object?)catalog ?? DBNull.Value), ("$m", mdx), ("$ok", success ? 1 : 0),
            ("$d", durationMs), ("$n", cellCount), ("$e", (object?)error ?? DBNull.Value),
            ("$t", DateTime.UtcNow.ToString("O")));
    }

    public IReadOnlyList<HistoryEntry> GetHistory(int limit = 100)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Server, Catalog, Mdx, Success, DurationMs, CellCount, Error, ExecutedUtc
                FROM QueryHistory ORDER BY Id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (r.Read())
                list.Add(new HistoryEntry(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), (int)r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetString(7), DateTime.Parse(r.GetString(8)).ToUniversalTime()));
            return list;
        }
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }

    public void Dispose() => _db.Dispose();
}
