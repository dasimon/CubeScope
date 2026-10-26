using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.State;
using Microsoft.AnalysisServices;
using ProcessingState = CubeScope.Core.Models.ProcessingState;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Processing state of the current database, read through AMO: cubes → measure groups →
/// partitions, plus the database dimensions. Read-only: nothing here modifies the server.
///
/// Why AMO and not a DMV: an unprocessed cube disappears from MDSCHEMA_CUBES (known pitfall),
/// which is precisely the case this panel must explain. AMO reads the definition, where it stays.
///
/// OBSERVED (SSAS 17.0.25.218, dev and prod, 2026-10-26) — see CLAUDE.md "Known pitfalls":
/// - the database's LastProcessed is not updated by a cube process → never shown as "last processing";
/// - an unprocessed object keeps a LastProcessed date → the state wins over the date;
/// - AMO returns LastProcessed as DateTime.Kind=Local → converted to UTC here;
/// - EstimatedRows is the design-time estimate (identical in dev and prod, a measure group ≠ the sum
///   of its partitions), EstimatedSize is reported by the server (measure group = Σ partitions).
/// </summary>
public sealed class ProcessingStateService(SsasSession session, StateStore store)
{
    /// <remarks>
    /// Short AMO connection of its own, like <see cref="CacheService"/>: it never waits for the lock
    /// of the ADOMD session, which a long-running query may hold for minutes.
    /// </remarks>
    public async Task<ProcessingState> ReadAsync(CancellationToken ct = default)
    {
        string server = session.Server ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        string catalog = session.Catalog ?? throw new InvalidOperationException("Aucun catalogue sélectionné.");
        int thresholdHours = store.GetStaleThresholdHours();

        var snapshot = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            using var amo = new Server();
            amo.Connect($"Data Source={server};Integrated Security=SSPI;");
            long connectMs = sw.ElapsedMilliseconds;
            try
            {
                // Without the Read Definition permission, the database is expected to be absent from
                // the collection (NOT verified: no account without it was available). ADOMD listed
                // the catalog, so the likely cause is permissions — a drop in between is possible too.
                var db = amo.Databases.FindByName(catalog)
                    ?? throw new ProcessingStateUnavailableException(
                        $"La définition de la base « {catalog} » est introuvable ou illisible sur {server} : "
                        + "le droit de lecture de la définition (Read Definition) est requis.");
                var result = Snapshot(server, db);
                Console.WriteLine($"[CubeScope] Processing state read in {sw.ElapsedMilliseconds} ms "
                    + $"(AMO connect {connectMs} ms).");
                return result;
            }
            finally
            {
                amo.Disconnect();
            }
        }, ct);

        return ProcessingStateEvaluator.Evaluate(snapshot, thresholdHours, DateTime.UtcNow);
    }

    /// <summary>AMO → plain records. Thin on purpose: every verdict lives in the evaluator.</summary>
    private static DatabaseSnapshot Snapshot(string server, Database db) => new(
        server,
        db.Name,
        Map(db.State),
        db.Cubes.Cast<Cube>().Select(c => new CubeSnapshot(
            c.Name, c.ID, Map(c.State), Utc(c.LastProcessed),
            c.MeasureGroups.Cast<MeasureGroup>().Select(mg => new MeasureGroupSnapshot(
                mg.Name, mg.ID, Map(mg.State), Utc(mg.LastProcessed), mg.EstimatedSize,
                mg.Partitions.Cast<Partition>().Select(p => new PartitionSnapshot(
                    p.Name, p.ID, Map(p.State), Utc(p.LastProcessed), p.StorageMode.ToString(),
                    string.IsNullOrWhiteSpace(p.Slice) ? null : p.Slice,
                    AggregationDesignName(mg, p),
                    p.EstimatedSize, p.EstimatedRows)).ToList())).ToList())).ToList(),
        db.Dimensions.Cast<Dimension>().Select(d => new DimensionSnapshot(
            d.Name, d.ID, Map(d.State), Utc(d.LastProcessed))).ToList());

    private static string? AggregationDesignName(MeasureGroup mg, Partition p)
    {
        if (string.IsNullOrEmpty(p.AggregationDesignID)) return null;
        return mg.AggregationDesigns.Find(p.AggregationDesignID)?.Name ?? p.AggregationDesignID;
    }

    internal static ObjectState Map(AnalysisState state) => state switch
    {
        AnalysisState.Processed => ObjectState.Processed,
        AnalysisState.PartiallyProcessed => ObjectState.PartiallyProcessed,
        // Unknown future values fall on the safe side: shown in red, never as fine.
        _ => ObjectState.Unprocessed,
    };

    /// <summary>
    /// AMO date → UTC. A never-processed object can carry DateTime.MinValue: that is no date.
    /// AMO gives Kind=Local (observed); an Unspecified value is treated as local as well.
    /// </summary>
    internal static DateTime? Utc(DateTime value)
    {
        if (value.Year < 1900) return null;
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}

/// <summary>The definition cannot be read (permissions): a state for the UI, not a failure.</summary>
public sealed class ProcessingStateUnavailableException(string message) : InvalidOperationException(message);
