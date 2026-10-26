using System.Data;
using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Xunit.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// Processing state read through AMO against the DEV server — read-only, nothing is processed.
/// Locks in the AMO behaviours documented in CLAUDE.md (observed on SSAS 17.0.25.218): if a server
/// or AMO version changes them, these tests say so.
/// </summary>
[Trait("Category", "Integration")]
public class ProcessingStateIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly SsasSession _session = new();
    private readonly StateStore _store = new(Path.Combine(Path.GetTempPath(), $"cs-proc-{Guid.NewGuid():N}.db"));

    private async Task<ProcessingState> ReadDevAsync()
    {
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var sw = Stopwatch.StartNew();
        var state = await new ProcessingStateService(_session, _store).ReadAsync();
        output.WriteLine($"Read in {sw.ElapsedMilliseconds} ms: {state.Cubes.Count} cubes, "
            + $"{state.Cubes.Sum(c => c.MeasureGroups.Sum(g => g.Partitions.Count))} partitions, "
            + $"{state.Dimensions.Count} dimensions");
        return state;
    }

    [Fact]
    public async Task Read_ContainsConfiguredCube_WithPartitions_DatesInUtc()
    {
        var state = await ReadDevAsync();

        var cube = Assert.Single(state.Cubes, c => c.Name == TestTarget.Cube);
        Assert.NotEmpty(cube.MeasureGroups);
        var partitions = cube.MeasureGroups.SelectMany(g => g.Partitions).ToList();
        Assert.NotEmpty(partitions);
        Assert.All(partitions.Where(p => p.LastProcessedUtc is not null),
            p => Assert.Equal(DateTimeKind.Utc, p.LastProcessedUtc!.Value.Kind));
        Assert.NotEmpty(state.Dimensions);
        Assert.Equal(24, state.ThresholdHours);
    }

    [Fact]
    public async Task MeasureGroupSize_IsSumOfPartitionSizes()
    {
        // Observed on 17.0.25.218: the measure group EstimatedSize is the sum of its partitions,
        // which is why it is shown as a server-reported size (unlike EstimatedRows).
        var state = await ReadDevAsync();
        foreach (var mg in state.Cubes.SelectMany(c => c.MeasureGroups).Where(g => g.Partitions.Count > 0))
            Assert.Equal(mg.Partitions.Sum(p => p.EstimatedSize), mg.EstimatedSize);
    }

    [Fact]
    public async Task UnprocessedCube_VisibleThroughAmo_AbsentFromMdschemaCubes()
    {
        // The case this panel exists for: an unprocessed cube is gone from MDSCHEMA_CUBES.
        var state = await ReadDevAsync();
        var unprocessed = state.Cubes.Where(c => c.State == ObjectState.Unprocessed).Select(c => c.Name).ToList();
        output.WriteLine($"Unprocessed cubes: {string.Join(", ", unprocessed)}");
        if (unprocessed.Count == 0) return; // nothing to compare on this server today

        var cubes = await _session.ExecuteDmvAsync(
            "SELECT [CUBE_NAME] FROM $SYSTEM.MDSCHEMA_CUBES WHERE [CUBE_SOURCE] = 1");
        var listed = cubes.Rows.Cast<DataRow>().Select(r => (string)r["CUBE_NAME"]).ToHashSet();
        Assert.All(unprocessed, name => Assert.DoesNotContain(name, listed));
        Assert.True(state.Summary.UnprocessedCubes >= unprocessed.Count);
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
    }
}
