using CubeScope.Core.Perfmon;

namespace CubeScope.Core.Tests;

public class PerfmonServiceUnitTests
{
    [Theory]
    // Localisé FR (OS serveur français) — séparateur " : " AVEC espaces
    [InlineData("MSAS16 : MDX", true)]
    [InlineData("MSAS16 : cache", true)]
    [InlineData("MSAS16 : requête du moteur de stockage", true)]
    // Anglais (autre serveur possible)
    [InlineData("MSAS16:MDX", true)]
    [InlineData("MSAS16:Storage Engine Query", true)]
    // Catégories non voulues
    [InlineData("MSAS16 : mémoire", false)]
    [InlineData("MSAS16 : threads", false)]
    [InlineData("MSAS16:Reliability Metrics", false)]
    [InlineData("SansSeparateur", false)]
    public void IsWantedCategory_MatchesLocalizedLabels(string category, bool expected)
        => Assert.Equal(expected, PerfmonService.IsWantedCategory(category));

    [Theory]
    [InlineData("MSAS16 : MDX", "MDX")]
    [InlineData("MSOLAP$INSTANCE01 : cache", "cache")]
    [InlineData("SansSeparateur", "SansSeparateur")]
    public void CategoryLabel_StripsPrefix(string category, string expected)
        => Assert.Equal(expected, PerfmonService.CategoryLabel(category));

    [Fact]
    public void Snapshot_WhenNotInitialized_IsEmptyAndSafe()
    {
        using var svc = new PerfmonService();
        Assert.Equal(PerfmonStatus.NotInitialized, svc.Status);
        Assert.Empty(svc.Snapshot());
        Assert.Empty(svc.DeltasSince([]));
    }

    [Fact]
    public void Initialize_UnknownHost_DegradesToUnavailable()
    {
        using var svc = new PerfmonService();
        svc.Initialize("SERVEUR-INEXISTANT");
        Assert.Equal(PerfmonStatus.Unavailable, svc.Status);
        Assert.NotNull(svc.StatusDetail);
        Assert.Empty(svc.Snapshot()); // toujours sans exception
    }
}

[Trait("Category", "Integration")]
public class PerfmonServiceIntegrationTests
{
    [Fact]
    public void Initialize_OnRealServer_FindsCounters()
    {
        using var svc = new PerfmonService();
        svc.Initialize($"{TestTarget.Server}:9999"); // le port doit être ignoré pour perfmon

        Assert.Equal(PerfmonStatus.Ready, svc.Status);
        var snap = svc.Snapshot();
        Assert.True(snap.Count > 20, $"attendu : dizaines de compteurs, obtenu {snap.Count}");
        Assert.Contains(snap.Keys, k => k.Contains("MDX", StringComparison.OrdinalIgnoreCase));
    }
}
