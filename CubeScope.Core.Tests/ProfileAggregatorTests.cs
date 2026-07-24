using CubeScope.Core.Models;
using CubeScope.Core.Profiler;

namespace CubeScope.Core.Tests;

public class ProfileAggregatorTests
{
    private static int _clock;
    // Horodatage croissant déterministe (l'appariement Subcube/Verbose se fait par ordre de capture)
    private static ProfileEvent Ev(string cls, long ms, string? text = null, int sub = 0) =>
        new(cls, sub, ms, text, DateTime.UnixEpoch.AddSeconds(_clock++));

    [Fact]
    public void Aggregate_SplitsStorageAndFormulaEngine_AndUsesVerboseText()
    {
        _clock = 0;
        var events = new[]
        {
            Ev("QueryEnd", 500),
            Ev("QuerySubcube", 120, "0000,0"),          // bitmap brut
            Ev("QuerySubcubeVerbose", 0, "détail A"),   // même grille, lisible (apparié au 1er)
            Ev("QuerySubcube", 80, "0010,0"),
            Ev("QuerySubcubeVerbose", 0, "détail B"),
            Ev("GetDataFromCache", 0),
            Ev("GetDataFromAggregation", 0),
        };

        var p = ProfileAggregator.Aggregate(events, fallbackTotalMs: 999);

        Assert.Equal(500, p.TotalMs);
        Assert.Equal(200, p.StorageEngineMs); // 120 + 80 (QuerySubcube seuls, pas le verbose)
        Assert.Equal(300, p.FormulaEngineMs); // 500 - 200
        Assert.Equal(2, p.SubcubeCount);
        Assert.Equal(1, p.CacheHits);
        Assert.Equal(1, p.AggregationHits);
        // Trié par durée décroissante → le 120ms (détail A) d'abord, texte Verbose affiché
        Assert.Equal("détail A", p.Subcubes[0].Text);
        Assert.Equal(120, p.Subcubes[0].DurationMs);
    }

    [Fact]
    public void Aggregate_FallsBackToRawWhenNoVerbose()
    {
        _clock = 0;
        var p = ProfileAggregator.Aggregate(
            [Ev("QueryEnd", 100), Ev("QuerySubcube", 40, "0000,0")], fallbackTotalMs: 100);
        Assert.Equal("0000,0", p.Subcubes[0].Text); // pas de verbose → bitmap brut
    }

    [Fact]
    public void Aggregate_NoQueryEnd_UsesFallbackTotal()
    {
        var p = ProfileAggregator.Aggregate([Ev("QuerySubcube", 40)], fallbackTotalMs: 250);
        Assert.Equal(250, p.TotalMs);
        Assert.Equal(40, p.StorageEngineMs);
        Assert.Equal(210, p.FormulaEngineMs);
    }

    [Fact]
    public void Aggregate_WarmQuery_AllFormulaEngineWhenSubcubesZeroDuration()
    {
        // Cas constaté au spike : requête chaude, sous-cubes servis par cache (durée 0)
        var events = new[]
        {
            Ev("QueryEnd", 234),
            Ev("QuerySubcube", 0),
            Ev("QuerySubcube", 0),
            Ev("GetDataFromCache", 0),
            Ev("GetDataFromCache", 0),
        };

        var p = ProfileAggregator.Aggregate(events, 234);

        Assert.Equal(0, p.StorageEngineMs);
        Assert.Equal(234, p.FormulaEngineMs);
        Assert.Equal(2, p.CacheHits);
    }

    [Fact]
    public void Aggregate_Empty_IsZeroButUsesFallback()
    {
        var p = ProfileAggregator.Aggregate([], 42);
        Assert.Equal(42, p.TotalMs);
        Assert.Equal(0, p.StorageEngineMs);
        Assert.Empty(p.Subcubes);
    }
}

[Trait("Category", "Integration")]
public class ProfilerServiceIntegrationTests
{
    [Fact]
    public void Initialize_OnRealServer_CreatesTraceAndStatusReady()
    {
        using var svc = new ProfilerService();
        svc.Initialize(TestTarget.Server);

        // Avec des droits admin SSAS → trace créée
        Assert.Equal(ProfilerStatus.Ready, svc.Status);
        Assert.NotNull(svc.StatusDetail);
        // DrainSince sur une session inconnue est sûr et vide
        Assert.Empty(svc.DrainSince("session-bidon", DateTime.UtcNow.AddMinutes(-1)));
    }
}
