namespace CubeScope.Core.Models;

/// <summary>Metadata tree of a cube (without members — lazy-loaded for autocompletion).</summary>
public sealed record CubeMeta(
    string CubeName,
    IReadOnlyList<MeasureFolder> MeasureFolders,
    IReadOnlyList<DimensionMeta> Dimensions);

/// <summary>Group of measures per display folder ("" = root).</summary>
public sealed record MeasureFolder(string Folder, IReadOnlyList<MeasureMeta> Measures);

public sealed record MeasureMeta(string Name, string UniqueName, string Description = "");

public sealed record DimensionMeta(string Name, string UniqueName, IReadOnlyList<HierarchyMeta> Hierarchies, string Description = "");

public sealed record HierarchyMeta(string Name, string UniqueName, IReadOnlyList<LevelMeta> Levels, string Description = "");

public sealed record LevelMeta(string Name, string UniqueName, int Number);

public sealed record MemberMeta(string Caption, string UniqueName);

/// <summary>
/// One level of the member tree (explorer). <paramref name="ChildrenCount"/> is the
/// REAL number of children, independent of the load cap: 0 identifies a leaf (no
/// expand arrow), and beyond the cap it gives the number of members not displayed.
/// -1 if the server did not return it — the caller then treats the node as expandable.
/// </summary>
public sealed record MemberNode(string Caption, string UniqueName, long ChildrenCount);

/// <summary>
/// One drill-down step. <paramref name="HasMore"/> says the cap truncated the list: the UI
/// says so instead of truncating silently. The exact number of hidden members is derived
/// client-side from the parent's cardinality — so the server does not have to recount it.
/// </summary>
public sealed record MemberChildren(IReadOnlyList<MemberNode> Nodes, bool HasMore);

/// <summary>Delta of a perfmon counter around a query.</summary>
public sealed record CounterDelta(string Category, string Counter, long Delta);
