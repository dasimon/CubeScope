using CubeScope.Core.Models;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Xunit.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// Processing against the DEV server only (<see cref="TestTarget.ServerDev"/>, guarded by
/// <see cref="TestTarget.AssertDevServerDistinct"/>). The run processes ONE small partition — the
/// smallest single-partition measure group of the test cube — approved by the owner on 2026-10-26.
/// Also records what a partition-only process does to its parents (open question of lot 1).
/// </summary>
[Trait("Category", "Integration")]
public class ProcessIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly SsasSession _session = new();
    private readonly StateStore _store = new(Path.Combine(Path.GetTempPath(), $"cs-proc-run-{Guid.NewGuid():N}.db"));

    private async Task<ProcessingState> ConnectDevAsync()
    {
        TestTarget.AssertDevServerDistinct();
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        return await new ProcessingStateService(_session, _store).ReadAsync();
    }

    /// <summary>The smallest processed single-partition measure group: cheap to process again.</summary>
    private static (CubeNode Cube, MeasureGroupNode Group, PartitionNode Partition) SmallPartition(ProcessingState s)
    {
        var cube = s.Cubes.Single(c => c.Name == TestTarget.Cube);
        var mg = cube.MeasureGroups
            .Where(g => g.Partitions.Count == 1 && g.Partitions[0].State == ObjectState.Processed && g.EstimatedSize > 0)
            .OrderBy(g => g.EstimatedSize).First();
        return (cube, mg, mg.Partitions[0]);
    }

    [Fact]
    public async Task Preview_CapturesXmlaWithoutProcessing()
    {
        var before = await ConnectDevAsync();
        var (cube, mg, p) = SmallPartition(before);
        var svc = new ProcessService(_session, _store);

        var preview = await svc.PreviewAsync(new ProcessRequest(
            new ProcessTarget(ProcessObjectKind.Partition, cube.Id, mg.Id, p.Id), ProcessMode.Full));
        output.WriteLine(preview.Xmla);

        Assert.Contains("<Type>ProcessFull</Type>", preview.Xmla);
        Assert.Contains($"<PartitionID>{p.Id}</PartitionID>", preview.Xmla);
        Assert.Null(preview.Refusal);

        // Capture only: nothing was processed.
        var after = await new ProcessingStateService(_session, _store).ReadAsync();
        Assert.Equal(p.LastProcessedUtc, SmallPartition(after).Partition.LastProcessedUtc);
    }

    [Fact]
    public async Task Preview_DimensionFull_ListsTheMeasureGroupsLeftUnprocessed()
    {
        var state = await ConnectDevAsync();
        var dim = state.Dimensions.First();
        var request = new ProcessRequest(new ProcessTarget(ProcessObjectKind.Dimension, DimensionId: dim.Id), ProcessMode.Full);
        var svc = new ProcessService(_session, _store);

        // Not declared dev = production: refused outright, nothing captured.
        var refused = await svc.PreviewAsync(request);
        Assert.True(refused.IsProduction);
        Assert.NotNull(refused.Refusal);
        Assert.Equal("", refused.Xmla);

        _store.SetDevServer(TestTarget.ServerDev, true);
        var preview = await svc.PreviewAsync(request);
        Assert.False(preview.IsProduction);
        Assert.Null(preview.Refusal);
        Assert.Contains("<Type>ProcessFull</Type>", preview.Xmla);

        // Some database dimensions are used by no measure group (10 of 51 on the dev base,
        // observed): look for one that is, and check its measure groups are listed.
        foreach (var d in state.Dimensions)
        {
            var p = await svc.PreviewAsync(request with { Target = new ProcessTarget(ProcessObjectKind.Dimension, DimensionId: d.Id) });
            if (p.AffectedMeasureGroups.Count == 0) continue;
            output.WriteLine($"{d.Name}: {string.Join(", ", p.AffectedMeasureGroups)}");
            Assert.All(p.AffectedMeasureGroups, g => Assert.Contains(" › ", g));
            return;
        }
        Assert.Fail("No dimension used by a measure group on the test database.");
    }

    [Fact]
    public async Task Run_ProcessFullOfOneSmallPartition_OnDev()
    {
        var before = await ConnectDevAsync();
        var (cube, mg, p) = SmallPartition(before);
        output.WriteLine($"Before: {cube.Name} › {mg.Name} › {p.Name}: partition {p.LastProcessedUtc:O}, "
            + $"group {mg.LastProcessedUtc:O}, cube {cube.LastProcessedUtc:O}, size {p.EstimatedSize}");

        var result = await new ProcessService(_session, _store).RunAsync(TestTarget.ServerDev, TestTarget.CatalogDev,
            new ProcessRequest(new ProcessTarget(ProcessObjectKind.Partition, cube.Id, mg.Id, p.Id), ProcessMode.Full),
            [TestTarget.ServerDev]);
        foreach (var m in result.Messages) output.WriteLine($"{m.Severity}: {m.Text}");
        Assert.True(result.Success);

        var after = await new ProcessingStateService(_session, _store).ReadAsync();
        var cube2 = after.Cubes.Single(c => c.Id == cube.Id);
        var mg2 = cube2.MeasureGroups.Single(g => g.Id == mg.Id);
        var p2 = mg2.Partitions.Single();
        output.WriteLine($"After ({result.DurationMs} ms): partition {p2.LastProcessedUtc:O}, "
            + $"group {mg2.LastProcessedUtc:O}, cube {cube2.LastProcessedUtc:O}, size {p2.EstimatedSize}");

        Assert.Equal(ObjectState.Processed, p2.State);
        Assert.True(p2.LastProcessedUtc > p.LastProcessedUtc);
        // Observed on 17.0.25.218 (2026-10-26): a partition-only process does NOT move the
        // LastProcessed of its measure group nor of its cube — hence freshness from partitions.
        Assert.Equal(mg.LastProcessedUtc, mg2.LastProcessedUtc);
        Assert.Equal(cube.LastProcessedUtc, cube2.LastProcessedUtc);
        var log = Assert.Single(_store.GetProcessLog());
        Assert.True(log.Success);
        Assert.False(log.IsProduction);
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
    }
}
