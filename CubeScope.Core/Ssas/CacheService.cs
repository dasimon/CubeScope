using System.Collections.Concurrent;
using System.Diagnostics;
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

    public async Task<(string DatabaseId, long DurationMs)> ClearCacheAsync(CancellationToken ct = default)
    {
        string server = session.Server ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        string catalog = session.Catalog ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");

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
