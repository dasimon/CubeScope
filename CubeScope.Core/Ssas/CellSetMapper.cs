using CubeScope.Core.Models;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>Représentation légère d'un axe : positions × membres (1 membre par hiérarchie projetée).</summary>
public sealed record AxisData(IReadOnlyList<string> HierarchyCaptions, IReadOnlyList<IReadOnlyList<string>> Positions);

/// <summary>
/// Aplatit un CellSet (0, 1 ou 2 axes) en QueryResult pour la grille.
/// Piège connu : une requête mono-axe n'a pas d'Axes[1] — toujours tester Axes.Count.
/// </summary>
public static class CellSetMapper
{
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
    /// Valeur d'affichage d'une cellule. Piège : `CELL PROPERTIES VALUE` (requêtes copiées
    /// d'Excel/SSMS) restreint les propriétés renvoyées — FormattedValue est alors null,
    /// il faut se replier sur Value (brut). Une cellule en erreur ne doit pas tout casser.
    /// </summary>
    private static object? CellValue(Cell cell)
    {
        try
        {
            // FormattedValue vaut "" (pas null) quand FORMATTED_VALUE n'a pas été demandé
            var formatted = cell.FormattedValue;
            return string.IsNullOrEmpty(formatted) ? cell.Value : formatted;
        }
        catch
        {
            try { return cell.Value; } catch { return "#ERREUR"; }
        }
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
            // Piège : Set.Hierarchies résout paresseusement les objets schéma et peut échouer
            // (constaté sur un cube réel : ArgumentException "Impossible de trouver l'objet
            // [Dimension].[Hiérarchie]"). Repli sans round-trip serveur : déduire le libellé des
            // UniqueName des membres de la première position, déjà dans la réponse XMLA.
            hierarchies = positions.Count > 0
                ? axis.Positions[0].Members.Cast<Member>().Select(m => HierarchyFromUniqueName(m.UniqueName)).ToList()
                : [];
        }
        return new AxisData(hierarchies, positions);
    }

    /// <summary>
    /// "[Dim].[Hier].&amp;[X]" → "Hier" ; "[Measures].[M]" → "Measures" (le 2ᵉ segment
    /// d'une mesure est le MEMBRE, pas la hiérarchie — cas particulier).
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
    /// Logique pure d'aplatissement (testable sans serveur). Ordinal des cellules ADOMD :
    /// ordinal = colonne + ligne * nbColonnes (l'axe 0 varie le plus vite).
    /// </summary>
    public static QueryResult Build(AxisData? columnsAxis, AxisData? rowsAxis,
        Func<int, object?> cellAt, int cellCount, long durationMs)
    {
        var gridColumns = new List<GridColumn>();
        var gridRows = new List<Dictionary<string, object?>>();

        // 0 axe : une seule cellule scalaire
        if (columnsAxis is null)
        {
            gridColumns.Add(new GridColumn("v0", "Valeur", IsRowHeader: false));
            gridRows.Add(new Dictionary<string, object?> { ["v0"] = cellCount > 0 ? cellAt(0) : null });
            return new QueryResult(gridColumns, gridRows, cellCount, 0, durationMs);
        }

        // Colonnes de données : une par position sur COLUMNS (libellé = captions joints)
        for (int c = 0; c < columnsAxis.Positions.Count; c++)
            gridColumns.Add(new GridColumn($"v{c}", string.Join(" / ", columnsAxis.Positions[c]), IsRowHeader: false));

        int nCols = columnsAxis.Positions.Count;

        // 1 axe : une seule ligne de valeurs
        if (rowsAxis is null)
        {
            var row = new Dictionary<string, object?>();
            for (int c = 0; c < nCols; c++) row[$"v{c}"] = cellAt(c);
            gridRows.Add(row);
            return new QueryResult(gridColumns, gridRows, cellCount, 1, durationMs);
        }

        // 2 axes : colonnes d'en-tête de ligne (une par hiérarchie sur ROWS) + données
        var headerCols = new List<GridColumn>();
        for (int h = 0; h < rowsAxis.HierarchyCaptions.Count; h++)
            headerCols.Add(new GridColumn($"h{h}", rowsAxis.HierarchyCaptions[h], IsRowHeader: true));
        gridColumns.InsertRange(0, headerCols);

        for (int r = 0; r < rowsAxis.Positions.Count; r++)
        {
            var row = new Dictionary<string, object?>();
            for (int h = 0; h < rowsAxis.Positions[r].Count; h++) row[$"h{h}"] = rowsAxis.Positions[r][h];
            for (int c = 0; c < nCols; c++) row[$"v{c}"] = cellAt(c + r * nCols);
            gridRows.Add(row);
        }
        return new QueryResult(gridColumns, gridRows, cellCount, 2, durationMs);
    }
}
