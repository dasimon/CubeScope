using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using Xunit.Abstractions;

namespace CubeScope.Core.Tests;

/// <summary>
/// Pasted keys resolved against the real DEV cube (queries only): real keys taken from a large
/// level, mixed with invented ones that must come back as missing — never dropped silently.
/// </summary>
[Trait("Category", "Integration")]
public class KeyResolutionIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly SsasSession _session = new();
    private readonly StateStore _store = new(Path.Combine(Path.GetTempPath(), $"cs-keys-{Guid.NewGuid():N}.db"));

    /// <summary>The key part of a canonical member name: what follows "{hierarchy}.", unescaped
    /// when it is a single key, kept as is when composite (<c>&amp;[a]&amp;[b]</c>).</summary>
    private static string KeyOf(string hierarchy, string member)
    {
        string suffix = member[(hierarchy.Length + 1)..];
        bool composite = suffix.IndexOf("]&[", StringComparison.Ordinal) >= 0;
        return composite ? suffix : suffix[2..^1].Replace("]]", "]");
    }

    [Fact]
    public async Task PastedKeys_FoundAndMissing_OnALargeLevel()
    {
        await _session.ConnectAsync(TestTarget.ServerDev);
        await _session.SetCatalogAsync(TestTarget.CatalogDev);
        var meta = new MetadataService(_session, _store);
        var cube = await meta.GetCubeMetaAsync(TestTarget.Cube);
        string measure = cube.MeasureFolders.SelectMany(f => f.Measures).First().UniqueName;

        // A large attribute level (more than 500 members), where a dimension scan would hurt.
        (HierarchyMeta Hierarchy, MemberChildren Level)? large = null;
        foreach (var h in cube.Dimensions.SelectMany(d => d.Hierarchies).Where(h => h.Levels.Count == 2).Take(80))
        {
            var top = await meta.GetChildrenAsync(TestTarget.Cube, h.UniqueName, isHierarchy: true, limit: 1);
            if (top.Nodes.Count != 1) continue;
            var level = await meta.GetChildrenAsync(TestTarget.Cube, top.Nodes[0].UniqueName, isHierarchy: false, limit: 500);
            if (level.HasMore) { large = (h, level); break; }
        }
        Assert.True(large.HasValue, "No level with more than 500 members on the test cube.");
        var (hier, members) = large!.Value;
        string levelName = hier.Levels.Single(l => !l.IsAll).UniqueName;

        var real = members.Nodes.Take(120).Select(n => KeyOf(hier.UniqueName, n.UniqueName)).ToList();
        string[] invented = ["__CUBESCOPE_NOPE_1", "__CUBESCOPE_NOPE_2", "__CUBESCOPE_NOPE_3"];
        var pasted = string.Join("\n", real.Take(60)) + "\n" + string.Join(";", invented) + "\t"
            + string.Join(",", real.Skip(60)) + "\n" + real[0];

        var keys = KeyList.Parse(pasted);
        Assert.Equal(123, keys.Count); // duplicate of real[0] dropped

        var sw = Stopwatch.StartNew();
        var r = await meta.ResolveKeysAsync(TestTarget.Cube, levelName, keys);
        output.WriteLine($"{levelName}: {r.Found.Count} found, {r.Missing.Count} missing in {sw.ElapsedMilliseconds} ms");

        Assert.Equal(invented, r.Missing);
        Assert.Empty(r.Failed);
        Assert.Equal(keys, r.Keys); // pasted order kept, for a saved list
        Assert.Equal(real, r.Found.Select(f => f.Key));
        // Canonical names, identical to those of the member tree — including members whose
        // caption is EMPTY (they exist: observed on the dev cube, they must not come back missing).
        Assert.Equal(members.Nodes.Take(120).Select(n => n.UniqueName), r.Found.Select(f => f.UniqueName));
        output.WriteLine($"Empty captions among found: {r.Found.Count(f => f.Caption.Length == 0)}");

        // The found members, level-qualified, are valid MDX in a sub-select and on an axis.
        var mdx = MdxQueryBuilder.Build(new BuilderModel(TestTarget.Cube, [measure], [],
            [new BuilderAxisItem(hier.UniqueName, null, r.Found.Take(5).Select(f => f.UniqueName).ToList(), null)],
            [new BuilderFilter(hier.UniqueName, r.Found.Select(f => f.UniqueName).ToList())],
            NonEmptyColumns: false, NonEmptyRows: false));
        var q = await new QueryService(_session).ExecuteAsync(mdx);
        Assert.Equal(5, q.Rows.Count);
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
    }
}
