namespace CubeScope.Core.Models;

/// <summary>Ranking of an axis item on a measure: ORDER (TopCount null) or TOPCOUNT.</summary>
public sealed record BuilderRank(string Measure, int? TopCount, bool Descending = true);

/// <summary>
/// One hierarchy on an axis. Either a whole level (<see cref="Level"/>, Members empty) or chosen
/// members (<see cref="Members"/>, which take precedence). One item per hierarchy and per axis.
/// </summary>
public sealed record BuilderAxisItem(
    string Hierarchy,
    string? Level,
    IReadOnlyList<string>? Members,
    BuilderRank? Rank);

/// <summary>Filter on one hierarchy: one member → WHERE, several → sub-select.</summary>
public sealed record BuilderFilter(string Hierarchy, IReadOnlyList<string> Members);

/// <summary>
/// Query composed in the graphical builder. Measures go on COLUMNS, innermost (crossed after
/// the column hierarchies). At most two axes: the limit of <c>CellSetMapper</c>.
/// </summary>
public sealed record BuilderModel(
    string Cube,
    IReadOnlyList<string> Measures,
    IReadOnlyList<BuilderAxisItem> Columns,
    IReadOnlyList<BuilderAxisItem> Rows,
    IReadOnlyList<BuilderFilter> Filters,
    bool NonEmptyColumns = true,
    bool NonEmptyRows = true);
