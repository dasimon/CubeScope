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
        // CA disparaît, Marge reste (référence désormais cassée).
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
}
