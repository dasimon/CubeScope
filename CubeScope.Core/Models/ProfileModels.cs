namespace CubeScope.Core.Models;

/// <summary>A retained SSAS trace event ("completed" events only).</summary>
public sealed record ProfileEvent(
    string EventClass,
    int Subclass,
    long DurationMs,
    string? TextData,
    DateTime CapturedUtc);

/// <summary>Profiler-style breakdown of a query: Formula Engine vs Storage Engine.</summary>
public sealed record QueryProfile(
    long TotalMs,
    long StorageEngineMs,
    long FormulaEngineMs,
    int SubcubeCount,
    int CacheHits,
    int AggregationHits,
    IReadOnlyList<SubcubeInfo> Subcubes);

/// <summary>A Query Subcube: duration + description (the grid requested from the Storage Engine).</summary>
public sealed record SubcubeInfo(long DurationMs, string Text);

public enum ProfilerStatus
{
    NotInitialized,
    Ready,
    Unavailable,
}
