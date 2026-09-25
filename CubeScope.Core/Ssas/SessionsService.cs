using System.Data;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// A session open on the SSAS instance. <paramref name="IsMine"/> singles out the session of
/// CubeScope itself: everything else belongs to other users or to jobs.
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
/// Sessions open on the instance, and cancellation of a session by its SPID.
///
/// PITFALLS (observed on SSAS 2022, not assumed):
/// - the DMV engine accepts NO JOIN, NO GROUP BY, NO LIKE, NO CAST: the two rowsets are
///   therefore read separately then matched in memory on SESSION_SPID;
/// - reading these DMVs requires server admin rights — without them, the read throws, and the UI
///   degrades instead of breaking (same stance as the Profiler);
/// - DISCOVER_SESSIONS durations are UInt64, DISCOVER_COMMANDS ones are Int64:
///   go through Convert rather than a direct cast.
///
/// Cancellation follows the form documented by Microsoft ("Disconnect users and sessions"):
/// a &lt;Cancel&gt; carrying the SPID, with CancelAssociated to take down the active commands
/// of the session. ⚠️ The list contains the sessions of production jobs: the caller is
/// responsible for confirmation, this service asks for none.
/// </summary>
public sealed class SessionsService(SsasSession session)
{
    /// <remarks>
    /// Listing and cancelling go through a transient connection, NOT the session's working
    /// connection: this panel exists precisely to deal with a long-running query, which holds the
    /// working connection's lock until it ends — going through it would wait for that query.
    /// </remarks>
    public async Task<IReadOnlyList<SsasSessionInfo>> ListAsync(CancellationToken ct = default)
    {
        var (sessions, commands, reader) = await session.WithTransientConnectionAsync(null, conn => (
            SsasSession.ExecuteDmv(conn, "SELECT * FROM $SYSTEM.DISCOVER_SESSIONS", ct),
            SsasSession.ExecuteDmv(conn, "SELECT * FROM $SYSTEM.DISCOVER_COMMANDS", ct),
            conn.SessionID), ct);
        return Merge(sessions, commands, session.SessionId, reader);
    }

    /// <summary>
    /// Pure matching of the two rowsets (testable without a server). <paramref name="mine"/> is
    /// the SessionID of CubeScope's working connection; <paramref name="reader"/> the one of the
    /// transient connection that read the list — left out, it is gone as soon as the list is returned.
    /// </summary>
    internal static IReadOnlyList<SsasSessionInfo> Merge(DataTable sessions, DataTable commands,
        string? mine, string? reader = null)
    {
        // In-memory matching (the DMV cannot join): the longest-running command
        // per SPID, which is the one of interest when looking for what keeps the server busy.
        var bySpid = commands.Rows.Cast<DataRow>()
            .GroupBy(r => Int32(r, "SESSION_SPID"))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => Int64(r, "COMMAND_ELAPSED_TIME_MS")).First());

        var list = new List<SsasSessionInfo>(sessions.Rows.Count);
        foreach (DataRow r in sessions.Rows)
        {
            int spid = Int32(r, "SESSION_SPID");
            bySpid.TryGetValue(spid, out var cmd);
            string sessionId = Text(r, "SESSION_ID") ?? "";
            if (reader is not null && string.Equals(sessionId, reader, StringComparison.OrdinalIgnoreCase)) continue;
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
    /// Cancels a session by its SPID: all its active commands go down with it.
    /// No guard here — it is up to the caller to have confirmed.
    ///
    /// Special case of our own session: the connection stays <c>Open</c> but its session ID
    /// no longer exists, and the next call would fail with "L'ID de session … est
    /// introuvable" (observed). So we start over on a fresh connection so that the cancellation
    /// has no visible consequence.
    /// </summary>
    /// <returns>
    /// <c>false</c> if the session had already gone — a common case: the displayed list gets stale,
    /// and a SPID is only valid while the session lives. We tell it apart from a failure so that
    /// the caller refreshes instead of showing a raw server error.
    /// </returns>
    public async Task<bool> CancelAsync(int spid, CancellationToken ct = default)
    {
        var target = (await ListAsync(ct)).FirstOrDefault(s => s.Spid == spid);
        if (target is null) return false;
        bool wasMine = target.IsMine;

        await session.WithTransientConnectionAsync(null, conn =>
        {
            using var cmd = new AdomdCommand(BuildCancelXmla(spid), conn);
            cmd.ExecuteNonQuery();
            return 0;
        }, ct);

        if (wasMine) await session.ResetAsync(ct);
        return true;
    }

    /// <summary>XMLA &lt;Cancel&gt; of a SPID and of its active commands (form documented by Microsoft).</summary>
    internal static string BuildCancelXmla(int spid) => $"""
        <Cancel xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
          <SPID>{spid}</SPID>
          <CancelAssociated>1</CancelAssociated>
        </Cancel>
        """;

    // Rowsets mix Int32/Int64/UInt64 depending on the column: we convert instead of casting.
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
