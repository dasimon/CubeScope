using System.Data;
using System.Diagnostics;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>MDX execution → QueryResult, with timing and cancellation (AdomdCommand.Cancel).</summary>
public sealed class QueryService(SsasSession session)
{
    public Task<QueryResult> ExecuteAsync(string mdx, CancellationToken ct = default)
        => session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
            var sw = Stopwatch.StartNew();
            var cs = cmd.ExecuteCellSet();
            sw.Stop();
            ct.ThrowIfCancellationRequested();
            return CellSetMapper.Map(cs, sw.ElapsedMilliseconds);
        }, ct);

    /// <summary>
    /// Wraps the current MDX query in DRILLTHROUGH and returns the source rowset.
    /// Known limitation: no precise per-cell drillthrough (CellSetMapper only keeps
    /// the Captions, not the UniqueNames) — works for a query that is "drillthroughable"
    /// server-side (typically a single cell).
    /// </summary>
    public Task<QueryResult> ExecuteDrillthroughAsync(string mdx, int maxRows, CancellationToken ct = default)
    {
        var stmt = BuildDrillthrough(mdx, maxRows);
        return session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(stmt, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
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

    /// <summary>Builds the DRILLTHROUGH statement; does not wrap twice if already present.</summary>
    internal static string BuildDrillthrough(string mdx, int maxRows)
    {
        var trimmed = mdx.Trim();
        if (trimmed.StartsWith("DRILLTHROUGH", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var clamped = Math.Clamp(maxRows, 1, 100000);
        return $"DRILLTHROUGH MAXROWS {clamped} {trimmed}";
    }

    /// <summary>Flattens a drillthrough rowset (DataTable) into a QueryResult for the grid.</summary>
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
