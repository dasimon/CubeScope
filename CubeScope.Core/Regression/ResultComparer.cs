using System.Globalization;
using CubeScope.Core.Models;

namespace CubeScope.Core.Regression;

/// <summary>A cell that differs between the baseline and the re-run (Column = readable header).</summary>
public sealed record CellDiff(int Row, string Column, string? Expected, string? Actual);

/// <summary>Result of a baseline/re-run comparison. Match = identical columns AND
/// same row count AND no differing cell. Summary = null when Match.</summary>
public sealed record ComparisonResult(bool Match, string? Summary, IReadOnlyList<CellDiff> Diffs);

/// <summary>
/// Compares two <see cref="QueryResult"/> (expected baseline vs re-run) deterministically,
/// without SSAS. Cells are normalized to strings to tolerate the JSON boundary: on the
/// server side, both QueryResults go through System.Text.Json (baseline stored as JSON,
/// "actual" re-serialized) → values are JsonElements on both sides and
/// <c>.ToString()</c> gives the raw JSON text, stable and identical for equal values.
/// For direct CLR values (tests), the invariant culture on IFormattable guarantees the same stability.
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

    /// <summary>Normalized string form of a cell. JsonElement → raw JSON text (stable);
    /// IFormattable CLR value → invariant culture (numbers stable whatever the locale).</summary>
    private static string? Norm(object? v) => v switch
    {
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}
