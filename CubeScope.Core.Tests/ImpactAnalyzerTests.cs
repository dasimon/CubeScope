using CubeScope.Core.Script;

namespace CubeScope.Core.Tests;

public class ImpactAnalyzerTests
{
    private static string Member(string name, string expr) =>
        $"CREATE MEMBER CURRENTCUBE.{name} AS {expr};";

    [Fact]
    public void Analyze_DetectsAddedRemovedChanged()
    {
        string oldScript = $"""
            CALCULATE;
            {Member("[Measures].[A]", "1")}
            {Member("[Measures].[B]", "2")}
            """;
        string newScript = $"""
            CALCULATE;
            {Member("[Measures].[B]", "3")}
            {Member("[Measures].[C]", "4")}
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        Assert.Equal(3, report.Changes.Count);
        var a = Assert.Single(report.Changes, c => c.Name == "[Measures].[A]");
        Assert.Equal(ChangeKind.Removed, a.Change);
        var b = Assert.Single(report.Changes, c => c.Name == "[Measures].[B]");
        Assert.Equal(ChangeKind.Changed, b.Change);
        var c = Assert.Single(report.Changes, c => c.Name == "[Measures].[C]");
        Assert.Equal(ChangeKind.Added, c.Change);
    }

    [Fact]
    public void Analyze_ComputesDownstreamImpact()
    {
        string script(string caExpr) => $"""
            CALCULATE;
            {Member("[Measures].[CA]", caExpr)}
            {Member("[Measures].[Marge]", "[Measures].[CA] - 1")}
            {Member("[Measures].[Total]", "[Measures].[Marge] * 2")}
            """;

        var report = ImpactAnalyzer.Analyze(script("100"), script("200"));

        var ca = Assert.Single(report.Changes, c => c.Name == "[Measures].[CA]");
        Assert.Equal(ChangeKind.Changed, ca.Change);
        Assert.Contains("[Measures].[Marge]", ca.ImpactedDownstream);
        Assert.Contains("[Measures].[Total]", ca.ImpactedDownstream);
    }

    [Fact]
    public void Analyze_RemovedMemberFlagsDependents()
    {
        string oldScript = $"""
            CALCULATE;
            {Member("[Measures].[CA]", "100")}
            {Member("[Measures].[Marge]", "[Measures].[CA] - 1")}
            """;
        // CA disappears, Marge remains (reference now broken).
        string newScript = $"""
            CALCULATE;
            {Member("[Measures].[Marge]", "[Measures].[CA] - 1")}
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        var ca = Assert.Single(report.Changes, c => c.Name == "[Measures].[CA]");
        Assert.Equal(ChangeKind.Removed, ca.Change);
        Assert.Contains("[Measures].[Marge]", ca.ImpactedDownstream);
    }

    [Fact]
    public void Analyze_NoChange_EmptyReport()
    {
        string script = $"""
            CALCULATE;
            {Member("[Measures].[A]", "1")}
            {Member("[Measures].[B]", "[Measures].[A] + 1")}
            """;

        var report = ImpactAnalyzer.Analyze(script, script);

        Assert.Empty(report.Changes);
    }

    [Fact]
    public void Analyze_HandlesCycleWithoutInfiniteLoop()
    {
        string oldScript = $"""
            CALCULATE;
            {Member("[Measures].[A]", "[Measures].[B] + 1")}
            {Member("[Measures].[B]", "[Measures].[A] + 1")}
            """;
        string newScript = $"""
            CALCULATE;
            {Member("[Measures].[A]", "[Measures].[B] + 2")}
            {Member("[Measures].[B]", "[Measures].[A] + 1")}
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        var a = Assert.Single(report.Changes, c => c.Name == "[Measures].[A]");
        Assert.Equal(ChangeKind.Changed, a.Change);
        Assert.Contains("[Measures].[B]", a.ImpactedDownstream);
    }

    [Fact]
    public void Analyze_PropertyChange_IsReported()
    {
        string oldScript = """
            CREATE MEMBER CURRENTCUBE.[Measures].[A] AS 1,
            FORMAT_STRING = "#,##0", NON_EMPTY_BEHAVIOR = [Measures].[X];
            """;
        string newScript = """
            CREATE MEMBER CURRENTCUBE.[Measures].[A] AS 1,
            FORMAT_STRING = "0.00%", NON_EMPTY_BEHAVIOR = [Measures].[X];
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        var a = Assert.Single(report.Changes);
        Assert.Equal(ChangeKind.Changed, a.Change);
        Assert.Equal(ChangeDetail.Properties, a.Detail);
    }

