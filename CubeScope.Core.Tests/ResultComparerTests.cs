using System.Text.Json;
using CubeScope.Core.Models;
using CubeScope.Core.Regression;

namespace CubeScope.Core.Tests;

public class ResultComparerTests
{
    private static QueryResult Make((string field, string header)[] cols, params Dictionary<string, object?>[] rows)
        => new(cols.Select(c => new GridColumn(c.field, c.header, false)).ToList(), rows, rows.Length, 2, 5);

    private static Dictionary<string, object?> Row(params (string k, object? v)[] cells)
        => cells.ToDictionary(c => c.k, c => c.v);

    private static readonly (string, string)[] Cols = [("c0", "Devise"), ("m", "VL")];

    [Fact]
    public void Identical_Results_Match_NoDiffs()
    {
        var a = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)), Row(("c0", "USD"), ("m", 2.0)));
        var b = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)), Row(("c0", "USD"), ("m", 2.0)));

        var r = ResultComparer.Compare(a, b);

        Assert.True(r.Match);
        Assert.Empty(r.Diffs);
        Assert.Null(r.Summary);
    }

    [Fact]
    public void OneChangedCell_Match_False_WithDiff()
    {
        var a = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)));
        var b = Make(Cols, Row(("c0", "EUR"), ("m", 1.6)));

        var r = ResultComparer.Compare(a, b);

        Assert.False(r.Match);
        var d = Assert.Single(r.Diffs);
        Assert.Equal(0, d.Row);
        Assert.Equal("VL", d.Column);
        Assert.Equal("1.5", d.Expected);
        Assert.Equal("1.6", d.Actual);
    }

    [Fact]
    public void DifferentRowCount_Match_False_Summary_StillDiffsOverlap()
    {
        var a = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)));
        var b = Make(Cols, Row(("c0", "EUR"), ("m", 9.9)), Row(("c0", "USD"), ("m", 2.0)));

        var r = ResultComparer.Compare(a, b);

        Assert.False(r.Match);
        Assert.Contains("attendu 1", r.Summary!);
        Assert.Contains("obtenu 2", r.Summary!);
        // La ligne qui se recoupe est quand même comparée
        var d = Assert.Single(r.Diffs);
        Assert.Equal(0, d.Row);
        Assert.Equal("VL", d.Column);
    }

    [Fact]
    public void DifferentColumns_Match_False_Summary_NoCellDiffs()
    {
        var a = Make([("c0", "Devise"), ("m", "VL")], Row(("c0", "EUR"), ("m", 1.5)));
        var b = Make([("c0", "Devise"), ("m", "Encours")], Row(("c0", "EUR"), ("m", 1.5)));

        var r = ResultComparer.Compare(a, b);

        Assert.False(r.Match);
        Assert.Contains("colonnes", r.Summary!);
        Assert.Empty(r.Diffs);
    }

    [Fact]
    public void MaxDiffs_Cap_Respected()
    {
        var cols = new[] { ("c0", "H"), ("m", "V") };
        var rowsA = Enumerable.Range(0, 10).Select(i => Row(("c0", "x"), ("m", (object?)i))).ToArray();
        var rowsB = Enumerable.Range(0, 10).Select(i => Row(("c0", "x"), ("m", (object?)(i + 100)))).ToArray();

        var r = ResultComparer.Compare(Make(cols, rowsA), Make(cols, rowsB), maxDiffs: 3);

        Assert.False(r.Match);
        Assert.Equal(3, r.Diffs.Count);
    }

    // Le crux : après round-trip JSON, les cellules sont des JsonElement des DEUX côtés.
    // .ToString() sur un JsonElement numérique/chaîne est stable → l'égalité tient à travers
    // la frontière JSON, et un vrai changement est toujours détecté.
    [Fact]
    public void JsonRoundTrip_NumbersAndStrings_CompareConsistently()
    {
        var live = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)), Row(("c0", "USD"), ("m", 1234.0)));
        var expected = JsonSerializer.Deserialize<QueryResult>(JsonSerializer.Serialize(live))!;
        var actual = JsonSerializer.Deserialize<QueryResult>(JsonSerializer.Serialize(live))!;

        var same = ResultComparer.Compare(expected, actual);
        Assert.True(same.Match, same.Summary);

        var changed = Make(Cols, Row(("c0", "EUR"), ("m", 1.5)), Row(("c0", "USD"), ("m", 1235.0)));
        var actual2 = JsonSerializer.Deserialize<QueryResult>(JsonSerializer.Serialize(changed))!;
        var diff = ResultComparer.Compare(expected, actual2);

        Assert.False(diff.Match);
        var d = Assert.Single(diff.Diffs);
        Assert.Equal(1, d.Row);
        Assert.Equal("VL", d.Column);
        Assert.Equal("1234", d.Expected);
        Assert.Equal("1235", d.Actual);
    }
}
