using CubeScope.Core.Models;
using CubeScope.Core.Profiler;

namespace CubeScope.Core.Tests;

public class ProfileAggregatorTests
{
    private static int _clock;
    // Deterministic increasing timestamp (Subcube/Verbose pairing is done by capture order)
    private static ProfileEvent Ev(string cls, long ms, string? text = null, int sub = 0) =>
        new(cls, sub, ms, text, DateTime.UnixEpoch.AddSeconds(_clock++));

    [Fact]
    public void Aggregate_SplitsStorageAndFormulaEngine_AndUsesVerboseText()
    {
        _clock = 0;
        var events = new[]
        {
            Ev("QueryEnd", 500),
            Ev("QuerySubcube", 120, "0000,0"),          // raw bitmap
            Ev("QuerySubcubeVerbose", 0, "détail A"),   // same grid, readable (paired with the 1st)
            Ev("QuerySubcube", 80, "0010,0"),
            Ev("QuerySubcubeVerbose", 0, "détail B"),
            Ev("GetDataFromCache", 0),
            Ev("GetDataFromAggregation", 0),
        };

        var p = ProfileAggregator.Aggregate(events, fallbackTotalMs: 999);

        Assert.Equal(500, p.TotalMs);
        Assert.Equal(200, p.StorageEngineMs); // 120 + 80 (QuerySubcube only, not the verbose)
        Assert.Equal(300, p.FormulaEngineMs); // 500 - 200
        Assert.Equal(2, p.SubcubeCount);
        Assert.Equal(1, p.CacheHits);
        Assert.Equal(1, p.AggregationHits);
        // Sorted by descending duration → the 120ms one (détail A) first, Verbose text shown
        Assert.Equal("détail A", p.Subcubes[0].Text);
        Assert.Equal(120, p.Subcubes[0].DurationMs);
    }

    [Fact]
    public void Aggregate_FallsBackToRawWhenNoVerbose()
    {
        _clock = 0;
        var p = ProfileAggregator.Aggregate(
            [Ev("QueryEnd", 100), Ev("QuerySubcube", 40, "0000,0")], fallbackTotalMs: 100);
        Assert.Equal("0000,0", p.Subcubes[0].Text); // no verbose → raw bitmap
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
        // Case observed in the spike: warm query, subcubes served from cache (duration 0)
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

    [Fact]
    public void QueryWindow_StopsAtItsQueryEnd_IgnoringFollowingQueriesOnTheSession()
    {
        // A hover caption query runs right after on the same session: before the window,
        // its QueryEnd and subcubes were added to the profile of the query being measured.
        _clock = 0;
        var events = new[]
        {
            Ev("QuerySubcube", 30),
            Ev("QueryEnd", 100, "SELECT 1 ON 0 FROM [C]"),
            Ev("QuerySubcube", 70),
            Ev("QueryEnd", 900, "WITH MEMBER [Measures].[__cap0] AS 1 SELECT ..."),
        };

        var window = ProfileAggregator.QueryWindow(events, "SELECT 1 ON 0 FROM [C]");
        var p = ProfileAggregator.Aggregate(window, 0);

        Assert.Equal(2, window.Count);
        Assert.Equal(100, p.TotalMs);
        Assert.Equal(30, p.StorageEngineMs);
        Assert.Equal(1, p.SubcubeCount);
    }

    [Fact]
    public void QueryWindow_DropsTheTailOfThePreviousQuery()
    {
        // The previous query's last events arrive after this one started (asynchronous push).
        _clock = 0;
        var events = new[]
        {
            Ev("QuerySubcube", 500),
            Ev("QueryEnd", 800, "previous"),
            Ev("QuerySubcube", 20),
            Ev("QueryEnd", 60, "SELECT\r\n  1 ON 0\r\nFROM [C]"),
        };

        var window = ProfileAggregator.QueryWindow(events, "SELECT 1 ON 0\nFROM [C]");

        Assert.Equal([20L, 60L], window.Select(e => e.DurationMs)); // whitespace-insensitive match
    }

    [Fact]
    public void QueryWindow_NoMatchingText_FallsBackToTheFirstQueryEnd()
    {
        _clock = 0;
        var events = new[] { Ev("QuerySubcube", 10), Ev("QueryEnd", 40), Ev("QuerySubcube", 99), Ev("QueryEnd", 99) };

        var window = ProfileAggregator.QueryWindow(events, "SELECT 1 ON 0 FROM [C]");

        Assert.Equal([10L, 40L], window.Select(e => e.DurationMs));
    }

    [Fact]
    public void QueryWindow_NoQueryEndYet_KeepsEverything()
    {
        _clock = 0;
        var events = new[] { Ev("QuerySubcube", 10), Ev("GetDataFromCache", 0) };

        Assert.Equal(2, ProfileAggregator.QueryWindow(events, "x").Count);
    }
}

public class ProfilerServiceBufferTests
{
    private static ProfileEvent At(DateTime utc) => new("QuerySubcube", 0, 1, null, utc);

    [Fact]
    public void IdleSessions_ArePurged_ActiveOnesKept()
    {
        // The trace is server-wide: on a shared server, every session of every user and job gets
        // a buffer. Idle ones must go, or the dictionary grows all day long.
        using var svc = new ProfilerService();
        var t0 = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

        svc.Record("job-1", At(t0));
        svc.Record("job-2", At(t0));
        Assert.Equal(2, svc.BufferedSessionCount);

        svc.Record("mine", At(t0.AddMinutes(3)));

        Assert.Equal(1, svc.BufferedSessionCount);
    }

    [Fact]
    public void RecentSessions_AreNotPurged()
    {
        using var svc = new ProfilerService();
        var t0 = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

        svc.Record("a", At(t0));
        svc.Record("b", At(t0.AddSeconds(30)));

        Assert.Equal(2, svc.BufferedSessionCount);
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

        // With SSAS admin rights → trace created
        Assert.Equal(ProfilerStatus.Ready, svc.Status);
        Assert.NotNull(svc.StatusDetail);
        // DrainSince on an unknown session is safe and empty
        Assert.Empty(svc.DrainSince("session-bidon", DateTime.UtcNow.AddMinutes(-1)));
    }
}
