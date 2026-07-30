using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.Regression;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Résultat d'une comparaison de la même requête entre deux catalogues.
/// <paramref name="Summary"/> est null quand tout concorde.
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
/// Exécute le MÊME MDX sur le catalogue courant et sur un autre catalogue du même serveur,
/// puis compare cellule à cellule. Répond à la seule question qui compte après un changement
/// de script : « est-ce qu'un chiffre a bougé ? ».
///
/// Le second catalogue passe par une connexion transitoire
/// (<see cref="SsasSession.WithTransientConnectionAsync"/>) : pas de contention avec la session
/// courante, même locale donc mêmes libellés de colonnes. Conséquence assumée : ces requêtes
/// ont leur propre SessionID et n'apparaissent pas dans le Profiler.
///
/// La comparaison réutilise <see cref="ResultComparer"/>, déjà employé par le harnais de
/// non-régression : même normalisation, donc mêmes verdicts.
/// </summary>
public sealed class CatalogComparisonService(SsasSession session, QueryService queries)
{
    /// <summary>Plafond de cellules rapportées : une grille large produirait un diff illisible.</summary>
    private const int MaxDiffs = 200;

    public async Task<CatalogComparison> CompareAsync(
        string mdx, string otherCatalog, CancellationToken ct = default)
    {
        string left = session.Catalog
            ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");
        if (string.Equals(left, otherCatalog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Le catalogue de comparaison est identique au catalogue courant ({left}).");

        // Le catalogue courant passe par la session (et donc par le Profiler) ; l'autre non.
        var leftResult = await queries.ExecuteAsync(mdx, ct);
        var rightResult = await session.WithTransientConnectionAsync(otherCatalog, conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* déjà fini */ } });
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
