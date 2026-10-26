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

/// <summary>A pasted key found in the cube: the unique name built under the level, and its caption.</summary>
public sealed record ResolvedKey(string Key, string UniqueName, string Caption);

/// <summary>
/// Result of a pasted list. <see cref="Keys"/>: the keys as parsed, in pasted order (what a saved
/// list keeps). <see cref="Missing"/>: the server answered "no such member". <see cref="Failed"/>:
/// the lookup itself failed for these keys (cell or query in error) — told apart, so that a server
/// problem never reads as "your keys do not exist". Nothing is dropped silently.
/// </summary>
public sealed record KeyResolution(
    IReadOnlyList<string> Keys,
    IReadOnlyList<ResolvedKey> Found,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Failed);

/// <summary>A named list of keys saved locally (SQLite), reusable across sessions and servers.</summary>
public sealed record NamedList(long Id, string Name, string Level, IReadOnlyList<string> Keys, DateTime CreatedUtc);

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
