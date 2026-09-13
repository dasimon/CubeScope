using CubeScope.Core.Models;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>Lightweight representation of an axis: positions × members (1 member per projected hierarchy).</summary>
public sealed record AxisData(IReadOnlyList<string> HierarchyCaptions, IReadOnlyList<IReadOnlyList<string>> Positions);

/// <summary>
/// Contents of a cell: display value, plus the server's error message when the
/// cell is in error (XMLA then returns &lt;Value&gt;&lt;Error&gt;&lt;Description&gt;…, which ADOMD
/// surfaces as AdomdErrorResponseException on Value AND FormattedValue — observed on a real cube).
/// </summary>
public readonly record struct CellData(object? Value, string? Error = null);

/// <summary>
/// Flattens a CellSet (0, 1 or 2 axes) into a QueryResult for the grid.
/// Known pitfall: a single-axis query has no Axes[1] — always check Axes.Count.
/// </summary>
public static class CellSetMapper
{
    /// <summary>Text displayed instead of the value of a cell in error.</summary>
    public const string ErrorPlaceholder = "#Erreur";

    /// <summary>
    /// Suffix of the "twin" key carrying a cell's error message in the row
    /// (e.g. cell "v3" in error adds "v3__err"). A parallel key rather than a structured
    /// value: no change to the model or the serialization, and the CSV/TSV export
    /// (which only iterates over Columns) naturally ignores it.
    /// </summary>
    public const string ErrorSuffix = "__err";

    public static QueryResult Map(CellSet cs, long durationMs)
    {
        int axes = cs.Axes.Count;
        if (axes > 2)
            throw new NotSupportedException($"Requête à {axes} axes : la grille ne supporte que 0 à 2 axes (Phase 1).");

        AxisData? cols = axes >= 1 ? ReadAxis(cs.Axes[0]) : null;
        AxisData? rows = axes == 2 ? ReadAxis(cs.Axes[1]) : null;
        return Build(cols, rows, i => CellValue(cs.Cells[i]), cs.Cells.Count, durationMs);
    }

    /// <summary>
    /// Display value of a cell. Pitfall: `CELL PROPERTIES VALUE` (queries copied
    /// from Excel/SSMS) restricts the returned properties — FormattedValue is then null,
    /// so fall back to Value (raw). A cell in error must not break everything:
    /// we keep the server's message instead of swallowing it, and the grid shows it as a tooltip.
    /// </summary>
    private static CellData CellValue(Cell cell)
    {
        try
        {
            // FormattedValue is "" (not null) when FORMATTED_VALUE was not requested
            var formatted = cell.FormattedValue;
            return new CellData(string.IsNullOrEmpty(formatted) ? cell.Value : formatted);
        }
        catch (Exception first)
        {
            try { return new CellData(cell.Value); }
            catch (Exception second) { return new CellData(ErrorPlaceholder, Describe(second) ?? Describe(first)); }
        }
    }

    /// <summary>Cleaned-up server message (ADOMD appends trailing spaces to Description).</summary>
    private static string? Describe(Exception ex)
    {
        var message = ex.Message?.Trim();
        return string.IsNullOrEmpty(message) ? null : message;
    }

    private static AxisData ReadAxis(Axis axis)
    {
        var positions = axis.Positions.Cast<Position>()
            .Select(p => (IReadOnlyList<string>)p.Members.Cast<Member>().Select(m => m.Caption).ToList())
            .ToList();
        IReadOnlyList<string> hierarchies;
        try
        {
            hierarchies = axis.Set.Hierarchies.Cast<Hierarchy>().Select(h => h.Caption).ToList();
        }
        catch (Exception)
        {
            // Pitfall: Set.Hierarchies lazily resolves schema objects and can fail
            // (observed on a real cube: ArgumentException "Impossible de trouver l'objet
            // [Dimension].[Hiérarchie]"). Fallback without a server round-trip: derive the label from the
            // UniqueName of the members of the first position, already in the XMLA response.
            hierarchies = positions.Count > 0
                ? axis.Positions[0].Members.Cast<Member>().Select(m => HierarchyFromUniqueName(m.UniqueName)).ToList()
                : [];
        }
        return new AxisData(hierarchies, positions);
    }

