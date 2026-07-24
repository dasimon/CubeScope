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
}
