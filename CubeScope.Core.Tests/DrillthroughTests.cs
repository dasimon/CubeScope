using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class DrillthroughTests
{
    [Fact]
    public void WrapsPlainSelect()
    {
        var stmt = QueryService.BuildDrillthrough("SELECT { [Measures].[Sales] } ON 0 FROM [Cube]", 500);

        Assert.StartsWith("DRILLTHROUGH MAXROWS 500 SELECT", stmt);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(999_999, 100_000)]
    [InlineData(100_000, 100_000)]
    public void ClampsMaxRows(int requested, int expected)
    {
        var stmt = QueryService.BuildDrillthrough("SELECT 1", requested);

        Assert.StartsWith($"DRILLTHROUGH MAXROWS {expected} ", stmt);
    }

    [Fact]
    public void DoesNotDoubleWrapAlreadyDrillthroughStatement()
    {
        const string input = "DRILLTHROUGH MAXROWS 10 SELECT 1";

        var stmt = QueryService.BuildDrillthrough(input, 500);

        Assert.Equal(input, stmt);
    }

    [Fact]
    public void DoesNotDoubleWrap_CaseInsensitive()
    {
        const string input = "drillthrough maxrows 10 select 1";

        var stmt = QueryService.BuildDrillthrough(input, 500);

        Assert.Equal(input, stmt);
    }
}
