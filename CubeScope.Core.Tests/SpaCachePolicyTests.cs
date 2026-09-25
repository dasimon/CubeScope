using CubeScope.Server;

namespace CubeScope.Core.Tests;

public class SpaCachePolicyTests
{
    [Theory]
    [InlineData("/", "index.html", "no-cache")]
    [InlineData("/index.html", "index.html", "no-cache")]
    [InlineData("/script/some-client-route", "index.html", "no-cache")]
    [InlineData("/assets/index-C9oTUMLy.js", "index-C9oTUMLy.js", "public, max-age=31536000, immutable")]
    [InlineData("/favicon.svg", "favicon.svg", null)]
    public void For_RevalidatesHtml_AndKeepsHashedAssets(string path, string file, string? expected) =>
        Assert.Equal(expected, SpaCachePolicy.For(path, file));
}
