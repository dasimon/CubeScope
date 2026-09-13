using CubeScope.Core.Models;

namespace CubeScope.Core.Profiler;

/// <summary>
/// Pure aggregation (testable without a server) of a query's trace events into
/// a Formula Engine / Storage Engine breakdown. Validated in the 2026-07-24 spike:
/// total = QueryEnd duration, SE = sum of QuerySubcube, FE = total − SE.
/// </summary>
public static class ProfileAggregator
{
    public static QueryProfile Aggregate(IReadOnlyList<ProfileEvent> events, long fallbackTotalMs)
    {
        long total = events.Where(e => e.EventClass == "QueryEnd").Sum(e => e.DurationMs);
        if (total == 0) total = fallbackTotalMs;

        // QuerySubcube = Storage Engine read (duration). QuerySubcubeVerbose carries the SAME
        // grid but described in plain text (dimension/attribute names) — we pair them by order
        // to display something readable rather than the raw bitmap. SE = sum of QuerySubcube only.
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
