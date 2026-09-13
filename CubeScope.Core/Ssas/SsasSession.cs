using System.Data;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// The application's single SSAS session (single-user tool): one current ADOMD
/// connection, serialized by a lock (ADOMD is not thread-safe). Known pitfall:
/// open WITHOUT Initial Catalog then ChangeDatabase(), to be able to list DBSCHEMA_CATALOGS.
/// </summary>
public sealed class SsasSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdomdConnection? _conn;
    private string? _connectionString;

    public string? Server { get; private set; }
    public string? Catalog { get; private set; }

    /// <summary>SSAS SessionID of the current connection (correlation with the profiler trace).</summary>
    public string? SessionId => _conn?.SessionID;

    // Connection locale = UI language → cube labels (measures, members)
    // come back in that language when the cube has translations. Default: system locale.
    private static string LocaleClause(string? lang) => lang switch
    {
        "en" => "Locale Identifier=1033;",
        "fr" => "Locale Identifier=1036;",
        _ => "",
    };

    /// <summary>Opens (or replaces) the connection and returns the list of catalogs.</summary>
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
    /// Starts over on a fresh connection. Needed after cancelling our own session:
    /// ADOMD then keeps a connection in the <c>Open</c> state whose session ID no longer exists
    /// on the server, and the next query fails with "L'ID de session … est introuvable.
    /// Soit la session n'existe pas, soit elle a déjà expiré" (observed). The connection
    /// state gives nothing away: only an explicit reconnection fixes the problem.
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

    /// <summary>Runs a piece of work on the current connection, under the lock.</summary>
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
    /// Usable connection, reopened if ADOMD closed it behind our back. Observed in real use:
    /// after cancelling a query, the next query failed with "La connexion n'est pas
    /// ouverte" (an ADOMD message, not ours) — cancellation goes through an XMLA &lt;Cancel&gt;, and
    /// nothing in the ADOMD docs guarantees that the session survives it. Rather than returning the error
    /// to the user, we restore the connection and its catalog.
    /// Call under <see cref="_gate"/> — only one piece of work at a time on the connection.
    /// </summary>
    private AdomdConnection EnsureOpen()
    {
        var conn = _conn ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        if (conn.State == ConnectionState.Open) return conn;

        // The log line makes the hypothesis checkable: if the symptom comes back, this line tells
        // whether it really was a closed connection, and when.
        Console.WriteLine($"[CubeScope] SSAS connection found {conn.State} — reopening.");
        conn.Dispose();
        _conn = new AdomdConnection(_connectionString);
        _conn.Open();
        if (Catalog is not null) _conn.ChangeDatabase(Catalog);
        return _conn;
    }

    /// <summary>
    /// Runs a piece of work on a FRESH connection targeting another catalog of the same server,
    /// outside the lock: used to compare a result between two catalogs without disturbing the
    /// current session. The connection string is the session's — hence the same locale,
    /// otherwise the column labels would differ and the comparison would see false differences.
    /// Accepted consequence: these queries have their own SessionID, the Profiler does not see them.
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

    /// <summary>Runs a $SYSTEM.* DMV on the current connection (metadata).</summary>
    public Task<DataTable> ExecuteDmvAsync(string query, CancellationToken ct = default)
        => WithConnectionAsync(conn => ExecuteDmv(conn, query), ct);

    internal static DataTable GetSchemaTable(AdomdConnection conn, string schemaName, AdomdRestrictionCollection? restrictions)
        => conn.GetSchemaDataSet(schemaName, restrictions).Tables[0];

    /// <summary>
    /// Runs a DMV ($SYSTEM.*) via ExecuteReader. Known pitfall: the rowsets declare
    /// uniqueness constraints that their data violates → load the DataTable into a
    /// DataSet with EnforceConstraints = false before Load.
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
