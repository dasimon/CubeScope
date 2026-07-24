namespace CubeScope.Core.Models;

/// <summary>Un événement de trace SSAS retenu (événements « complétés » uniquement).</summary>
public sealed record ProfileEvent(
    string EventClass,
    int Subclass,
    long DurationMs,
    string? TextData,
    DateTime CapturedUtc);

/// <summary>Découpage type profiler d'une requête : Formula Engine vs Storage Engine.</summary>
public sealed record QueryProfile(
    long TotalMs,
    long StorageEngineMs,
    long FormulaEngineMs,
    int SubcubeCount,
    int CacheHits,
    int AggregationHits,
    IReadOnlyList<SubcubeInfo> Subcubes);

/// <summary>Un Query Subcube : durée + description (la grille demandée au Storage Engine).</summary>
public sealed record SubcubeInfo(long DurationMs, string Text);

public enum ProfilerStatus
{
    NotInitialized,
    Ready,
    Unavailable,
}
