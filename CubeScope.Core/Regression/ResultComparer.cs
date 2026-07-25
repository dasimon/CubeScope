using System.Globalization;
using CubeScope.Core.Models;

namespace CubeScope.Core.Regression;

/// <summary>Une cellule qui diffère entre baseline et relance (Column = en-tête lisible).</summary>
public sealed record CellDiff(int Row, string Column, string? Expected, string? Actual);

/// <summary>Résultat d'une comparaison baseline/relance. Match = colonnes identiques ET
/// même nombre de lignes ET aucune cellule différente. Summary = null quand Match.</summary>
public sealed record ComparisonResult(bool Match, string? Summary, IReadOnlyList<CellDiff> Diffs);

/// <summary>
/// Compare deux <see cref="QueryResult"/> (baseline attendue vs relance) de façon déterministe,
/// sans SSAS. Les cellules sont normalisées en chaîne pour tolérer la frontière JSON : côté
/// serveur, les deux QueryResult passent par System.Text.Json (baseline stockée en JSON,
/// « actual » re-sérialisé) → les valeurs sont des JsonElement des deux côtés et
/// <c>.ToString()</c> donne le texte JSON brut, stable et identique pour des valeurs égales.
/// Pour des valeurs CLR directes (tests), l'invariant sur IFormattable garantit la même stabilité.
/// </summary>
public static class ResultComparer
{
    public static ComparisonResult Compare(QueryResult expected, QueryResult actual, int maxDiffs = 100)
    {
        var expHeaders = expected.Columns.Select(c => c.Header).ToList();
        var actHeaders = actual.Columns.Select(c => c.Header).ToList();
        if (!expHeaders.SequenceEqual(actHeaders))
        {
            var summary = $"colonnes différentes : attendu [{string.Join(", ", expHeaders)}], "
                        + $"obtenu [{string.Join(", ", actHeaders)}]";
            return new ComparisonResult(false, summary, Array.Empty<CellDiff>());
        }

        var diffs = new List<CellDiff>();
        int minRows = Math.Min(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < minRows; i++)
        {
            var er = expected.Rows[i];
            var ar = actual.Rows[i];
            foreach (var col in expected.Columns)
            {
                er.TryGetValue(col.Field, out var ev);
                ar.TryGetValue(col.Field, out var av);
                var es = Norm(ev);
                var as_ = Norm(av);
                if (!string.Equals(es, as_, StringComparison.Ordinal))
                {
                    diffs.Add(new CellDiff(i, col.Header, es, as_));
                    if (diffs.Count >= maxDiffs) goto done;
                }
            }
        }
        done:

        bool rowCountEqual = expected.Rows.Count == actual.Rows.Count;
        bool match = rowCountEqual && diffs.Count == 0;

        string? summary2 = null;
        if (!match)
        {
            var parts = new List<string>();
            if (!rowCountEqual)
                parts.Add($"lignes : attendu {expected.Rows.Count}, obtenu {actual.Rows.Count}");
            if (diffs.Count > 0)
                parts.Add($"{diffs.Count} cellule(s) différente(s)");
            summary2 = string.Join(" ; ", parts);
        }

        return new ComparisonResult(match, summary2, diffs);
    }

    /// <summary>Forme chaîne normalisée d'une cellule. JsonElement → texte JSON brut (stable) ;
    /// valeur CLR IFormattable → culture invariante (nombres stables quelle que soit la locale).</summary>
    private static string? Norm(object? v) => v switch
    {
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}
