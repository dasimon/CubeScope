using System.Data;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Une session ouverte sur l'instance SSAS. <paramref name="IsMine"/> distingue la session de
/// CubeScope lui-même : tout le reste appartient à d'autres utilisateurs ou à des jobs.
/// </summary>
public sealed record SsasSessionInfo(
    int Spid,
    string SessionId,
    string User,
    string? Database,
    DateTime StartTime,
    long ElapsedMs,
    long CpuMs,
    long IdleMs,
    string? LastCommand,
    string? CommandText,
    long CommandElapsedMs,
    bool IsMine);

/// <summary>
/// Sessions ouvertes sur l'instance et annulation d'une session par son SPID.
///
/// PIÈGES (constatés sur SSAS 2022, pas supposés) :
/// - le moteur DMV n'accepte NI JOIN, NI GROUP BY, NI LIKE, NI CAST : les deux rowsets sont
///   donc lus séparément puis rapprochés en mémoire sur SESSION_SPID ;
/// - lire ces DMV exige les droits admin serveur — sans eux, la lecture lève, et l'UI se
///   dégrade au lieu de casser (même parti pris que le Profiler) ;
/// - les durées de DISCOVER_SESSIONS sont des UInt64, celles de DISCOVER_COMMANDS des Int64 :
///   passer par Convert plutôt que par un cast direct.
///
/// L'annulation suit la forme documentée par Microsoft (« Disconnect users and sessions ») :
/// un &lt;Cancel&gt; portant le SPID, avec CancelAssociated pour emporter les commandes actives
/// de la session. ⚠️ La liste contient les sessions des jobs de production : l'appelant est
/// responsable de la confirmation, ce service n'en pose aucune.
/// </summary>
public sealed class SessionsService(SsasSession session)
{
    public async Task<IReadOnlyList<SsasSessionInfo>> ListAsync(CancellationToken ct = default)
    {
        var sessions = await session.ExecuteDmvAsync("SELECT * FROM $SYSTEM.DISCOVER_SESSIONS", ct);
        var commands = await session.ExecuteDmvAsync("SELECT * FROM $SYSTEM.DISCOVER_COMMANDS", ct);

        // Rapprochement en mémoire (le DMV ne sait pas joindre) : la commande la plus longue
        // par SPID, qui est celle qui intéresse quand on cherche ce qui occupe le serveur.
        var bySpid = commands.Rows.Cast<DataRow>()
            .GroupBy(r => Int32(r, "SESSION_SPID"))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => Int64(r, "COMMAND_ELAPSED_TIME_MS")).First());

        string? mine = session.SessionId;
        var list = new List<SsasSessionInfo>(sessions.Rows.Count);
        foreach (DataRow r in sessions.Rows)
        {
            int spid = Int32(r, "SESSION_SPID");
            bySpid.TryGetValue(spid, out var cmd);
            string sessionId = Text(r, "SESSION_ID") ?? "";
            list.Add(new SsasSessionInfo(
                Spid: spid,
                SessionId: sessionId,
                User: Text(r, "SESSION_USER_NAME") ?? "",
                Database: Text(r, "SESSION_CURRENT_DATABASE"),
                StartTime: Date(r, "SESSION_START_TIME"),
                ElapsedMs: Int64(r, "SESSION_ELAPSED_TIME_MS"),
                CpuMs: Int64(r, "SESSION_CPU_TIME_MS"),
                IdleMs: Int64(r, "SESSION_IDLE_TIME_MS"),
                LastCommand: Text(r, "SESSION_LAST_COMMAND"),
                CommandText: cmd is null ? null : Text(cmd, "COMMAND_TEXT"),
                CommandElapsedMs: cmd is null ? 0 : Int64(cmd, "COMMAND_ELAPSED_TIME_MS"),
                IsMine: mine is not null && string.Equals(sessionId, mine, StringComparison.OrdinalIgnoreCase)));
        }

        return list.OrderByDescending(s => s.CommandElapsedMs).ThenByDescending(s => s.CpuMs).ToList();
    }

    /// <summary>
    /// Annule une session par son SPID : toutes ses commandes actives tombent avec elle.
    /// Aucune garde ici — c'est à l'appelant d'avoir confirmé.
    ///
    /// Cas particulier de notre propre session : la connexion reste <c>Open</c> mais son ID de
    /// session n'existe plus, et l'appel suivant échouerait sur « L'ID de session … est
    /// introuvable » (constaté). On repart donc sur une connexion neuve pour que l'annulation
    /// soit sans conséquence visible.
    /// </summary>
    /// <returns>
    /// <c>false</c> si la session avait déjà disparu — cas courant : la liste affichée vieillit,
    /// et un SPID n'est valable que tant que la session vit. On le distingue d'un échec pour que
    /// l'appelant rafraîchisse au lieu de présenter une erreur serveur brute.
    /// </returns>
    public async Task<bool> CancelAsync(int spid, CancellationToken ct = default)
    {
        var target = (await ListAsync(ct)).FirstOrDefault(s => s.Spid == spid);
        if (target is null) return false;
        bool wasMine = target.IsMine;

        await session.WithConnectionAsync(conn =>
        {
            string xmla = $"""
                <Cancel xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
                  <SPID>{spid}</SPID>
                  <CancelAssociated>1</CancelAssociated>
                </Cancel>
                """;
            using var cmd = new AdomdCommand(xmla, conn);
            cmd.ExecuteNonQuery();
            return 0;
        }, ct);

        if (wasMine) await session.ResetAsync(ct);
        return true;
    }

    // Les rowsets mélangent Int32/Int64/UInt64 selon la colonne : on convertit au lieu de caster.
    private static string? Text(DataRow r, string col)
    {
        if (!r.Table.Columns.Contains(col) || r[col] is DBNull) return null;
        var s = r[col].ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int Int32(DataRow r, string col)
        => !r.Table.Columns.Contains(col) || r[col] is DBNull ? 0 : Convert.ToInt32(r[col]);

    private static long Int64(DataRow r, string col)
        => !r.Table.Columns.Contains(col) || r[col] is DBNull ? 0 : Convert.ToInt64(r[col]);

    private static DateTime Date(DataRow r, string col)
        => !r.Table.Columns.Contains(col) || r[col] is DBNull ? default : Convert.ToDateTime(r[col]);
}
