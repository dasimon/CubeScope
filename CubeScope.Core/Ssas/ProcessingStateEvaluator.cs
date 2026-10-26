using CubeScope.Core.Models;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Pure staleness verdict on an AMO snapshot (testable without a server).
///
/// Rules:
/// - a partition or a dimension that is not Processed is <see cref="FreshnessStatus.Unprocessed"/>,
///   whatever its date: an unprocessed object keeps a LastProcessed value (observed);
/// - otherwise it is <see cref="FreshnessStatus.Stale"/> when its last processing is older than
///   the threshold (strictly), or when the server gave no date at all;
/// - a measure group or a cube has no rule of its own: it rolls up its partitions (red if it, or
///   one of its partitions, is unprocessed; orange if one partition is stale). Its freshness range
///   comes from its processed partitions only — the date of an unprocessed one means nothing.
/// </summary>
public static class ProcessingStateEvaluator
{
    public static ProcessingState Evaluate(DatabaseSnapshot db, int thresholdHours, DateTime nowUtc)
    {
        var threshold = TimeSpan.FromHours(thresholdHours);

        var cubes = db.Cubes.Select(c => EvaluateCube(c, threshold, nowUtc)).ToList();
        var dimensions = db.Dimensions
            .Select(d => new DimensionNode(d.Name, d.Id, d.State, d.LastProcessedUtc,
                LeafStatus(d.State, d.LastProcessedUtc, threshold, nowUtc)))
            .ToList();

        var processedPartitions = cubes
            .SelectMany(c => c.MeasureGroups)
            .SelectMany(mg => mg.Partitions)
            .Where(p => p.Status != FreshnessStatus.Unprocessed && p.LastProcessedUtc is not null)
            .ToList();

        var summary = new ProcessingSummary(
            // Unprocessed only: that is the cube missing from MDSCHEMA_CUBES. A partially processed
            // cube is still red in the tree, through its unprocessed partitions.
            UnprocessedCubes: cubes.Count(c => c.State == ObjectState.Unprocessed),
            UnprocessedPartitions: cubes.Sum(c => c.UnprocessedCount),
            UnprocessedDimensions: dimensions.Count(d => d.Status == FreshnessStatus.Unprocessed),
            StaleObjects: cubes.Sum(c => c.StaleCount) + dimensions.Count(d => d.Status == FreshnessStatus.Stale),
            NewestProcessedUtc: processedPartitions.Count == 0 ? null : processedPartitions.Max(p => p.LastProcessedUtc));

        return new ProcessingState(db.Server, db.Catalog, nowUtc, thresholdHours, db.State,
            cubes, dimensions, summary);
    }

    internal static FreshnessStatus LeafStatus(ObjectState state, DateTime? lastProcessedUtc,
        TimeSpan threshold, DateTime nowUtc)
    {
        if (state != ObjectState.Processed) return FreshnessStatus.Unprocessed;
        // Processed but no date: the age is unknown, so it cannot be vouched for.
        if (lastProcessedUtc is not { } lp) return FreshnessStatus.Stale;
        return nowUtc - lp > threshold ? FreshnessStatus.Stale : FreshnessStatus.Ok;
    }

    private static CubeNode EvaluateCube(CubeSnapshot c, TimeSpan threshold, DateTime nowUtc)
    {
        var groups = c.MeasureGroups.Select(mg => EvaluateMeasureGroup(mg, threshold, nowUtc)).ToList();
        int unprocessed = groups.Sum(g => g.UnprocessedCount);
        int stale = groups.Sum(g => g.StaleCount);
        var (oldest, newest) = Range(groups.SelectMany(g => g.Partitions));
        var status = c.State != ObjectState.Processed || groups.Any(g => g.Status == FreshnessStatus.Unprocessed)
            ? FreshnessStatus.Unprocessed
            : stale > 0 ? FreshnessStatus.Stale : FreshnessStatus.Ok;
        return new CubeNode(c.Name, c.Id, c.State, c.LastProcessedUtc, oldest, newest,
            status, stale, unprocessed, groups);
    }

    private static MeasureGroupNode EvaluateMeasureGroup(MeasureGroupSnapshot mg, TimeSpan threshold, DateTime nowUtc)
    {
        var partitions = mg.Partitions
            .Select(p => new PartitionNode(p.Name, p.Id, p.State, p.LastProcessedUtc, p.StorageMode,
                p.Slice, p.AggregationDesign, p.EstimatedSize, p.EstimatedRows,
                LeafStatus(p.State, p.LastProcessedUtc, threshold, nowUtc)))
            .ToList();
        int unprocessed = partitions.Count(p => p.Status == FreshnessStatus.Unprocessed);
        int stale = partitions.Count(p => p.Status == FreshnessStatus.Stale);
        var (oldest, newest) = Range(partitions);
        var status = mg.State != ObjectState.Processed || unprocessed > 0
            ? FreshnessStatus.Unprocessed
            : stale > 0 ? FreshnessStatus.Stale : FreshnessStatus.Ok;
        return new MeasureGroupNode(mg.Name, mg.Id, mg.State, mg.LastProcessedUtc, oldest, newest,
            mg.EstimatedSize, status, stale, unprocessed, partitions);
    }

    /// <summary>Oldest and newest LastProcessed of the processed partitions (null if none).</summary>
    private static (DateTime? Oldest, DateTime? Newest) Range(IEnumerable<PartitionNode> partitions)
    {
        var dates = partitions
            .Where(p => p.Status != FreshnessStatus.Unprocessed && p.LastProcessedUtc is not null)
            .Select(p => p.LastProcessedUtc!.Value)
            .ToList();
        return dates.Count == 0 ? (null, null) : (dates.Min(), dates.Max());
    }
}
