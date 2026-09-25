using CubeScope.Core.Ai;
using static CubeScope.Core.Ai.AiService;

namespace CubeScope.Core.Tests;

/// <summary>
/// The AI actions on a query injected the metadata of the catalog's FIRST cube, whatever the
/// query targeted: on a multi-cube catalog, the model reasoned on another cube's measures.
/// </summary>
public class AiCubeAndProviderTests
{
    [Theory]
    [InlineData("SELECT [Measures].[X] ON 0 FROM [CubeDemo]", "CubeDemo")]
    [InlineData("select {} on 0 from   [Cube Demo] where ([D].[H].&[1])", "Cube Demo")]
    [InlineData("SELECT {} ON 0 FROM [Odd]]Name]", "Odd]Name")]
    [InlineData("SELECT {} ON 0 FROM (SELECT {[D].[H].&[1]} ON 0 FROM [Inner]) WHERE [M].[X]", "Inner")]
    [InlineData("SELECT {} ON 0 FROM CubeDemo", "CubeDemo")]
    [InlineData("SELECT [Measures].[From Date] ON 0 FROM [Real]", "Real")]
    [InlineData("-- FROM [Commented]\nSELECT {} ON 0 FROM [Real]", "Real")]
    [InlineData("/* FROM [Commented] */ SELECT {} ON 0 FROM [Real]", "Real")]
    [InlineData("WITH MEMBER [Measures].[S] AS \"from [Str]\" SELECT {} ON 0 FROM [Real]", "Real")]
    public void Cube_is_read_from_the_from_clause(string mdx, string expected)
        => Assert.Equal(expected, MdxContextBuilder.CubeFromMdx(mdx));

    [Theory]
    [InlineData("SELECT {} ON 0")]
    [InlineData("")]
    public void No_from_clause_gives_null(string mdx)
        => Assert.Null(MdxContextBuilder.CubeFromMdx(mdx));

    [Fact]
    public void Choose_cube_prefers_the_from_clause_case_insensitively()
        => Assert.Equal("Second", MdxContextBuilder.ChooseCube(["First", "Second"], "SELECT {} ON 0 FROM [second]"));

    [Fact]
    public void Choose_cube_falls_back_to_the_first_cube()
    {
        Assert.Equal("First", MdxContextBuilder.ChooseCube(["First", "Second"], "SELECT {} ON 0 FROM [Unknown]"));
        Assert.Equal("First", MdxContextBuilder.ChooseCube(["First"], "no from here"));
        Assert.Null(MdxContextBuilder.ChooseCube([], "SELECT {} ON 0 FROM [X]"));
    }

    [Theory]
    // Expected provider as a string: AiProvider is internal, a public test signature cannot expose it.
    [InlineData(null, null, null, "None")]
    [InlineData(null, null, "sk-ant", "Anthropic")]
    [InlineData("http://localhost:11434/v1", "llama3", null, "OpenAiCompatible")]
    [InlineData("http://localhost:11434/v1", "llama3", "sk-ant", "OpenAiCompatible")]
    [InlineData("http://localhost:11434/v1", null, "sk-ant", "Anthropic")] // model missing
    [InlineData(null, "llama3", "sk-ant", "Anthropic")]                    // base URL missing
    [InlineData("  ", "llama3", "  ", "None")]
    public void Provider_selection(string? baseUrl, string? model, string? anthropicKey, string expected)
        => Assert.Equal(Enum.Parse<AiProvider>(expected), SelectProvider(baseUrl, model, anthropicKey));
}
