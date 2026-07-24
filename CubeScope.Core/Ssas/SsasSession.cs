using System.Data;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Session SSAS unique de l'application (outil mono-utilisateur) : une connexion ADOMD
/// courante, sérialisée par un verrou (ADOMD n'est pas thread-safe). Piège connu :
/// ouvrir SANS Initial Catalog puis ChangeDatabase(), pour pouvoir lister DBSCHEMA_CATALOGS.
/// </summary>
public sealed class SsasSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdomdConnection? _conn;

    public string? Server { get; private set; }
    public string? Catalog { get; private set; }

    /// <summary>SessionID SSAS de la connexion courante (corrélation avec la trace du profiler).</summary>
    public string? SessionId => _conn?.SessionID;

    // Locale de la connexion = langue de l'UI → les libellés du cube (mesures, membres)
    // reviennent dans cette langue quand le cube a des traductions. Défaut : locale système.
    private static string LocaleClause(string? lang) => lang switch
    {
        "en" => "Locale Identifier=1033;",
        "fr" => "Locale Identifier=1036;",
        _ => "",
    };

    /// <summary>Ouvre (ou remplace) la connexion et retourne la liste des catalogues.</summary>
    public async Task<IReadOnlyList<string>> ConnectAsync(string server, string? lang = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                _conn?.Dispose();
                _conn = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;{LocaleClause(lang)}");
                _conn.Open();
                Server = server;
                Catalog = null;
                var t = GetSchemaTable(_conn, "DBSCHEMA_CATALOGS", null);
                return (IReadOnlyList<string>)t.Rows.Cast<DataRow>()
                    .Select(r => (string)r["CATALOG_NAME"]).ToList();
            }, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task SetCatalogAsync(string catalog, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var conn = _conn ?? throw new InvalidOperationException("Aucune connexion ouverte.");
            await Task.Run(() => conn.ChangeDatabase(catalog), ct);
            Catalog = catalog;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exécute un travail sur la connexion courante, sous verrou.</summary>
    public async Task<T> WithConnectionAsync<T>(Func<AdomdConnection, T> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var conn = _conn ?? throw new InvalidOperationException("Aucune connexion ouverte.");
            return await Task.Run(() => work(conn), ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exécute une DMV $SYSTEM.* sur la connexion courante (métadonnées).</summary>
    public Task<DataTable> ExecuteDmvAsync(string query, CancellationToken ct = default)
        => WithConnectionAsync(conn => ExecuteDmv(conn, query), ct);

    internal static DataTable GetSchemaTable(AdomdConnection conn, string schemaName, AdomdRestrictionCollection? restrictions)
        => conn.GetSchemaDataSet(schemaName, restrictions).Tables[0];

    /// <summary>
    /// Exécute une DMV ($SYSTEM.*) via ExecuteReader. Piège connu : les rowsets déclarent
    /// des contraintes d'unicité que leurs données violent → charger la DataTable dans un
    /// DataSet avec EnforceConstraints = false avant Load.
    /// </summary>
    internal static DataTable ExecuteDmv(AdomdConnection conn, string query)
    {
        using var cmd = new AdomdCommand(query, conn);
        using var rdr = cmd.ExecuteReader();
        var ds = new DataSet { EnforceConstraints = false };
        var t = new DataTable();
        ds.Tables.Add(t);
        t.Load(rdr);
        return t;
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