    [Fact]
    public void Analyze_ExpressionChange_HasExpressionDetail()
    {
        var report = ImpactAnalyzer.Analyze(Member("[Measures].[A]", "1"), Member("[Measures].[A]", "2"));

        Assert.Equal(ChangeDetail.Expression, Assert.Single(report.Changes).Detail);
    }

    [Fact]
    public void Analyze_ExpressionAndPropertyChange_HasBothDetail()
    {
        var report = ImpactAnalyzer.Analyze(
            "CREATE MEMBER CURRENTCUBE.[Measures].[A] AS 1, VISIBLE = 1;",
            "CREATE MEMBER CURRENTCUBE.[Measures].[A] AS 2, VISIBLE = 0;");

        Assert.Equal(ChangeDetail.ExpressionAndProperties, Assert.Single(report.Changes).Detail);
    }

    [Fact]
    public void Analyze_WhitespaceAndCommentOnlyChange_IsNotReported()
    {
        string oldScript = """
            CREATE MEMBER CURRENTCUBE.[Measures].[A] AS [Measures].[X] + 1,
            VISIBLE = 1;
            SCOPE([Measures].[A]);
                THIS = 2;
            END SCOPE;
            """;
        string newScript = """
            -- reformatted
            CREATE MEMBER CURRENTCUBE.[Measures].[A]
                AS [Measures].[X]+1, /* inline note */
                VISIBLE = 1;
            SCOPE ( [Measures].[A] ) ;
              THIS=2;
            END SCOPE;
            """;

        Assert.Empty(ImpactAnalyzer.Analyze(oldScript, newScript).Changes);
    }

    [Fact]
    public void Analyze_ScopeBodyChange_IsReported()
    {
        string script(string value) => $"""
            CALCULATE;
            {Member("[Measures].[A]", "1")}
            SCOPE([Measures].[A]);
                THIS = {value};
            END SCOPE;
            """;

        var report = ImpactAnalyzer.Analyze(script("2"), script("3"));

        var scope = Assert.Single(report.Changes);
        Assert.Equal("Scope", scope.Kind);
        Assert.Equal(ChangeKind.Changed, scope.Change);
        Assert.Equal("SCOPE([Measures].[A]);", scope.Name);
        Assert.Equal(3, scope.StartLine);
    }

    [Fact]
    public void Analyze_ScopeAddedAndRemoved_AreReported()
    {
        string oldScript = """
            CALCULATE;
            SCOPE([Measures].[A]);
                THIS = 2;
            END SCOPE;
            """;
        string newScript = """
            CALCULATE;
            SCOPE([Measures].[B]);
                THIS = 2;
            END SCOPE;
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        Assert.Equal(2, report.Changes.Count);
        Assert.Contains(report.Changes, c => c.Kind == "Scope" && c.Change == ChangeKind.Removed && c.Name.Contains("[A]"));
        Assert.Contains(report.Changes, c => c.Kind == "Scope" && c.Change == ChangeKind.Added && c.Name.Contains("[B]"));
    }

    [Fact]
    public void Analyze_StandaloneAssignmentChange_IsReported()
    {
        string oldScript = """
            CALCULATE;
            ([Measures].[A], [Dates].[Année].&[2026]) = 10;
            """;
        string newScript = """
            CALCULATE;
            ([Measures].[A], [Dates].[Année].&[2026]) = 20;
            """;

        var report = ImpactAnalyzer.Analyze(oldScript, newScript);

        Assert.NotEmpty(report.Changes);
        Assert.All(report.Changes, c => Assert.Equal("Autre", c.Kind));
    }

    [Fact]
    public void Analyze_IdenticalScopes_DuplicatedInBothScripts_AreNotReported()
    {
        string script = """
            SCOPE([Measures].[A]); THIS = 1; END SCOPE;
            SCOPE([Measures].[A]); THIS = 1; END SCOPE;
            """;

        Assert.Empty(ImpactAnalyzer.Analyze(script, script).Changes);
    }
}
