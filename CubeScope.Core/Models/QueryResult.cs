namespace CubeScope.Core.Models;

/// <summary>Column of the results grid. IsRowHeader = column coming from the ROWS axis (member captions).</summary>
public sealed record GridColumn(string Field, string Header, bool IsRowHeader);

/// <summary>Result of an MDX query flattened for the grid (PrimeVue DataTable: Rows = key/value objects).</summary>
public sealed record QueryResult(
    IReadOnlyList<GridColumn> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int CellCount,
    int AxesCount,
    long DurationMs);
