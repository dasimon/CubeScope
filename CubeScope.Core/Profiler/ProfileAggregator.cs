using CubeScope.Core.Models;

namespace CubeScope.Core.Profiler;

/// <summary>
/// Agrégation pure (testable sans serveur) des événements de trace d'une requête en
/// un découpage Formula Engine / Storage Engine. Validé au spike 2026-07-24 :
/// total = durée du QueryEnd, SE = somme des QuerySubcube, FE = total − SE.
/// </summary>
public static class ProfileAggregator
{
    public static QueryProfile Aggregate(IReadOnlyList<ProfileEvent> events, long fallbackTotalMs)
    {
        long total = events.Where(e => e.EventClass == "QueryEnd").Sum(e => e.DurationMs);
        if (total == 0) total = fallbackTotalMs;

        // QuerySubcube = lecture Storage Engine (durée). QuerySubcubeVerbose porte la MÊME
        // grille mais décrite en clair (noms de dimensions/attributs) — on l'apparie par ordre
        // pour afficher du lisible plutôt que le bitmap brut. SE = somme des QuerySubcube seuls.
        var raw = events.Where(e => e.EventClass == "QuerySubcube").OrderBy(e => e.CapturedUtc).ToList();
        var verbose = events.Where(e => e.EventClass == "QuerySubcubeVerbose").OrderBy(e => e.CapturedUtc).ToList();
        var subcubes = raw
            .Select((e, i) => new SubcubeInfo(
                e.DurationMs,
                i < verbose.Count && !string.IsNullOrWhiteSpace(verbose[i].TextData)
                    ? verbose[i].TextData!
                    : e.TextData ?? ""))
            .OrderByDescending(s => s.DurationMs)
            .ToList();
        long se = raw.Sum(e => e.DurationMs);
        long fe = Math.Max(0, total - se);

        int cacheHits = events.Count(e => e.EventClass == "GetDataFromCache");
        int aggHits = events.Count(e => e.EventClass == "GetDataFromAggregation");

        return new QueryProfile(total, se, fe, subcubes.Count, cacheHits, aggHits, subcubes);
    }
}
