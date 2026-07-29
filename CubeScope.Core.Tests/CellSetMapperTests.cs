using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class CellSetMapperTests
{
    private static CellData Cell(int i) => new($"c{i}");

    [Fact]
    public void ZeroAxis_SingleScalarCell()
    {
        var r = CellSetMapper.Build(null, null, Cell, cellCount: 1, durationMs: 5);

        Assert.Equal(0, r.AxesCount);
        var col = Assert.Single(r.Columns);
        Assert.False(col.IsRowHeader);
        var row = Assert.Single(r.Rows);
        Assert.Equal("c0", row[col.Field]);
    }

    [Fact]
    public void OneAxis_SingleRowOfValues()
    {
        var cols = new AxisData(["Measures"], [["Sales"], ["Cost"]]);

        var r = CellSetMapper.Build(cols, null, Cell, cellCount: 2, durationMs: 5);

        Assert.Equal(1, r.AxesCount);
        Assert.Equal(2, r.Columns.Count);
        Assert.Equal("Sales", r.Columns[0].Header);
        Assert.Equal("Cost", r.Columns[1].Header);
        var row = Assert.Single(r.Rows);
        Assert.Equal("c0", row["v0"]);
        Assert.Equal("c1", row["v1"]);
    }

    [Fact]
    public void TwoAxes_RowHeadersThenValues_OrdinalIsColumnFirst()
    {
        // 2 colonnes (Sales, Cost) × 2 lignes (ItemA, ItemB) — ordinal = col + ligne * nbCols
        var cols = new AxisData(["Measures"], [["Sales"], ["Cost"]]);
        var rows = new AxisData(["Product"], [["ItemA"], ["ItemB"]]);

        var r = CellSetMapper.Build(cols, rows, Cell, cellCount: 4, durationMs: 5);

        Assert.Equal(2, r.AxesCount);
        Assert.Equal(3, r.Columns.Count); // 1 en-tête de ligne + 2 données
        Assert.True(r.Columns[0].IsRowHeader);
        Assert.Equal("Product", r.Columns[0].Header);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("ItemA", r.Rows[0]["h0"]);
        Assert.Equal("c0", r.Rows[0]["v0"]); // ItemA × Sales
        Assert.Equal("c1", r.Rows[0]["v1"]); // ItemA × Cost
        Assert.Equal("ItemB", r.Rows[1]["h0"]);
        Assert.Equal("c2", r.Rows[1]["v0"]); // ItemB × Sales = ordinal 0 + 1*2
        Assert.Equal("c3", r.Rows[1]["v1"]);
    }

    [Fact]
    public void TwoAxes_CrossjoinOnColumns_JoinsCaptions()
    {
        var cols = new AxisData(["Measures", "Currency"], [["Sales", "EUR"], ["Sales", "USD"]]);
        var rows = new AxisData(["Product"], [["ItemA"]]);

        var r = CellSetMapper.Build(cols, rows, Cell, cellCount: 2, durationMs: 5);

        Assert.Equal("Sales / EUR", r.Columns[1].Header);
        Assert.Equal("Sales / USD", r.Columns[2].Header);
    }

    [Fact]
    public void TwoAxes_MultipleRowHierarchies_OneHeaderColumnEach()
    {
        var cols = new AxisData(["Measures"], [["Sales"]]);
        var rows = new AxisData(["Product", "Currency"], [["ItemA", "EUR"], ["ItemA", "USD"]]);

        var r = CellSetMapper.Build(cols, rows, Cell, cellCount: 2, durationMs: 5);

        Assert.Equal(2, r.Columns.Count(c => c.IsRowHeader));
        Assert.Equal("EUR", r.Rows[0]["h1"]);
        Assert.Equal("USD", r.Rows[1]["h1"]);
    }

    [Theory]
    [InlineData("[Customer].[Segment].&[X]", "Segment")]
    [InlineData("[Measures].[Sales Amount]", "Measures")]
    [InlineData("[Measures]", "Measures")]
    [InlineData("[Dim à ]]crochet].[Hier].&[K]", "Hier")]
    public void HierarchyFromUniqueName_ParsesSecondSegment(string uniqueName, string expected)
        => Assert.Equal(expected, CellSetMapper.HierarchyFromUniqueName(uniqueName));

    [Fact]
    public void ErrorCell_KeepsServerMessageUnderTwinKey()
    {
        // Cellule 1 en erreur : la valeur reste affichable, le message atterrit sous "v1__err"
        var cols = new AxisData(["Measures"], [["Sales"], ["Boom"]]);
        static CellData WithError(int i) => i == 1
            ? new CellData(CellSetMapper.ErrorPlaceholder, "Le type ne correspond pas.")
            : new CellData($"c{i}");

        var r = CellSetMapper.Build(cols, null, WithError, cellCount: 2, durationMs: 5);

        var row = Assert.Single(r.Rows);
        Assert.Equal("c0", row["v0"]);
        Assert.False(row.ContainsKey("v0" + CellSetMapper.ErrorSuffix)); // pas de clé parasite
        Assert.Equal(CellSetMapper.ErrorPlaceholder, row["v1"]);
        Assert.Equal("Le type ne correspond pas.", row["v1" + CellSetMapper.ErrorSuffix]);
    }

    [Fact]
    public void ErrorCell_TwoAxes_TwinKeyFollowsTheRightCell()
    {
        var cols = new AxisData(["Measures"], [["Sales"], ["Cost"]]);
        var rows = new AxisData(["Product"], [["ItemA"], ["ItemB"]]);
        // ordinal 3 = ligne 1 (ItemB), colonne 1 (Cost)
        static CellData WithError(int i) => i == 3
            ? new CellData(CellSetMapper.ErrorPlaceholder, "Division par zéro.")
            : new CellData($"c{i}");

        var r = CellSetMapper.Build(cols, rows, WithError, cellCount: 4, durationMs: 5);

        Assert.False(r.Rows[0].ContainsKey("v1" + CellSetMapper.ErrorSuffix));
        Assert.Equal("Division par zéro.", r.Rows[1]["v1" + CellSetMapper.ErrorSuffix]);
        Assert.False(r.Rows[1].ContainsKey("v0" + CellSetMapper.ErrorSuffix));
    }

    [Fact]
    public void OneAxis_EmptySet_NoColumnsNoRows()
    {
        var cols = new AxisData(["Measures"], []);

        var r = CellSetMapper.Build(cols, null, Cell, cellCount: 0, durationMs: 5);

        Assert.Empty(r.Columns);
        var row = Assert.Single(r.Rows); // une ligne vide, sans champ
        Assert.Empty(row);
    }
}
