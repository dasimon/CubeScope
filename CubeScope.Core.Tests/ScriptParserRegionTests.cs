using CubeScope.Core.Models;
using CubeScope.Core.Script;

namespace CubeScope.Core.Tests;

public class ScriptParserRegionTests
{
    private const string RegionScript = """
        CALCULATE;

        // #region Rentabilité
        CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
         AS [Measures].[CA] - [Measures].[Coûts],
        VISIBLE = 1;

        -- #region Détail
        CREATE MEMBER CURRENTCUBE.[Measures].[Taux]
         AS [Measures].[Marge] / [Measures].[CA];
        -- #endregion
        // #endregion

        CREATE SET CURRENTCUBE.[Fonds ouverts]
         AS [Portefeuille].[Portefeuille].Members;

        // #region Jamais fermée
        SCOPE([Dates].[Année].&[2026]);
            THIS = 0;
        END SCOPE;
        """;

    private static IReadOnlyList<ScriptCommand> Parsed => ScriptParser.Parse(RegionScript);

    [Fact]
    public void Section_SimpleRegion()
        => Assert.Equal("Rentabilité", Parsed.Single(c => c.Name == "[Measures].[Marge]").Section);

    [Fact]
    public void Section_NestedRegion_UsesFullPath()
        => Assert.Equal("Rentabilité / Détail", Parsed.Single(c => c.Name == "[Measures].[Taux]").Section);

    [Fact]
    public void Section_OutsideRegion_IsNull()
        => Assert.Null(Parsed.Single(c => c.Name == "[Fonds ouverts]").Section);

    [Fact]
    public void Section_UnclosedRegion_RunsToEnd()
        => Assert.Equal("Jamais fermée", Parsed.Single(c => c.Kind == "Scope").Section);

    [Fact]
    public void Section_EndregionWithoutStart_IsIgnored()
    {
        var cmds = ScriptParser.Parse("-- #endregion\nCREATE SET CURRENTCUBE.[S] AS [D].[H].Members;");
        Assert.Null(cmds.Single().Section);
    }

    [Fact]
    public void Section_FirstStatement_WithLeadingBlankLines_UsesRegion()
    {
        // Script commençant par des lignes blanches AVANT le premier #region : le
        // premier statement doit quand même se voir attribuer la bonne région.
        var cmds = ScriptParser.Parse("\n\n// #region X\nCREATE SET CURRENTCUBE.[S] AS [D].[H].Members;");
        Assert.Equal("X", cmds.Single().Section);
    }

    [Fact]
    public void Section_FirstStatement_WithLeadingBlankLines_OutsideRegion_IsNull()
    {
        // Même scénario mais sans aucune région dans le script : doit rester null.
        var cmds = ScriptParser.Parse("\n\nCREATE SET CURRENTCUBE.[S] AS [D].[H].Members;");
        Assert.Null(cmds.Single().Section);
    }
}
