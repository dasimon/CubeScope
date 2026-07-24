using CubeScope.Core.Ai;
using CubeScope.Core.Models;

namespace CubeScope.Core.Tests;

public class MdxContextBuilderTests
{
    private static readonly CubeMeta Meta = new(
        "SalesCube",
        [
            new MeasureFolder("", [
                new MeasureMeta("Sales Amount", "[Measures].[Sales Amount]"),
                new MeasureMeta("Order Count", "[Measures].[Order Count]"),
            ]),
        ],
        [
            new DimensionMeta("Product Category", "[Product Category]", [
                new HierarchyMeta("Product Category", "[Product Category].[Product Category]", [
                    new LevelMeta("(All)", "[Product Category].[Product Category].[(All)]", 0),
                    new LevelMeta("Product Category", "[Product Category].[Product Category].[Product Category]", 1),
                ]),
            ]),
            new DimensionMeta("Dates", "[Dates]", [
                new HierarchyMeta("Year - Quarter - Month - Date", "[Dates].[Year - Quarter - Month - Date]", []),
            ]),
        ]);

    [Fact]
    public void ExtractReferences_HandlesEscapedBracketsAndDedup()
    {
        var refs = MdxContextBuilder.ExtractReferences(
            "SELECT { [Measures].[Sales Amount], [MEASURES].[Sales Amount] } ON COLUMNS FROM [Dim à ]]crochet]");

        Assert.Contains("Measures", refs);
        Assert.Contains("Sales Amount", refs);
        Assert.Contains("Dim à ]crochet", refs);
        Assert.Equal(3, refs.Count); // dédup insensible à la casse
    }

    [Fact]
    public void Build_IncludesOnlyReferencedMetadata()
    {
        string ctx = MdxContextBuilder.Build(Meta,
            "SELECT { [Measures].[Sales Amount] } ON COLUMNS, [Product Category].[Product Category].Members ON ROWS FROM [SalesCube]");

        Assert.Contains("[Measures].[Sales Amount]", ctx);
        Assert.DoesNotContain("Order Count", ctx); // non référencée
        Assert.Contains("[Product Category].[Product Category]", ctx);
        Assert.DoesNotContain("[Dates]", ctx); // dimension non référencée
        Assert.Contains("2 mesures", ctx);
    }

    [Fact]
    public void Build_NoReferences_StillGivesCubeSummary()
    {
        string ctx = MdxContextBuilder.Build(Meta, "SELECT");

        Assert.Contains("Cube : [SalesCube]", ctx);
        Assert.DoesNotContain("Mesures référencées", ctx);
        Assert.Contains("2 mesures et 2 dimensions", ctx);
    }
}
