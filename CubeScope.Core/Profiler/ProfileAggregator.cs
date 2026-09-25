using CubeScope.Core.Models;

namespace CubeScope.Core.Profiler;

/// <summary>
/// Pure aggregation (testable without a server) of a query's trace events into
/// a Formula Engine / Storage Engine breakdown. Validated in the 2026-07-24 spike:
/// total = QueryEnd duration, SE = sum of QuerySubcube, FE = total − SE.
/// </summary>
public static class ProfileAggregator
{
    /// <summary>
    /// The events of ONE query among those captured on its session since it started. The session
    /// is shared: hover captions, explorer expansions… run right after on the same SessionID, and
    /// the previous query's last events can still arrive after this one started (asynchronous
    /// push). So the window ends at the QueryEnd whose text is this query's, and what precedes
    /// another QueryEnd belongs to that other query. Without a matching QueryEnd (text column
    /// missing, reformatted text), falls back to the events up to the first QueryEnd.
    /// </summary>
    public static IReadOnlyList<ProfileEvent> QueryWindow(IReadOnlyList<ProfileEvent> events, string? queryText)
    {
        string? wanted = queryText is null ? null : Normalize(queryText);
        var window = new List<ProfileEvent>();
        List<ProfileEvent>? firstWindow = null;
        foreach (var e in events.OrderBy(e => e.CapturedUtc))
        {
            window.Add(e);
            if (e.EventClass != "QueryEnd") continue;
            if (wanted is not null && e.TextData is not null && Normalize(e.TextData) == wanted) return window;
            firstWindow ??= [.. window];
            window.Clear();
        }
        return firstWindow ?? window;
    }

    // Whitespace-insensitive: line endings and indentation may not come back as submitted.
    private static string Normalize(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
