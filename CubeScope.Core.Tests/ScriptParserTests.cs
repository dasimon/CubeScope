using CubeScope.Core.Models;
using CubeScope.Core.Script;

namespace CubeScope.Core.Tests;

public class ScriptParserTests
{
    private const string SampleScript = """
        CALCULATE;

        CREATE MEMBER CURRENTCUBE.[Measures].[Marge]
         AS [Measures].[CA] - [Measures].[Coûts],
        FORMAT_STRING = "#,##0.00",
        VISIBLE = 1;

        CREATE MEMBER CURRENTCUBE.[Measures].[Taux de marge]
         AS IIF([Measures].[CA] = 0, NULL, [Measures].[Marge] / [Measures].[CA]),
        FORMAT_STRING = "Percent",
        VISIBLE = 1, DISPLAY_FOLDER = 'Rentabilité';

        CREATE SET CURRENTCUBE.[Fonds ouverts]
         AS Filter([Portefeuille].[Portefeuille].Members, [Measures].[Actif] > 0);

        SCOPE([Dates].[Année].&[2026]);
            THIS = [Measures].[CA] * 1.1;
        END SCOPE;

        -- commentaire avec un ; dedans
        CREATE MEMBER CURRENTCUBE.[Measures].[Après commentaire]
         AS "point-virgule ; dans une chaîne",
        VISIBLE = 0;
        """;

    private static IReadOnlyList<ScriptCommand> Parsed => ScriptParser.Parse(SampleScript);

    [Fact]
    public void Parse_FindsAllCommandKinds()
    {
        Assert.Equal(3, Parsed.Count(c => c.Kind == "CalculatedMember"));
        Assert.Equal(1, Parsed.Count(c => c.Kind == "NamedSet"));
        Assert.Equal(1, Parsed.Count(c => c.Kind == "Scope"));
        Assert.DoesNotContain(Parsed, c => c.Kind == "Autre"); // CALCULATE ignoré
    }

    [Fact]
    public void Parse_MemberExpression_StopsAtPropertyList()
    {
        var marge = Parsed.Single(c => c.Name == "[Measures].[Marge]");
        Assert.Equal("[Measures].[CA] - [Measures].[Coûts]", marge.Expression);
    }

    [Fact]
    public void Parse_MemberExpression_KeepsCommasInsideFunctions()
    {
        var taux = Parsed.Single(c => c.Name == "[Measures].[Taux de marge]");
        Assert.StartsWith("IIF(", taux.Expression);
        Assert.EndsWith(")", taux.Expression);
        Assert.DoesNotContain("FORMAT_STRING", taux.Expression);
    }

    [Fact]
    public void Parse_ScopeBlock_IsOneCommand_SemicolonsInsideKept()
    {
        var scope = Parsed.Single(c => c.Kind == "Scope");
        Assert.Contains("THIS =", scope.Expression);
        Assert.Contains("END SCOPE", scope.Expression, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_IgnoresSemicolonsInCommentsAndStrings()
    {
        var after = Parsed.Single(c => c.Name == "[Measures].[Après commentaire]");
        Assert.Contains("point-virgule ; dans une chaîne", after.Expression);
    }

    [Fact]
    public void Parse_StartLines_AreIncreasing()
    {
        var lines = Parsed.Select(c => c.StartLine).ToList();
        Assert.Equal(lines.OrderBy(l => l).ToList(), lines);
        Assert.True(lines.First() >= 1);
    }
}

public class DependencyServiceTests
{
    private static readonly CubeMeta Meta = new("Ventes",
        [new MeasureFolder("", [
            new MeasureMeta("CA", "[Measures].[CA]"),
            new MeasureMeta("Coûts", "[Measures].[Coûts]"),
            new MeasureMeta("Actif", "[Measures].[Actif]"),
        ])],
        [new DimensionMeta("Portefeuille", "[Portefeuille]", [
            new HierarchyMeta("Portefeuille", "[Portefeuille].[Portefeuille]", []),
        ])]);

    private static readonly CubeScript Script = new("Ventes", "",
        [
            new ScriptCommand("CalculatedMember", "[Measures].[Marge]", "[Measures].[CA] - [Measures].[Coûts]", 1),
            new ScriptCommand("CalculatedMember", "[Measures].[Taux de marge]", "[Measures].[Marge] / [Measures].[CA]", 5),
            new ScriptCommand("NamedSet", "[Fonds ouverts]", "Filter([Portefeuille].[Portefeuille].Members, [Measures].[Actif] > 0)", 9),
        ]);

    [Fact]
    public void Resolve_BuildsRecursiveTree()
    {
        var g = DependencyService.Resolve(Script, Meta, "[Measures].[Taux de marge]");

        var margeNode = Assert.Single(g.Root.Dependencies, d => d.Name == "[Measures].[Marge]");
        Assert.Contains(margeNode.Dependencies, d => d.Name == "[Measures].[CA]" && d.Kind == "Measure");
        Assert.Contains(margeNode.Dependencies, d => d.Name == "[Measures].[Coûts]");
        // CA est aussi dépendance directe du taux
        Assert.Contains(g.Root.Dependencies, d => d.Name == "[Measures].[CA]");
    }

    [Fact]
    public void Resolve_UsedBy_FindsReverseDependents()
    {
        var g = DependencyService.Resolve(Script, Meta, "[Measures].[Marge]");
        Assert.Contains("[Measures].[Taux de marge]", g.UsedBy);
        Assert.DoesNotContain("[Fonds ouverts]", g.UsedBy);
    }

    [Fact]
    public void Resolve_SetReferencingHierarchy_YieldsHierarchyLeaf()
    {
        var g = DependencyService.Resolve(Script, Meta, "[Fonds ouverts]");
        Assert.Contains(g.Root.Dependencies, d => d.Kind == "Hierarchy" && d.Name == "[Portefeuille].[Portefeuille]");
        Assert.Contains(g.Root.Dependencies, d => d.Kind == "Measure" && d.Name == "[Measures].[Actif]");
    }

    [Fact]
    public void Resolve_CycleDoesNotLoopForever()
    {
        var cyclic = new CubeScript("C", "",
        [
            new ScriptCommand("CalculatedMember", "[Measures].[A]", "[Measures].[B] + 1", 1),
            new ScriptCommand("CalculatedMember", "[Measures].[B]", "[Measures].[A] + 1", 2),
        ]);
        var meta = new CubeMeta("C", [], []);

        var g = DependencyService.Resolve(cyclic, meta, "[Measures].[A]");

        var b = Assert.Single(g.Root.Dependencies);
        Assert.Equal("[Measures].[B]", b.Name);
        var backToA = Assert.Single(b.Dependencies);
        Assert.Empty(backToA.Dependencies); // le cycle est coupé
    }
}
