using System.Reflection;
using CubeScope.Server;

namespace CubeScope.Core.Tests;

/// <summary>
/// The published exe serves its SPA from embedded resources only: if this provider stops
/// resolving a path, the moved exe answers 404 on index.html again (the v0.1.0 failure).
/// The test assembly embeds a miniature SPA under the same "spa/" prefix.
/// </summary>
public class EmbeddedSpaFileProviderTests
{
    private readonly EmbeddedSpaFileProvider _spa = new(Assembly.GetExecutingAssembly(), "spa/");

    [Fact]
    public void Counts_only_the_resources_under_the_prefix()
        => Assert.Equal(2, _spa.Count);

    [Theory]
    [InlineData("/index.html")]
    [InlineData("index.html")]
    [InlineData("/assets/app.js")]
    [InlineData("/ASSETS/App.js")]      // URL case differs from the resource name
    [InlineData(@"\assets\app.js")]
    public void Resolves_embedded_files(string subpath)
    {
        var file = _spa.GetFileInfo(subpath);
        Assert.True(file.Exists);
        Assert.False(file.IsDirectory);
        Assert.Null(file.PhysicalPath);
    }

    [Fact]
    public void Serves_the_content_and_its_length()
    {
        var file = _spa.GetFileInfo("/assets/app.js");
        using var reader = new StreamReader(file.CreateReadStream());
        string content = reader.ReadToEnd();
        Assert.Contains("console.log", content);
        Assert.Equal("app.js", file.Name);
        Assert.True(file.Length > 0);
    }

    [Theory]
    [InlineData("/missing.html")]
    [InlineData("/assets")]
    [InlineData("/../index.html")]
    public void Unknown_paths_are_not_found(string subpath)
        => Assert.False(_spa.GetFileInfo(subpath).Exists);

    [Fact]
    public void An_assembly_without_the_prefix_gives_an_empty_provider()
        => Assert.Equal(0, new EmbeddedSpaFileProvider(typeof(ServerHost).Assembly, "spa/").Count);
}