    /// <summary>
    /// "[Dim].[Hier].&amp;[X]" → "Hier"; "[Measures].[M]" → "Measures" (the 2nd segment
    /// of a measure is the MEMBER, not the hierarchy — special case).
    /// </summary>
    internal static string HierarchyFromUniqueName(string uniqueName)
    {
        var segments = System.Text.RegularExpressions.Regex
            .Matches(uniqueName, @"\[(?:[^\]]|\]\])*\]")
            .Select(m => m.Value[1..^1].Replace("]]", "]"))
            .ToList();
        if (segments.Count == 0) return uniqueName;
        if (segments[0] == "Measures") return "Measures";
        return segments.Count >= 2 ? segments[1] : segments[0];
    }

    /// <summary>
    /// Pure flattening logic (testable without a server). ADOMD cell ordinal:
    /// ordinal = column + row * columnCount (axis 0 varies fastest).
    /// </summary>
    public static QueryResult Build(AxisData? columnsAxis, AxisData? rowsAxis,
        Func<int, CellData> cellAt, int cellCount, long durationMs)
    {
        var gridColumns = new List<GridColumn>();
        var gridRows = new List<Dictionary<string, object?>>();

        // Writes the value under "v{c}" and, if the cell is in error, the message under "v{c}__err"
        static void SetCell(Dictionary<string, object?> row, string field, CellData cell)
        {
            row[field] = cell.Value;
            if (cell.Error is not null) row[field + ErrorSuffix] = cell.Error;
        }

        // 0 axes: a single scalar cell
        if (columnsAxis is null)
        {
            gridColumns.Add(new GridColumn("v0", "Valeur", IsRowHeader: false));
            var scalar = new Dictionary<string, object?>();
            if (cellCount > 0) SetCell(scalar, "v0", cellAt(0));
            else scalar["v0"] = null;
            gridRows.Add(scalar);
            return new QueryResult(gridColumns, gridRows, cellCount, 0, durationMs);
        }

        // Data columns: one per position on COLUMNS (label = joined captions)
        for (int c = 0; c < columnsAxis.Positions.Count; c++)
            gridColumns.Add(new GridColumn($"v{c}", string.Join(" / ", columnsAxis.Positions[c]), IsRowHeader: false));

        int nCols = columnsAxis.Positions.Count;

        // 1 axis: a single row of values
        if (rowsAxis is null)
        {
            var row = new Dictionary<string, object?>();
            for (int c = 0; c < nCols; c++) SetCell(row, $"v{c}", cellAt(c));
            gridRows.Add(row);
            return new QueryResult(gridColumns, gridRows, cellCount, 1, durationMs);
        }

        // 2 axes: row header columns (one per hierarchy on ROWS) + data
        var headerCols = new List<GridColumn>();
        for (int h = 0; h < rowsAxis.HierarchyCaptions.Count; h++)
            headerCols.Add(new GridColumn($"h{h}", rowsAxis.HierarchyCaptions[h], IsRowHeader: true));
        gridColumns.InsertRange(0, headerCols);

        for (int r = 0; r < rowsAxis.Positions.Count; r++)
        {
            var row = new Dictionary<string, object?>();
            for (int h = 0; h < rowsAxis.Positions[r].Count; h++) row[$"h{h}"] = rowsAxis.Positions[r][h];
            for (int c = 0; c < nCols; c++) SetCell(row, $"v{c}", cellAt(c + r * nCols));
            gridRows.Add(row);
        }
        return new QueryResult(gridColumns, gridRows, cellCount, 2, durationMs);
    }
}
