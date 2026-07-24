namespace CubeScope.Core.Models;

/// <summary>Colonne de la grille de résultats. IsRowHeader = colonne issue de l'axe ROWS (captions de membres).</summary>
public sealed record GridColumn(string Field, string Header, bool IsRowHeader);

/// <summary>Résultat d'une requête MDX aplati pour la grille (PrimeVue DataTable : Rows = objets clé/valeur).</summary>
public sealed record QueryResult(
    IReadOnlyList<GridColumn> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int CellCount,
    int AxesCount,
    long DurationMs);
