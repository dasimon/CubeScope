using System.Collections.Concurrent;
using System.Diagnostics;
using CubeScope.Core.Project;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// XMLA ClearCache scoped to the current catalog. Known pitfall: the XMLA element requires the
/// DatabaseID, which differs from the name if the database was renamed → resolved through AMO
/// (settled decision: AMO only for the MDX Script and object IDs), with a cache.
/// </summary>
public sealed class CacheService(SsasSession session)
{
    private readonly ConcurrentDictionary<string, string> _idCache = new();

    /// <param name="devServers">
    /// Servers where clearing the cache is allowed (explicit list, see <see cref="DevServerGuard"/>).
    /// Empty = none (fail-closed). Same rule as the script deployment: emptying the cache of a
    /// production catalog slows every user down until it warms up again.
    /// </param>
    public async Task<(string DatabaseId, long DurationMs)> ClearCacheAsync(
        IReadOnlyList<string> devServers, CancellationToken ct = default)
    {
        string server = session.Server ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        string catalog = session.Catalog ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");

        // BEFORE the AMO resolution below, which already connects to the server.
        EnsureDevServer(server, devServers);

        string databaseId = await ResolveDatabaseIdAsync(server, catalog, ct);

        var sw = Stopwatch.StartNew();
        await session.WithConnectionAsync(conn =>
        {
            string xmla = $"""
                <ClearCache xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
                  <Object>
                    <DatabaseID>{System.Security.SecurityElement.Escape(databaseId)}</DatabaseID>
                  </Object>
                </ClearCache>
                """;
            using var cmd = new AdomdCommand(xmla, conn);
            cmd.ExecuteNonQuery();
            return 0;
        }, ct);
        sw.Stop();
        return (databaseId, sw.ElapsedMilliseconds);
    }

    /// <summary>Refuses a server absent from the dev list — the API may be called without the UI.</summary>
    internal static void EnsureDevServer(string server, IReadOnlyList<string> devServers)
    {
        if (!DevServerGuard.IsDev(devServers, server))
            throw new ClearCacheRefusedException(
                $"ClearCache refused: server \"{server}\" is not declared as a development server. "
                + "Declare it in the connection dialog if it is one.");
    }

    /// <summary>Catalog name → DatabaseID through AMO (short dedicated connection, result cached).</summary>
    internal async Task<string> ResolveDatabaseIdAsync(string server, string catalog, CancellationToken ct = default)
    {
        string key = $"{server}|{catalog}";
        if (_idCache.TryGetValue(key, out var cached)) return cached;

        string id = await Task.Run(() =>
        {
            using var amo = new Microsoft.AnalysisServices.Server();
            amo.Connect($"Data Source={server};Integrated Security=SSPI;");
            try
            {
                var db = amo.Databases.GetByName(catalog);
                return db.ID;
            }
            finally
            {
                amo.Disconnect();
            }
        }, ct);
        _idCache[key] = id;
        return id;
    }
}

/// <summary>ClearCache refused by the dev server guard (distinct from a failed connection).</summary>
public sealed class ClearCacheRefusedException(string message) : InvalidOperationException(message);
