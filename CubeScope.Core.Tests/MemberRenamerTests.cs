using CubeScope.Core.Script;

namespace CubeScope.Core.Tests;

public class MemberRenamerTests
{
    [Fact]
    public void Rename_DefinitionAndReferences()
    {
        const string script = """
            CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
             AS [Measures].[CA] - [Measures].[Coûts],
            VISIBLE = 1;

            CREATE MEMBER CURRENTCUBE.[Measures].[Taux de marge]
             AS [Measures].[Marge] / [Measures].[CA],
            VISIBLE = 1;
            """;

        var result = MemberRenamer.Rename(script, "[Measures].[Marge]", "[Measures].[Marge brute]");

        Assert.Equal(2, result.Occurrences); // la définition + la référence dans [Taux de marge]
        Assert.DoesNotContain("[Measures].[Marge]", result.NewScript);
        Assert.Contains("[Measures].[Marge brute]", result.NewScript);
        // Les références à [CA] et [Coûts] (autres membres) restent inchangées.
        Assert.Contains("[Measures].[CA]", result.NewScript);
        Assert.Contains("[Measures].[Coûts]", result.NewScript);
    }

    [Fact]
    public void Rename_SkipsStringsAndComments()
    {
        const string script = """
            CREATE MEMBER CURRENTCUBE.[Measures].[Autre] AS 1;
            -- TODO : revoir [Measures].[Marge] plus tard
            CREATE MEMBER CURRENTCUBE.[Measures].[Texte] AS "Référence : [Measures].[Marge]";
            """;

        var result = MemberRenamer.Rename(script, "[Measures].[Marge]", "[Measures].[MargeBrute]");

        Assert.Equal(0, result.Occurrences);
        // Le texte du commentaire et de la chaîne n'a pas bougé.
        Assert.Contains("-- TODO : revoir [Measures].[Marge] plus tard", result.NewScript);
        Assert.Contains("\"Référence : [Measures].[Marge]\"", result.NewScript);
        Assert.DoesNotContain("MargeBrute", result.NewScript);
    }

    [Fact]
    public void Rename_DoesNotPartialMatch()
    {
        const string script = """
            CREATE MEMBER CURRENTCUBE.[Measures].[Marge] AS 1;
            CREATE MEMBER CURRENTCUBE.[Measures].[Marge Ratio] AS [Measures].[Marge] * 2;
            CREATE MEMBER CURRENTCUBE.[Measures].[MargeBis] AS [Measures].[Marge] + 1;
            """;

        var result = MemberRenamer.Rename(script, "[Measures].[Marge]", "[Measures].[MargeX]");

        // Définition + 1 référence dans [Marge Ratio] + 1 référence dans [MargeBis] = 3.
        Assert.Equal(3, result.Occurrences);
        Assert.Contains("[Measures].[Marge Ratio]", result.NewScript);
        Assert.Contains("[Measures].[MargeBis]", result.NewScript);
        Assert.DoesNotContain("[Measures].[Marge]", result.NewScript);
    }

    [Fact]
    public void Rename_ToleratesWhitespaceAroundDots()
    {
        const string script = "CREATE MEMBER CURRENTCUBE.[Measures] . [Marge] AS 1;";

        var result = MemberRenamer.Rename(script, "[Measures].[Marge]", "[Measures].[MargeBrute]");

        Assert.Equal(1, result.Occurrences);
        Assert.Contains("[Measures].[MargeBrute]", result.NewScript);
    }

    [Fact]
    public void Rename_HandlesEscapedBracket()
    {
        const string script = "CREATE MEMBER CURRENTCUBE.[Measures].[A]]B] AS 1;";

        var result = MemberRenamer.Rename(script, "[Measures].[A]]B]", "[Measures].[A]]C]");

        Assert.Equal(1, result.Occurrences);
        Assert.Contains("[Measures].[A]]C]", result.NewScript);
        Assert.DoesNotContain("[A]]B]", result.NewScript);
    }
}
