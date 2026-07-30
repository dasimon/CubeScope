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
    private string? _connectionString;

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
                _connectionString = $"Data Source={server};Integrated Security=SSPI;{LocaleClause(lang)}";
                _conn = new AdomdConnection(_connectionString);
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
            await Task.Run(() => EnsureOpen().ChangeDatabase(catalog), ct);
            Catalog = catalog;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Repart sur une connexion neuve. Nécessaire après avoir annulé notre propre session :
    /// ADOMD garde alors une connexion en état <c>Open</c> dont l'ID de session n'existe plus
    /// côté serveur, et la requête suivante échoue sur « L'ID de session … est introuvable.
    /// Soit la session n'existe pas, soit elle a déjà expiré » (constaté). L'état de la
    /// connexion ne trahit rien : seule une reconnexion explicite règle le problème.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                _conn?.Dispose();
                _conn = new AdomdConnection(_connectionString);
                _conn.Open();
                if (Catalog is not null) _conn.ChangeDatabase(Catalog);
            }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exécute un travail sur la connexion courante, sous verrou.</summary>
    public async Task<T> WithConnectionAsync<T>(Func<AdomdConnection, T> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => work(EnsureOpen()), ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Connexion utilisable, rouverte si ADOMD l'a fermée dans notre dos. Constaté en usage réel :
    /// après l'annulation d'une requête, la requête suivante échouait sur « La connexion n'est pas
    /// ouverte » (message ADOMD, pas le nôtre) — l'annulation passe par un &lt;Cancel&gt; XMLA, et
    /// rien dans la doc ADOMD ne garantit que la session y survit. Plutôt que de renvoyer l'erreur
    /// à l'utilisateur, on rétablit la connexion et son catalogue.
    /// À appeler sous <see cref="_gate"/> — un seul travail à la fois sur la connexion.
    /// </summary>
    private AdomdConnection EnsureOpen()
    {
        var conn = _conn ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        if (conn.State == ConnectionState.Open) return conn;

        // La trace rend l'hypothèse vérifiable : si le symptôme revient, cette ligne dit
        // s'il s'agissait bien d'une connexion fermée, et à quel moment.
        Console.WriteLine($"[CubeScope] Connexion SSAS trouvée {conn.State} — réouverture.");
        conn.Dispose();
        _conn = new AdomdConnection(_connectionString);
        _conn.Open();
        if (Catalog is not null) _conn.ChangeDatabase(Catalog);
        return _conn;
    }

    /// <summary>
    /// Exécute un travail sur une connexion NEUVE visant un autre catalogue du même serveur,
    /// hors du verrou : sert à comparer un résultat entre deux catalogues sans perturber la
    /// session courante. La chaîne de connexion est celle de la session — donc la même locale,
    /// sinon les libellés de colonnes différeraient et la comparaison verrait de faux écarts.
    /// Conséquence assumée : ces requêtes ont leur propre SessionID, le Profiler ne les voit pas.
    /// </summary>
    public Task<T> WithTransientConnectionAsync<T>(
        string catalog, Func<AdomdConnection, T> work, CancellationToken ct = default)
    {
        var connectionString = _connectionString
            ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        return Task.Run(() =>
        {
            using var conn = new AdomdConnection(connectionString);
            conn.Open();
            conn.ChangeDatabase(catalog);
            return work(conn);
        }, ct);
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
