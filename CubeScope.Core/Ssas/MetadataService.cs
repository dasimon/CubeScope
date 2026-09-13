using System.Collections.Concurrent;
using System.Data;
using System.Text;
using CubeScope.Core.Models;
using CubeScope.Core.State;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Cube metadata via $SYSTEM.MDSCHEMA_* DMVs (settled decision: DMVs as the main
/// path). In-memory cache per (server, catalog, cube) — metadata only
/// changes on deployment, with a Refresh button in the UI to force it.
/// Pitfall: ALWAYS bracket DMV columns (HIERARCHY is an MDX reserved word).
/// </summary>
public sealed class MetadataService(SsasSession session, StateStore store)
{
    private readonly ConcurrentDictionary<string, CubeMeta> _cache = new();

    public async Task<IReadOnlyList<string>> GetCubesAsync(CancellationToken ct = default)
    {
        var t = await session.ExecuteDmvAsync(
            "SELECT [CUBE_NAME] FROM $SYSTEM.MDSCHEMA_CUBES WHERE [CUBE_SOURCE] = 1", ct);
        return t.Rows.Cast<DataRow>().Select(r => (string)r["CUBE_NAME"]).ToList();
    }

    public async Task<CubeMeta> GetCubeMetaAsync(string cube, bool refresh = false, CancellationToken ct = default)
    {
        string key = $"{session.Server}|{session.Catalog}|{cube}";
        if (!refresh && _cache.TryGetValue(key, out var cached)) return cached;

        string quoted = cube.Replace("'", "''");

        var measures = await session.ExecuteDmvAsync($"""
            SELECT [MEASURE_NAME], [MEASURE_UNIQUE_NAME], [MEASURE_DISPLAY_FOLDER], [DESCRIPTION]
            FROM $SYSTEM.MDSCHEMA_MEASURES
            WHERE [CUBE_NAME] = '{quoted}' AND [MEASURE_IS_VISIBLE]
            """, ct);
        var dimensions = await session.ExecuteDmvAsync($"""
            SELECT [DIMENSION_NAME], [DIMENSION_UNIQUE_NAME], [DESCRIPTION]
            FROM $SYSTEM.MDSCHEMA_DIMENSIONS
            WHERE [CUBE_NAME] = '{quoted}' AND [DIMENSION_IS_VISIBLE] AND [DIMENSION_UNIQUE_NAME] <> '[Measures]'
            """, ct);
        var hierarchies = await session.ExecuteDmvAsync($"""
            SELECT [DIMENSION_UNIQUE_NAME], [HIERARCHY_NAME], [HIERARCHY_UNIQUE_NAME], [DESCRIPTION]
            FROM $SYSTEM.MDSCHEMA_HIERARCHIES
            WHERE [CUBE_NAME] = '{quoted}' AND [HIERARCHY_IS_VISIBLE]
            """, ct);
        var levels = await session.ExecuteDmvAsync($"""
            SELECT [HIERARCHY_UNIQUE_NAME], [LEVEL_NAME], [LEVEL_UNIQUE_NAME], [LEVEL_NUMBER]
            FROM $SYSTEM.MDSCHEMA_LEVELS
            WHERE [CUBE_NAME] = '{quoted}' AND [LEVEL_IS_VISIBLE]
            """, ct);

        var meta = Build(cube, measures, dimensions, hierarchies, levels);
        _cache[key] = meta;
        return meta;
    }

    /// <summary>
    /// Members of a hierarchy for autocompletion — lazy + in-memory cache (settled decision).
    /// Capped: large hierarchies (securities…) must not flood either the UI or the server.
    /// </summary>
    public async Task<IReadOnlyList<MemberMeta>> GetMembersAsync(string cube, string hierarchyUniqueName,
        int limit = 1000, CancellationToken ct = default)
    {
        string key = $"m|{session.Server}|{session.Catalog}|{cube}|{hierarchyUniqueName}";
        if (_memberCache.TryGetValue(key, out var cached)) return cached;

        var t = await session.ExecuteDmvAsync($"""
            SELECT [MEMBER_CAPTION], [MEMBER_UNIQUE_NAME]
            FROM $SYSTEM.MDSCHEMA_MEMBERS
            WHERE [CUBE_NAME] = '{cube.Replace("'", "''")}'
              AND [HIERARCHY_UNIQUE_NAME] = '{hierarchyUniqueName.Replace("'", "''")}'
            """, ct);
        var members = t.Rows.Cast<DataRow>()
            .Take(limit)
            .Select(r => new MemberMeta((string)r["MEMBER_CAPTION"], (string)r["MEMBER_UNIQUE_NAME"]))
            .ToList();
        _memberCache[key] = members;
        return members;
    }

    private readonly ConcurrentDictionary<string, IReadOnlyList<MemberMeta>> _memberCache = new();

    /// <summary>
    /// Children of an explorer node, one level at a time (see <see cref="MemberChildrenQuery"/>
    /// for why MDX rather than the DMV). <paramref name="parent"/> is a hierarchy
    /// at the first level, a member after that.
    ///
    /// We request <paramref name="limit"/> + 1 members to know whether the cap truncated without
    /// having to count separately: the extra row is never returned, it only serves to
    /// answer "there are more".
    /// </summary>
    public async Task<MemberChildren> GetChildrenAsync(
        string cube, string parent, bool isHierarchy, int limit = 500, CancellationToken ct = default)
    {
        string key = $"c|{session.Server}|{session.Catalog}|{cube}|{isHierarchy}|{parent}|{limit}";
        if (_childrenCache.TryGetValue(key, out var cached)) return cached;

        string mdx = MemberChildrenQuery.Build(cube, parent, isHierarchy, limit + 1);
        var result = await session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { } });
            var cs = cmd.ExecuteCellSet();

            // A single-axis query has no Axes[1] — and a member with no children returns an
            // axis with zero positions rather than an error.
            var nodes = new List<MemberNode>();
            if (cs.Axes.Count > 0)
            {
                foreach (Position pos in cs.Axes[0].Positions)
                {
                    var m = pos.Members[0];
                    // ChildCount comes from the CHILDREN_CARDINALITY requested in the query. If it
                    // is not returned, -1: the node stays expandable and we will find out when
                    // expanding it, rather than wrongly declaring it a leaf and making it mute.
                    long enfants;
                    try { enfants = m.ChildCount; } catch { enfants = -1; }
                    nodes.Add(new MemberNode(m.Caption, m.UniqueName, enfants));
                }
            }

            bool hasMore = nodes.Count > limit;
            if (hasMore) nodes.RemoveAt(nodes.Count - 1);
            return new MemberChildren(nodes, hasMore);
        }, ct);

        _childrenCache[key] = result;
        return result;
    }

    private readonly ConcurrentDictionary<string, MemberChildren> _childrenCache = new();

    // Cubes whose stamp has already been validated this session (a single DMV round-trip per
    // (server, catalog, cube): on first access we compare the stamp with the SQLite cache and,
    // if it differs, we invalidate the persistent cache).
    private readonly ConcurrentDictionary<string, byte> _validatedCubes = new();

    /// <summary>Version fingerprint of the cube (LAST_SCHEMA_UPDATE|LAST_DATA_UPDATE): changes
    /// when the cube is reprocessed → used to invalidate the caption cache. "" if no row.</summary>
    private async Task<string> GetCubeStampAsync(string cube, CancellationToken ct)
    {
        var t = await session.ExecuteDmvAsync($"""
            SELECT [LAST_SCHEMA_UPDATE], [LAST_DATA_UPDATE]
            FROM $SYSTEM.MDSCHEMA_CUBES
            WHERE [CUBE_NAME] = '{cube.Replace("'", "''")}' AND [CUBE_SOURCE] = 1
            """, ct);
        var row = t.Rows.Cast<DataRow>().FirstOrDefault();
        if (row is null) return "";
        string schema = row["LAST_SCHEMA_UPDATE"] is DBNull ? "" : Convert.ToString(row["LAST_SCHEMA_UPDATE"]) ?? "";
        string data = row["LAST_DATA_UPDATE"] is DBNull ? "" : Convert.ToString(row["LAST_DATA_UPDATE"]) ?? "";
        return $"{schema}|{data}";
    }

    /// <summary>
    /// Captions of several members by unique name: persistent SQLite cache first
    /// (invalidated once per session if the cube was reprocessed), the missing ones resolved by
    /// targeted MDSCHEMA_MEMBERS lookup then persisted. Null value for a member not found.
    /// </summary>
    /// <summary>
    /// Resolves member captions through MDX (`member.Properties("MEMBER_CAPTION")`): each
    /// member is resolved directly by its key, with no dimension scan. One query for the whole
    /// batch. Throws if a member is invalid (the caller then falls back to member-by-member).
    /// </summary>
    private Task<IReadOnlyDictionary<string, string>> ResolveCaptionsViaMdxAsync(
        string cube, IReadOnlyList<string> members, CancellationToken ct)
        => session.WithConnectionAsync<IReadOnlyDictionary<string, string>>(conn =>
        {
            var sb = new StringBuilder("WITH ");
            for (int i = 0; i < members.Count; i++)
                sb.Append($"MEMBER [Measures].[__cap{i}] AS StrToMember('{members[i].Replace("'", "''")}').Properties(\"MEMBER_CAPTION\") ");
            sb.Append("SELECT { ")
              .Append(string.Join(", ", Enumerable.Range(0, members.Count).Select(i => $"[Measures].[__cap{i}]")))
              .Append(" } ON 0 FROM [").Append(cube.Replace("]", "]]")).Append(']');

            using var cmd = new AdomdCommand(sb.ToString(), conn);
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { } });
            var cs = cmd.ExecuteCellSet();
            var result = new Dictionary<string, string>();
            for (int i = 0; i < members.Count; i++)
            {
                var val = cs.Cells[i].Value;
                if (val is not null and not DBNull && val.ToString() is { Length: > 0 } s)
                    result[members[i]] = s;
            }
            return result;
        }, ct);

    public async Task<IReadOnlyDictionary<string, string?>> GetMemberCaptionsAsync(
        string cube, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        string server = session.Server ?? "", catalog = session.Catalog ?? "";
        string key = $"{server}|{catalog}|{cube}";

        // Stamp validation only once per (server, catalog, cube) this session.
        if (!_validatedCubes.ContainsKey(key))
        {
            var stamp = await GetCubeStampAsync(cube, ct);
            if (store.GetCaptionStamp(server, catalog, cube) != stamp)
            {
                store.InvalidateCubeCaptions(server, catalog, cube);
                store.SetCaptionStamp(server, catalog, cube, stamp);
            }
            _validatedCubes.TryAdd(key, 0);
        }

        var cached = store.GetCachedCaptions(server, catalog, cube, names);
        var misses = names.Where(n => !cached.ContainsKey(n)).ToList();

        var found = new Dictionary<string, string>();
        // Resolution through MDX `.Properties("MEMBER_CAPTION")`: resolves each member DIRECTLY
        // by its key, without scanning the dimension — unlike MDSCHEMA_MEMBERS, which scans
        // the whole hierarchy (thousands of securities) → freeze. And the DMV does not support `IN`.
        // A single MDX query resolves a whole batch; fallback to member-by-member if a member
        // of the batch is invalid (stale reference) and makes the whole query fail.
        const int mdxChunk = 50;
        for (int off = 0; off < misses.Count; off += mdxChunk)
        {
            var slice = misses.Skip(off).Take(mdxChunk).ToList();
            try
            {
                foreach (var kv in await ResolveCaptionsViaMdxAsync(cube, slice, ct)) found[kv.Key] = kv.Value;
            }
            catch
            {
                foreach (var name in slice)
                    try { foreach (var kv in await ResolveCaptionsViaMdxAsync(cube, new[] { name }, ct)) found[kv.Key] = kv.Value; }
                    catch { /* invalid member (stale reference): ignored */ }
            }
        }
        if (found.Count > 0) store.PutCachedCaptions(server, catalog, cube, found);

        var result = new Dictionary<string, string?>(names.Count);
        foreach (var name in names)
            result[name] = cached.TryGetValue(name, out var c) ? c : found.GetValueOrDefault(name);
        return result;
    }

    /// <summary>
    /// Caption of ONE member by its unique name. Delegates to the batched lookup (persistent SQLite cache).
    /// Works for any member regardless of the size of the dimension. Null if not found.
    /// </summary>
    public async Task<string?> GetMemberCaptionAsync(string cube, string memberUniqueName, CancellationToken ct = default)
    {
        var d = await GetMemberCaptionsAsync(cube, new[] { memberUniqueName }, ct);
        return d.TryGetValue(memberUniqueName, out var c) ? c : null;
    }

    /// <summary>Clears the cube's persistent caption cache (manual refresh).</summary>
    public void InvalidateCube(string cube)
    {
        string server = session.Server ?? "", catalog = session.Catalog ?? "";
        store.InvalidateCubeCaptions(server, catalog, cube);
        _validatedCubes.TryRemove($"{server}|{catalog}|{cube}", out _);
    }

    /// <summary>Pure construction of the DTO from the rowsets (testable without a server).</summary>
    internal static CubeMeta Build(string cube, DataTable measures, DataTable dimensions,
        DataTable hierarchies, DataTable levels)
    {
        var measureFolders = measures.Rows.Cast<DataRow>()
            .GroupBy(r => r["MEASURE_DISPLAY_FOLDER"] as string ?? "")
            .OrderBy(g => g.Key)
            .Select(g => new MeasureFolder(g.Key,
                g.Select(r => new MeasureMeta((string)r["MEASURE_NAME"], (string)r["MEASURE_UNIQUE_NAME"],
                        r["DESCRIPTION"] as string ?? ""))
                 .OrderBy(m => m.Name).ToList()))
            .ToList();

        var levelsByHier = levels.Rows.Cast<DataRow>()
            .GroupBy(r => (string)r["HIERARCHY_UNIQUE_NAME"])
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LevelMeta>)g
                    .Select(r => new LevelMeta((string)r["LEVEL_NAME"], (string)r["LEVEL_UNIQUE_NAME"],
                        Convert.ToInt32(r["LEVEL_NUMBER"])))
                    .OrderBy(l => l.Number).ToList());

        var hiersByDim = hierarchies.Rows.Cast<DataRow>()
            .GroupBy(r => (string)r["DIMENSION_UNIQUE_NAME"])
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<HierarchyMeta>)g
                    .Select(r =>
                    {
                        string un = (string)r["HIERARCHY_UNIQUE_NAME"];
                        return new HierarchyMeta((string)r["HIERARCHY_NAME"], un,
                            levelsByHier.GetValueOrDefault(un, []), r["DESCRIPTION"] as string ?? "");
                    })
                    .OrderBy(h => h.Name).ToList());

        var dims = dimensions.Rows.Cast<DataRow>()
            .Select(r =>
            {
                string un = (string)r["DIMENSION_UNIQUE_NAME"];
                return new DimensionMeta((string)r["DIMENSION_NAME"], un, hiersByDim.GetValueOrDefault(un, []),
                    r["DESCRIPTION"] as string ?? "");
            })
            .OrderBy(d => d.Name)
            .ToList();

        return new CubeMeta(cube, measureFolders, dims);
    }
}
