using CubeScope.Core.Models;
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
        if (version < 2)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS RecentProject (
                    Path        TEXT NOT NULL PRIMARY KEY,
                    LastUsedUtc TEXT NOT NULL
                );
                PRAGMA user_version = 2;
                """);
        }
        if (version < 3)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS Snippet (
                    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name       TEXT NOT NULL,
                    Mdx        TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );
                PRAGMA user_version = 3;
                """);
        }
        if (version < 4)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS ProfileRun (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Server TEXT NOT NULL, Catalog TEXT NULL, Mdx TEXT NOT NULL,
                    TotalMs INTEGER NOT NULL, StorageEngineMs INTEGER NOT NULL, FormulaEngineMs INTEGER NOT NULL,
                    SubcubeCount INTEGER NOT NULL, CacheHits INTEGER NOT NULL, AggregationHits INTEGER NOT NULL,
                    ExecutedUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ProfileRun_ExecutedUtc ON ProfileRun (ExecutedUtc DESC);
                PRAGMA user_version = 4;
                """);
        }
        if (version < 5)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS DeployLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Server TEXT NOT NULL, Catalog TEXT NULL, CubeName TEXT NOT NULL, ProjectPath TEXT NOT NULL,
                    ScriptChars INTEGER NOT NULL, Forced INTEGER NOT NULL, DeployedUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_DeployLog_DeployedUtc ON DeployLog (DeployedUtc DESC);
                PRAGMA user_version = 5;
                """);
        }
        if (version < 6)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS RegressionCase (
                    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name         TEXT NOT NULL,
                    Mdx          TEXT NOT NULL,
                    ExpectedJson TEXT NOT NULL,
                    CreatedUtc   TEXT NOT NULL
                );
                PRAGMA user_version = 6;
                """);
        }
        if (version < 7)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS MemberCaption (
                    Server TEXT NOT NULL, Catalog TEXT NOT NULL, Cube TEXT NOT NULL,
                    UniqueName TEXT NOT NULL, Caption TEXT NOT NULL,
                    PRIMARY KEY (Server, Catalog, Cube, UniqueName)
                );
                CREATE TABLE IF NOT EXISTS CaptionStamp (
                    Server TEXT NOT NULL, Catalog TEXT NOT NULL, Cube TEXT NOT NULL,
                    Stamp TEXT NOT NULL, PRIMARY KEY (Server, Catalog, Cube)
                );
                PRAGMA user_version = 7;
                """);
        }
    }

    // --- Cache persistant des captions de membres (v7) : évite de re-DMV chaque membre
    // référencé à chaque session. Invalidé quand le cube a été reprocessé (stamp).

    /// <summary>Captions en cache pour les <paramref name="names"/> demandés (seulement ceux
    /// trouvés). Le IN est découpé à ≤ 500 paramètres (limite de variables SQLite).</summary>
    public IReadOnlyDictionary<string, string> GetCachedCaptions(
        string server, string catalog, string cube, IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, string>();
        if (names.Count == 0) return result;
        lock (_lock)
        {
            const int chunkSize = 500;
            var list = names as IList<string> ?? names.ToList();
            for (int offset = 0; offset < list.Count; offset += chunkSize)
            {
                int count = Math.Min(chunkSize, list.Count - offset);
                using var cmd = _db.CreateCommand();
                var placeholders = new string[count];
                for (int i = 0; i < count; i++)
                {
                    placeholders[i] = $"$n{i}";
                    cmd.Parameters.AddWithValue($"$n{i}", list[offset + i]);
                }
                cmd.CommandText =
                    "SELECT UniqueName, Caption FROM MemberCaption " +
                    "WHERE Server = $s AND Catalog = $c AND Cube = $cube " +
                    $"AND UniqueName IN ({string.Join(",", placeholders)})";
                cmd.Parameters.AddWithValue("$s", server);
                cmd.Parameters.AddWithValue("$c", catalog);
                cmd.Parameters.AddWithValue("$cube", cube);
                using var r = cmd.ExecuteReader();
                while (r.Read()) result[r.GetString(0)] = r.GetString(1);
            }
        }
        return result;
    }

    /// <summary>Insère/remplace les captions fournies (une seule transaction, sous verrou).</summary>
    public void PutCachedCaptions(
        string server, string catalog, string cube, IReadOnlyDictionary<string, string> captions)
    {
        if (captions.Count == 0) return;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO MemberCaption (Server, Catalog, Cube, UniqueName, Caption) " +
                "VALUES ($s, $c, $cube, $u, $cap)";
            var pS = cmd.Parameters.Add("$s", SqliteType.Text);
            var pC = cmd.Parameters.Add("$c", SqliteType.Text);
            var pCube = cmd.Parameters.Add("$cube", SqliteType.Text);
            var pU = cmd.Parameters.Add("$u", SqliteType.Text);
            var pCap = cmd.Parameters.Add("$cap", SqliteType.Text);
            pS.Value = server; pC.Value = catalog; pCube.Value = cube;
            foreach (var (name, caption) in captions)
            {
                pU.Value = name;
                pCap.Value = caption;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public string? GetCaptionStamp(string server, string catalog, string cube)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT Stamp FROM CaptionStamp WHERE Server = $s AND Catalog = $c AND Cube = $cube";
            cmd.Parameters.AddWithValue("$s", server);
            cmd.Parameters.AddWithValue("$c", catalog);
            cmd.Parameters.AddWithValue("$cube", cube);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetCaptionStamp(string server, string catalog, string cube, string stamp) =>
        Exec("""
            INSERT INTO CaptionStamp (Server, Catalog, Cube, Stamp) VALUES ($s, $c, $cube, $st)
            ON CONFLICT (Server, Catalog, Cube) DO UPDATE SET Stamp = $st
            """,
            ("$s", server), ("$c", catalog), ("$cube", cube), ("$st", stamp));

    /// <summary>Vide le cache de captions ET le stamp d'un cube (reprocessing / refresh manuel).</summary>
    public void InvalidateCubeCaptions(string server, string catalog, string cube)
    {
        Exec("DELETE FROM MemberCaption WHERE Server = $s AND Catalog = $c AND Cube = $cube",
            ("$s", server), ("$c", catalog), ("$cube", cube));
        Exec("DELETE FROM CaptionStamp WHERE Server = $s AND Catalog = $c AND Cube = $cube",
            ("$s", server), ("$c", catalog), ("$cube", cube));
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

    public void AddRecentProject(string path)
    {
        Exec("""
            INSERT INTO RecentProject (Path, LastUsedUtc) VALUES ($p, $t)
            ON CONFLICT (Path) DO UPDATE SET LastUsedUtc = $t
            """,
            ("$p", path), ("$t", DateTime.UtcNow.ToString("O")));
    }

    public IReadOnlyList<RecentProject> GetRecentProjects(int limit = 10)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT Path, LastUsedUtc FROM RecentProject ORDER BY LastUsedUtc DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<RecentProject>();
            while (r.Read())
                list.Add(new RecentProject(r.GetString(0), DateTime.Parse(r.GetString(1)).ToUniversalTime()));
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
        Prune("QueryHistory", KeepHistory);
    }

    /// <summary>Nombre d'exécutions conservées : au-delà, on perd un historique qu'on ne relit pas.</summary>
    private const int KeepHistory = 5000;

    /// <summary>Runs de profil conservés (même raisonnement que l'historique).</summary>
    private const int KeepProfileRuns = 5000;

    /// <summary>Déploiements conservés : bien plus rares, un millier couvre des années.</summary>
    private const int KeepDeployLog = 1000;

    /// <summary>
    /// Borne une table « journal » aux <paramref name="keep"/> dernières lignes. Ces tables
    /// grossissaient sans limite : les LIMIT du code ne portaient que sur la lecture, et
    /// l'historique stocke le texte MDX complet à chaque exécution.
    ///
    /// L'Id étant AUTOINCREMENT, donc monotone, le seuil se calcule en une lecture indexée
    /// plutôt qu'en comptant les lignes. Les suppressions ne créent des trous qu'en bas de
    /// la plage : MAX(Id) - keep reste donc exact au fil des purges.
    /// </summary>
    private void Prune(string table, int keep)
        => Exec($"DELETE FROM {table} WHERE Id <= (SELECT MAX(Id) FROM {table}) - $keep",
            ("$keep", keep));

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

    /// <summary>Insère un snippet et retourne son Id généré. INSERT + last_insert_rowid() sous
    /// le même verrou (et la même connexion SQLite, jamais poolée) pour éviter qu'une écriture
    /// concurrente ne s'intercale entre les deux appels.</summary>
    public long AddSnippet(string name, string mdx)
    {
        lock (_lock)
        {
            using (var insert = _db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO Snippet (Name, Mdx, CreatedUtc) VALUES ($n, $m, $t)";
                insert.Parameters.AddWithValue("$n", name);
                insert.Parameters.AddWithValue("$m", mdx);
                insert.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
            using var scalar = _db.CreateCommand();
            scalar.CommandText = "SELECT last_insert_rowid()";
            return (long)scalar.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<Snippet> GetSnippets()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Mdx, CreatedUtc FROM Snippet ORDER BY Name COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            var list = new List<Snippet>();
            while (r.Read())
                list.Add(new Snippet(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    DateTime.Parse(r.GetString(3)).ToUniversalTime()));
            return list;
        }
    }

    public void DeleteSnippet(long id) => Exec("DELETE FROM Snippet WHERE Id = $id", ("$id", id));

    /// <summary>Insère un cas de non-régression et retourne son Id généré (INSERT +
    /// last_insert_rowid() sous le même verrou, comme <see cref="AddSnippet"/>).</summary>
    public long AddRegressionCase(string name, string mdx, string expectedJson)
    {
        lock (_lock)
        {
            using (var insert = _db.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO RegressionCase (Name, Mdx, ExpectedJson, CreatedUtc) VALUES ($n, $m, $j, $t)";
                insert.Parameters.AddWithValue("$n", name);
                insert.Parameters.AddWithValue("$m", mdx);
                insert.Parameters.AddWithValue("$j", expectedJson);
                insert.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
            using var scalar = _db.CreateCommand();
            scalar.CommandText = "SELECT last_insert_rowid()";
            return (long)scalar.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<RegressionCase> GetRegressionCases()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT Id, Name, Mdx, ExpectedJson, CreatedUtc FROM RegressionCase ORDER BY Name COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            var list = new List<RegressionCase>();
            while (r.Read())
                list.Add(new RegressionCase(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    DateTime.Parse(r.GetString(4)).ToUniversalTime()));
            return list;
        }
    }

    public void DeleteRegressionCase(long id) =>
        Exec("DELETE FROM RegressionCase WHERE Id = $id", ("$id", id));

    public void AddProfileRun(string server, string? catalog, string mdx, long totalMs, long storageEngineMs,
        long formulaEngineMs, int subcubeCount, int cacheHits, int aggregationHits)
    {
        Exec("""
            INSERT INTO ProfileRun (Server, Catalog, Mdx, TotalMs, StorageEngineMs, FormulaEngineMs,
                SubcubeCount, CacheHits, AggregationHits, ExecutedUtc)
            VALUES ($s, $c, $m, $tot, $se, $fe, $sc, $ch, $ah, $t)
            """,
            ("$s", server), ("$c", (object?)catalog ?? DBNull.Value), ("$m", mdx),
            ("$tot", totalMs), ("$se", storageEngineMs), ("$fe", formulaEngineMs),
            ("$sc", subcubeCount), ("$ch", cacheHits), ("$ah", aggregationHits),
            ("$t", DateTime.UtcNow.ToString("O")));
        Prune("ProfileRun", KeepProfileRuns);
    }

    public IReadOnlyList<ProfileRun> GetProfileRuns(int limit = 50)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Server, Catalog, Mdx, TotalMs, StorageEngineMs, FormulaEngineMs,
                    SubcubeCount, CacheHits, AggregationHits, ExecutedUtc
                FROM ProfileRun ORDER BY Id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<ProfileRun>();
            while (r.Read())
                list.Add(new ProfileRun(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                    (int)r.GetInt64(7), (int)r.GetInt64(8), (int)r.GetInt64(9),
                    DateTime.Parse(r.GetString(10)).ToUniversalTime()));
            return list;
        }
    }

    public void AddDeployLog(string server, string? catalog, string cubeName, string projectPath,
        int scriptChars, bool forced)
    {
        Exec("""
            INSERT INTO DeployLog (Server, Catalog, CubeName, ProjectPath, ScriptChars, Forced, DeployedUtc)
            VALUES ($s, $c, $cube, $p, $n, $f, $t)
            """,
            ("$s", server), ("$c", (object?)catalog ?? DBNull.Value), ("$cube", cubeName), ("$p", projectPath),
            ("$n", scriptChars), ("$f", forced ? 1 : 0), ("$t", DateTime.UtcNow.ToString("O")));
        Prune("DeployLog", KeepDeployLog);
    }

    public IReadOnlyList<DeployLogEntry> GetDeployLog(int limit = 100)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Server, Catalog, CubeName, ProjectPath, ScriptChars, Forced, DeployedUtc
                FROM DeployLog ORDER BY Id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<DeployLogEntry>();
            while (r.Read())
                list.Add(new DeployLogEntry(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3), r.GetString(4), (int)r.GetInt64(5), r.GetInt64(6) != 0,
                    DateTime.Parse(r.GetString(7)).ToUniversalTime()));
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
