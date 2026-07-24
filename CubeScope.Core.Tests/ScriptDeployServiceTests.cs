using CubeScope.Core.Project;

namespace CubeScope.Core.Tests;

public class ScriptDeployServiceTests
{
    [Fact]
    public void TextEquals_ToleratesLineEndingsAndTrailingSpaces()
    {
        Assert.True(ScriptDeployService.TextEquals("CALCULATE;\r\nCREATE MEMBER X;  \r\n", "CALCULATE;\nCREATE MEMBER X;"));
    }

    [Fact]
    public void TextEquals_DetectsRealDifference()
    {
        Assert.False(ScriptDeployService.TextEquals("CALCULATE;", "CALCULATE;\nCREATE MEMBER X;"));
    }
}
