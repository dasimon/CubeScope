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
        Assert.DoesNotContain(Parsed, c => c.Kind == "Autre"); // CALCULATE ignored
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

    [Theory]
    [InlineData("STATIC")]
    [InlineData("DYNAMIC")]
    [InlineData("")] // bare CREATE SET
    public void Parse_CreateSet_RecognizesSetModifiers(string modifier)
    {
        // STATIC is even the default set type in MDX: a CREATE STATIC SET
        // must be classified as NamedSet, not "Autre" (observed on a real cube).
        string mdx = $"CREATE {modifier} SET CURRENTCUBE.[Ensemble géré] " +
                     "AS {[Dim].[Hier].[A], [Dim].[Hier].[B]};";
        var cmd = ScriptParser.Parse(mdx).Single();
        Assert.Equal("NamedSet", cmd.Kind);
        Assert.Equal("[Ensemble géré]", cmd.Name);
    }

    [Fact]
    public void Parse_ScopeIsolationProperty_IsNotCountedAsScope()
    {
        // SCOPE_ISOLATION = CUBE is a CREATE MEMBER property: '_' is an identifier
        // character, so "SCOPE" inside it must not open a SCOPE block.
        const string mdx = """
            CREATE MEMBER CURRENTCUBE.[Measures].[A] AS 1,
            FORMAT_STRING = "#,##0", SCOPE_ISOLATION = CUBE;
            CREATE MEMBER CURRENTCUBE.[Measures].[B] AS 2;
            SCOPE([Measures].[A]);
                THIS = 3;
            END SCOPE;
            CREATE MEMBER CURRENTCUBE.[Measures].[C] AS 4;
            """;

        var cmds = ScriptParser.Parse(mdx);

        Assert.Equal(["[Measures].[A]", "[Measures].[B]", "SCOPE([Measures].[A]);", "[Measures].[C]"],
            cmds.Select(c => c.Name));
        Assert.Equal("1", cmds[0].Expression);
    }

    [Fact]
    public void Parse_IdentifierEndingWithScope_IsNotCountedAsScope()
    {
        const string mdx = """
            CREATE MEMBER CURRENTCUBE.[Measures].[A] AS MY_SCOPE + 1;
            CREATE MEMBER CURRENTCUBE.[Measures].[B] AS 2;
            """;

        Assert.Equal(2, ScriptParser.Parse(mdx).Count);
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
        // CA is also a direct dependency of the rate
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
        Assert.Empty(backToA.Dependencies); // the cycle is broken
    }

    [Fact]
    public void Resolve_DeepDiamond_TerminatesQuickly_AndExpandsEachNodeOnce()
    {
        // Layered diamond: every member of layer i references BOTH members of layer i+1.
        // Without memoization the tree doubles at every layer (2^depth expansions).
        const int layers = 30;
        var commands = new List<ScriptCommand>();
        for (int i = 0; i < layers; i++)
            foreach (var side in new[] { "L", "R" })
            {
                string expr = i + 1 < layers ? $"[Measures].[L{i + 1}] + [Measures].[R{i + 1}]" : "1";
                commands.Add(new ScriptCommand("CalculatedMember", $"[Measures].[{side}{i}]", expr, i + 1));
            }
        var script = new CubeScript("C", "", commands);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = DependencyService.Resolve(script, new CubeMeta("C", [], []), "[Measures].[L0]");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        // Each member is expanded (has children) at most once in the whole tree.
        var expanded = new List<string>();
        void Walk(DependencyNode n)
        {
            if (n.Dependencies.Count > 0) expanded.Add(n.Name);
            foreach (var d in n.Dependencies) Walk(d);
        }
        Walk(g.Root);
        Assert.Equal(expanded.Count, expanded.Distinct().Count());
    }

    [Fact]
    public void Resolve_SharedDependency_IsExpandedAtItsShallowestOccurrence()
    {
        // Root → A → Shared, and Root → Shared directly: the direct (shallower) occurrence is expanded.
        var script = new CubeScript("C", "",
        [
            new ScriptCommand("CalculatedMember", "[Measures].[Root]", "[Measures].[A] + [Measures].[Shared]", 1),
            new ScriptCommand("CalculatedMember", "[Measures].[A]", "[Measures].[Shared] * 2", 2),
            new ScriptCommand("CalculatedMember", "[Measures].[Shared]", "[Measures].[Leaf] + 1", 3),
            new ScriptCommand("CalculatedMember", "[Measures].[Leaf]", "1", 4),
        ]);

        var g = DependencyService.Resolve(script, new CubeMeta("C", [], []), "[Measures].[Root]");

        var direct = Assert.Single(g.Root.Dependencies, d => d.Name == "[Measures].[Shared]");
        Assert.Contains(direct.Dependencies, d => d.Name == "[Measures].[Leaf]");
        var a = Assert.Single(g.Root.Dependencies, d => d.Name == "[Measures].[A]");
        var viaA = Assert.Single(a.Dependencies);
        Assert.Equal("[Measures].[Shared]", viaA.Name);
        Assert.Empty(viaA.Dependencies);
    }
}
