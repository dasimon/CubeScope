using System.Data;
using System.Diagnostics;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>Exécution MDX → QueryResult, avec timing et annulation (AdomdCommand.Cancel).</summary>
public sealed class QueryService(SsasSession session)
{
    public Task<QueryResult> ExecuteAsync(string mdx, CancellationToken ct = default)
        => session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* déjà terminé */ } });
            var sw = Stopwatch.StartNew();
            var cs = cmd.ExecuteCellSet();
            sw.Stop();
            ct.ThrowIfCancellationRequested();
            return CellSetMapper.Map(cs, sw.ElapsedMilliseconds);
        }, ct);

    /// <summary>
    /// Enveloppe la requête MDX courante dans DRILLTHROUGH et retourne le rowset source.
    /// Limitation connue : pas de drillthrough précis par cellule (CellSetMapper ne garde que
    /// les Captions, pas les UniqueName) — fonctionne pour une requête « drillthroughable »
    /// côté serveur (typiquement une cellule unique).
    /// </summary>
    public Task<QueryResult> ExecuteDrillthroughAsync(string mdx, int maxRows, CancellationToken ct = default)
    {
        var stmt = BuildDrillthrough(mdx, maxRows);
        return session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(stmt, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* déjà terminé */ } });
            var sw = Stopwatch.StartNew();
            using var reader = cmd.ExecuteReader();
            var table = new DataTable();
            using var ds = new DataSet { EnforceConstraints = false };
            ds.Tables.Add(table);
            table.Load(reader);
            sw.Stop();
            ct.ThrowIfCancellationRequested();
            return MapTable(table, sw.ElapsedMilliseconds);
        }, ct);
    }

    /// <summary>Construit l'instruction DRILLTHROUGH ; ne double pas l'enveloppe si déjà présente.</summary>
    internal static string BuildDrillthrough(string mdx, int maxRows)
    {
        var trimmed = mdx.Trim();
        if (trimmed.StartsWith("DRILLTHROUGH", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var clamped = Math.Clamp(maxRows, 1, 100000);
        return $"DRILLTHROUGH MAXROWS {clamped} {trimmed}";
    }

    /// <summary>Aplati un rowset de drillthrough (DataTable) en QueryResult pour la grille.</summary>
    internal static QueryResult MapTable(DataTable table, long durationMs)
    {
        var columns = table.Columns.Cast<DataColumn>()
            .Select(c => new GridColumn(c.ColumnName, c.ColumnName, false))
            .ToList();

        var rows = new List<Dictionary<string, object?>>(table.Rows.Count);
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object?>(columns.Count);
            foreach (var col in columns)
            {
                var value = row[col.Field];
                dict[col.Field] = value is DBNull ? null : value;
            }
            rows.Add(dict);
        }

        return new QueryResult(columns, rows, rows.Count, 0, durationMs);
    }
}
