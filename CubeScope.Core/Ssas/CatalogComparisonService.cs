using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.Regression;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Result of comparing the same query between two catalogs.
/// <paramref name="Summary"/> is null when everything matches.
/// </summary>
public sealed record CatalogComparison(
    string LeftCatalog,
    string RightCatalog,
    int LeftCells,
    int RightCells,
    long LeftMs,
    long RightMs,
    bool Match,
    string? Summary,
    int DiffCount,
    IReadOnlyList<CellDiff> Diffs);

/// <summary>
/// Runs the SAME MDX on the current catalog and on another catalog of the same server,
/// then compares cell by cell. Answers the only question that matters after a script
/// change: "did any number move?".
///
/// The second catalog goes through a transient connection
/// (<see cref="SsasSession.WithTransientConnectionAsync"/>): no contention with the current
/// session, same locale and therefore same column labels. Accepted consequence: these queries
/// have their own SessionID and do not show up in the Profiler.
///
/// The comparison reuses <see cref="ResultComparer"/>, already used by the regression
/// harness: same normalization, hence same verdicts.
/// </summary>
public sealed class CatalogComparisonService(SsasSession session, QueryService queries)
{
    /// <summary>Cap on reported cells: a wide grid would produce an unreadable diff.</summary>
    private const int MaxDiffs = 200;

    public async Task<CatalogComparison> CompareAsync(
        string mdx, string otherCatalog, CancellationToken ct = default)
    {
        string left = session.Catalog
            ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");
        if (string.Equals(left, otherCatalog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Le catalogue de comparaison est identique au catalogue courant ({left}).");

        // The current catalog goes through the session (and hence the Profiler); the other does not.
        var leftResult = await queries.ExecuteAsync(mdx, ct);
        var rightResult = await session.WithTransientConnectionAsync(otherCatalog, conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
            var sw = Stopwatch.StartNew();
            var cs = cmd.ExecuteCellSet();
            sw.Stop();
            return CellSetMapper.Map(cs, sw.ElapsedMilliseconds);
        }, ct);

        var cmp = ResultComparer.Compare(leftResult, rightResult, MaxDiffs);

        return new CatalogComparison(
            LeftCatalog: left,
            RightCatalog: otherCatalog,
            LeftCells: leftResult.CellCount,
            RightCells: rightResult.CellCount,
            LeftMs: leftResult.DurationMs,
            RightMs: rightResult.DurationMs,
            Match: cmp.Match,
            Summary: cmp.Summary,
            DiffCount: cmp.Diffs.Count,
            Diffs: cmp.Diffs);
    }
}
