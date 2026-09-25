using CubeScope.Core.Perfmon;

namespace CubeScope.Core.Tests;

public class PerfmonServiceUnitTests
{
    [Theory]
    // FR-localized (French server OS) — " : " separator WITH spaces
    [InlineData("MSAS16 : MDX", true)]
    [InlineData("MSAS16 : cache", true)]
    [InlineData("MSAS16 : requête du moteur de stockage", true)]
    // English (another possible server)
    [InlineData("MSAS16:MDX", true)]
    [InlineData("MSAS16:Storage Engine Query", true)]
    // Unwanted categories
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
        Assert.Empty(svc.Snapshot()); // still no exception
    }

    [Fact]
    public async Task Snapshot_DoesNotWaitForAnOngoingDiscovery()
    {
        using var release = new ManualResetEventSlim();
        using var svc = new PerfmonService(_ => { release.Wait(); return []; });

        var init = Task.Run(() => svc.Initialize("SLOW"));
        await Task.Delay(100); // let Initialize enter the discovery

        // The first query after connecting used to wait for the whole remote discovery.
        var snap = Task.Run(svc.Snapshot);
        Assert.Same(snap, await Task.WhenAny(snap, Task.Delay(2000)));
        Assert.Empty(await snap);

        release.Set();
        await init;
    }

    [Fact]
    public async Task Initialize_StaleDiscoveryFinishingLast_IsDropped()
    {
        // Two connections in quick succession: A's slow discovery ends after B's.
        using var releaseA = new ManualResetEventSlim();
        using var svc = new PerfmonService(machine =>
        {
            if (machine == "A") releaseA.Wait();
            return [];
        });

        var initA = Task.Run(() => svc.Initialize("A"));
        await Task.Delay(100);
        svc.Initialize("B:2383");
        releaseA.Set();
        await initA;

        Assert.EndsWith("sur B", svc.StatusDetail); // B's outcome, not overwritten by stale A
    }

    [Fact]
    public void Initialize_AfterDispose_DoesNothing()
    {
        int calls = 0;
        var svc = new PerfmonService(_ => { calls++; return []; });
        svc.Dispose();

        svc.Initialize("X");

        Assert.Equal(0, calls);
        Assert.Equal(PerfmonStatus.NotInitialized, svc.Status);
    }
}

[Trait("Category", "Integration")]
public class PerfmonServiceIntegrationTests
{
    [Fact]
    public void Initialize_OnRealServer_FindsCounters()
    {
        using var svc = new PerfmonService();
        svc.Initialize($"{TestTarget.Server}:9999"); // the port must be ignored for perfmon

        Assert.Equal(PerfmonStatus.Ready, svc.Status);
        var snap = svc.Snapshot();
        Assert.True(snap.Count > 20, $"expected: dozens of counters, got {snap.Count}");
        Assert.Contains(snap.Keys, k => k.Contains("MDX", StringComparison.OrdinalIgnoreCase));
    }
}
