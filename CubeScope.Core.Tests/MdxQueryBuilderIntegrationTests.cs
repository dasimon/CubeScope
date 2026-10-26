using CubeScope.Core.Models;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Xunit.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// The builder's MDX run against the real DEV cube (queries only, read-only): the unit tests pin
/// the text, these prove the server accepts it. Hierarchies and members are discovered, not assumed.
/// </summary>
[Trait("Category", "Integration")]
public class MdxQueryBuilderIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly SsasSession _session = new();
    private readonly StateStore _store = new(Path.Combine(Path.GetTempPath(), $"cs-bld-{Guid.NewGuid():N}.db"));

    private sealed record Small(string Hierarchy, string Level, IReadOnlyList<string> Members);

    /// <summary>Two small attribute hierarchies (2 to 50 members on their first level below All).</summary>
    private async Task<(MetadataService Meta, string Measure, Small A, Small B)> DiscoverAsync()
    {
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var meta = new MetadataService(_session, _store);
        var cube = await meta.GetCubeMetaAsync(TestTarget.Cube);
        string measure = cube.MeasureFolders.SelectMany(f => f.Measures).First().UniqueName;

        var found = new List<Small>();
        foreach (var h in cube.Dimensions.SelectMany(d => d.Hierarchies).Where(h => h.Levels.Count == 2).Take(40))
        {
            var top = await meta.GetChildrenAsync(TestTarget.Cube, h.UniqueName, isHierarchy: true, limit: 1);
            if (top.Nodes.Count != 1) continue; // no single All member
            var level = await meta.GetChildrenAsync(TestTarget.Cube, top.Nodes[0].UniqueName, isHierarchy: false, limit: 50);
            if (level.HasMore || level.Nodes.Count < 2) continue;
            found.Add(new Small(h.UniqueName, h.Levels.Single(l => l.Number == 1).UniqueName,
                level.Nodes.Select(n => n.UniqueName).ToList()));
            if (found.Count == 2) break;
        }
        Assert.True(found.Count == 2, "Two small hierarchies are needed on the test cube.");
        output.WriteLine($"Measure {measure}; A = {found[0].Hierarchy}; B = {found[1].Hierarchy}");
        return (meta, measure, found[0], found[1]);
    }

    private async Task<QueryResult> RunAsync(BuilderModel model)
    {
        string mdx = MdxQueryBuilder.Build(model);
        output.WriteLine(mdx + "\n");
        return await new QueryService(_session).ExecuteAsync(mdx);
    }

    [Fact]
    public async Task GeneratedQueries_RunOnTheDevCube()
    {
        var (_, measure, a, b) = await DiscoverAsync();
        string cube = TestTarget.Cube;

        // Zero axis.
        await RunAsync(new BuilderModel(cube, [], [], [], []));

        // Top N on rows, crossjoin with chosen members, single-member WHERE.
        var r = await RunAsync(new BuilderModel(cube, [measure], [],
            [new BuilderAxisItem(a.Hierarchy, a.Level, null, new BuilderRank(measure, 2))],
            [new BuilderFilter(b.Hierarchy, [b.Members[0]])],
            NonEmptyColumns: false, NonEmptyRows: false));
        Assert.Equal(2, r.AxesCount);
        Assert.Equal(2, r.Rows.Count);

        // Order + filter on the axis hierarchy (sub-select) + multi-member sub-select on another one.
        r = await RunAsync(new BuilderModel(cube, [measure], [new BuilderAxisItem(b.Hierarchy, b.Level, null, null)],
            [new BuilderAxisItem(a.Hierarchy, a.Level, null, new BuilderRank(measure, null))],
            [new BuilderFilter(a.Hierarchy, [a.Members[0], a.Members[1]]),
             new BuilderFilter(b.Hierarchy, [b.Members[0], b.Members[1]])],
            NonEmptyColumns: false, NonEmptyRows: false));
        Assert.Equal(2, r.AxesCount);

        // Rows only: the default measure fills COLUMNS.
        r = await RunAsync(new BuilderModel(cube, [], [],
            [new BuilderAxisItem(a.Hierarchy, null, [a.Members[0]], null)], [], NonEmptyRows: false));
        Assert.Single(r.Rows);
    }

    [Fact]
    public async Task AllLevel_FlaggedFromLevelType()
    {
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var cube = await new MetadataService(_session, _store).GetCubeMetaAsync(TestTarget.Cube);
        var levels = cube.Dimensions.SelectMany(d => d.Hierarchies).SelectMany(h => h.Levels).ToList();
        // Observed on 17.0.25.218: (All) = level 0 of every aggregatable hierarchy, never below.
        Assert.Contains(levels, l => l.IsAll);
        Assert.All(levels.Where(l => l.IsAll), l => Assert.Equal(0, l.Number));
    }

    [Fact]
    public async Task Where_TwoHierarchiesOfTheSameDimension_RunsOnTheDevCube()
    {
        // Auto-exists between attributes of one dimension: the server must accept the tuple.
        var (meta, measure, _, _) = await DiscoverAsync();
        var cube = await meta.GetCubeMetaAsync(TestTarget.Cube);
        foreach (var dim in cube.Dimensions.Where(d => d.Hierarchies.Count >= 2))
        {
            var members = new List<(string Hierarchy, string Member)>();
            foreach (var h in dim.Hierarchies.Where(h => h.Levels.Count == 2))
            {
                var top = await meta.GetChildrenAsync(TestTarget.Cube, h.UniqueName, isHierarchy: true, limit: 1);
                if (top.Nodes.Count != 1) continue;
                var first = await meta.GetChildrenAsync(TestTarget.Cube, top.Nodes[0].UniqueName, isHierarchy: false, limit: 1);
                if (first.Nodes.Count == 1) members.Add((h.UniqueName, first.Nodes[0].UniqueName));
                if (members.Count == 2) break;
            }
            if (members.Count < 2) continue;
            await RunAsync(new BuilderModel(TestTarget.Cube, [measure], [], [],
                members.Select(m => new BuilderFilter(m.Hierarchy, [m.Member])).ToList()));
            return;
        }
        Assert.Fail("No dimension with two small attribute hierarchies on the test cube.");
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
    }
}
