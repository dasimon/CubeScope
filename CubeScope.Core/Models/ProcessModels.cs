using System.Text.Json.Serialization;

namespace CubeScope.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ProcessObjectKind>))]
public enum ProcessObjectKind
{
    Cube,
    MeasureGroup,
    Partition,
    Dimension,
}

/// <summary>
/// Processing modes offered by the State panel (owner's decisions, 2026-10-26):
/// no ProcessAdd — without an out-of-line source query it re-reads the whole partition source
/// and doubles the data; no ProcessClear — it empties the object and leaves it unqueryable,
/// with no use in daily work. Both remain possible through the XMLA console (dev only for Clear).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessMode>))]
public enum ProcessMode
{
    Default,
    Full,
    Data,
    Update,
}

/// <summary>The object to process, addressed by its IDs (an ID differs from the name after a rename).</summary>
public sealed record ProcessTarget(
    ProcessObjectKind Kind,
    string? CubeId = null,
    string? MeasureGroupId = null,
    string? PartitionId = null,
    string? DimensionId = null);

/// <param name="ConfirmServer">Production only: the server name typed again by the user.</param>
public sealed record ProcessRequest(ProcessTarget Target, ProcessMode Mode, string? ConfirmServer = null);

/// <summary>
/// What a run would do: the captured XMLA (exactly what is sent), whether the server counts as
/// production, the measure groups a dimension process would leave unprocessed, and the refusal
/// the guard would give whatever the confirmation (null if none).
/// </summary>
public sealed record ProcessPreview(
    string ObjectLabel,
    string Xmla,
    bool IsProduction,
    IReadOnlyList<string> AffectedMeasureGroups,
    string? Refusal);

public sealed record ProcessMessage(string Severity, string Text);

public sealed record ProcessResult(bool Success, long DurationMs, IReadOnlyList<ProcessMessage> Messages);

public sealed record ProcessLogEntry(
    long Id,
    string Server,
    string Catalog,
    string ObjectLabel,
    string Mode,
    bool IsProduction,
    long DurationMs,
    bool Success,
    string? Error,
    DateTime ExecutedUtc);
