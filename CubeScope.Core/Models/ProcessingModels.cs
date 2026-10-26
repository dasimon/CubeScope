using System.Text.Json.Serialization;

namespace CubeScope.Core.Models;

/// <summary>Processing state as reported by the server (AMO <c>AnalysisState</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ObjectState>))]
public enum ObjectState
{
    Processed,
    PartiallyProcessed,
    Unprocessed,
}

/// <summary>
/// Verdict of the staleness alert for one node. <see cref="Unprocessed"/> always wins over
/// <see cref="Stale"/>: an unprocessed object keeps a (meaningless) LastProcessed date.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FreshnessStatus>))]
public enum FreshnessStatus
{
    Ok,
    Stale,
    Unprocessed,
}

// --- Raw snapshot read through AMO (no verdict yet) -----------------------------------

public sealed record PartitionSnapshot(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    string StorageMode,
    string? Slice,
    string? AggregationDesign,
    long EstimatedSize,
    long EstimatedRows);

public sealed record MeasureGroupSnapshot(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    long EstimatedSize,
    IReadOnlyList<PartitionSnapshot> Partitions);

public sealed record CubeSnapshot(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    IReadOnlyList<MeasureGroupSnapshot> MeasureGroups);

public sealed record DimensionSnapshot(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc);

public sealed record DatabaseSnapshot(
    string Server,
    string Catalog,
    ObjectState State,
    IReadOnlyList<CubeSnapshot> Cubes,
    IReadOnlyList<DimensionSnapshot> Dimensions);

// --- Evaluated state returned to the UI ------------------------------------------------

public sealed record PartitionNode(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    string StorageMode,
    string? Slice,
    string? AggregationDesign,
    long EstimatedSize,
    long EstimatedRows,
    FreshnessStatus Status);

/// <summary>
/// A measure group. Its freshness comes from its partitions (oldest and newest
/// LastProcessed): the server value of a parent is not trusted to follow its partitions.
/// </summary>
public sealed record MeasureGroupNode(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    DateTime? OldestPartitionUtc,
    DateTime? NewestPartitionUtc,
    long EstimatedSize,
    FreshnessStatus Status,
    int StaleCount,
    int UnprocessedCount,
    IReadOnlyList<PartitionNode> Partitions);

public sealed record CubeNode(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    DateTime? OldestPartitionUtc,
    DateTime? NewestPartitionUtc,
    FreshnessStatus Status,
    int StaleCount,
    int UnprocessedCount,
    IReadOnlyList<MeasureGroupNode> MeasureGroups);

public sealed record DimensionNode(
    string Name,
    string Id,
    ObjectState State,
    DateTime? LastProcessedUtc,
    FreshnessStatus Status);

/// <summary>
/// Status bar summary. <see cref="NewestProcessedUtc"/> = newest partition of the database:
/// it answers "did last night's processing run?". The database's own LastProcessed is not
/// used — a cube process does not update it (observed).
/// </summary>
public sealed record ProcessingSummary(
    int UnprocessedCubes,
    int UnprocessedPartitions,
    int UnprocessedDimensions,
    int StaleObjects,
    DateTime? NewestProcessedUtc);

public sealed record ProcessingState(
    string Server,
    string Catalog,
    DateTime ReadAtUtc,
    int ThresholdHours,
    ObjectState DatabaseState,
    IReadOnlyList<CubeNode> Cubes,
    IReadOnlyList<DimensionNode> Dimensions,
    ProcessingSummary Summary);
