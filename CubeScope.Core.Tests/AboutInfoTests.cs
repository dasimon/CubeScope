using CubeScope.Server;

namespace CubeScope.Core.Tests;

public class AboutInfoTests
{
    [Theory]
    [InlineData("0.15.0+1c98c306d36cb4c05922c62e4a2cbb44d17a117c", "0.15.0", "1c98c30")]
    [InlineData("0.15.0+abc", "0.15.0", "abc")]
    [InlineData("0.15.0", "0.15.0", null)]
    [InlineData("0.15.0+", "0.15.0", null)]
    [InlineData(null, "?", null)]
    [InlineData("  ", "?", null)]
    public void SplitVersion_SeparatesVersionAndShortCommit(string? informational, string version, string? commit)
    {
        var (v, c) = AboutInfo.SplitVersion(informational);
        Assert.Equal(version, v);
        Assert.Equal(commit, c);
    }

    [Fact]
    public void Build_ReportsTheAssemblyVersionAndDataFolder()
    {
        var about = AboutInfo.Build("srv", "17.0.25.218");

        Assert.NotEqual("?", about.Version);
        Assert.StartsWith(".NET", about.Runtime);
        Assert.Equal(Path.GetDirectoryName(about.DatabasePath), about.DataFolder);
        Assert.Equal("srv", about.SsasServer);
        Assert.Equal("17.0.25.218", about.SsasVersion);
    }
}
