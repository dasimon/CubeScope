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

    /// <summary>
    /// Cache key of a cube on the session's current server and catalog. Read it INSIDE the
    /// piece of work run under the session lock: Connect/SetCatalog take the same lock, so
    /// the key then matches the connection the data was read from. Computed before and
    /// outside, a concurrent catalog change would file one catalog's data under the other's key.
    /// </summary>
    private string CubeKey(string cube) => $"{session.Server}|{session.Catalog}|{cube}";

    public async Task<CubeMeta> GetCubeMetaAsync(string cube, bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cache.TryGetValue(CubeKey(cube), out var cached)) return cached;

        string quoted = cube.Replace("'", "''");

        // The four rowsets and the key under a single lock acquisition (see CubeKey).
        var (key, meta) = await session.WithConnectionAsync(conn =>
        {
            var measures = SsasSession.ExecuteDmv(conn, $"""
                SELECT [MEASURE_NAME], [MEASURE_UNIQUE_NAME], [MEASURE_DISPLAY_FOLDER], [DESCRIPTION]
                FROM $SYSTEM.MDSCHEMA_MEASURES
                WHERE [CUBE_NAME] = '{quoted}' AND [MEASURE_IS_VISIBLE]
                """, ct);
            var dimensions = SsasSession.ExecuteDmv(conn, $"""
                SELECT [DIMENSION_NAME], [DIMENSION_UNIQUE_NAME], [DESCRIPTION]
                FROM $SYSTEM.MDSCHEMA_DIMENSIONS
                WHERE [CUBE_NAME] = '{quoted}' AND [DIMENSION_IS_VISIBLE] AND [DIMENSION_UNIQUE_NAME] <> '[Measures]'
                """, ct);
            var hierarchies = SsasSession.ExecuteDmv(conn, $"""
                SELECT [DIMENSION_UNIQUE_NAME], [HIERARCHY_NAME], [HIERARCHY_UNIQUE_NAME], [DESCRIPTION]
                FROM $SYSTEM.MDSCHEMA_HIERARCHIES
                WHERE [CUBE_NAME] = '{quoted}' AND [HIERARCHY_IS_VISIBLE]
                """, ct);
            var levels = SsasSession.ExecuteDmv(conn, $"""
                SELECT [HIERARCHY_UNIQUE_NAME], [LEVEL_NAME], [LEVEL_UNIQUE_NAME], [LEVEL_NUMBER], [LEVEL_TYPE]
                FROM $SYSTEM.MDSCHEMA_LEVELS
                WHERE [CUBE_NAME] = '{quoted}' AND [LEVEL_IS_VISIBLE]
                """, ct);
            return (CubeKey(cube), Build(cube, measures, dimensions, hierarchies, levels));
        }, ct);

        // An explicit refresh (redeployed cube) also drops the member lists derived from it.
        if (refresh) EvictMembers(key);
        _cache[key] = meta;
        return meta;
    }

    /// <summary>
    /// Members of a hierarchy for autocompletion — lazy + in-memory cache (settled decision).
    /// Capped server-side (see <see cref="MemberChildrenQuery.BuildMembers"/>): large hierarchies
    /// (securities…) must flood neither the UI nor the server.
    /// </summary>
    public async Task<IReadOnlyList<MemberMeta>> GetMembersAsync(string cube, string hierarchyUniqueName,
        int limit = 1000, CancellationToken ct = default)
    {
        if (_memberCache.TryGetValue($"m|{CubeKey(cube)}|{hierarchyUniqueName}", out var cached)) return cached;

        string mdx = MemberChildrenQuery.BuildMembers(cube, hierarchyUniqueName, limit);
        var (key, members) = await session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            var cs = SsasSession.Run(cmd, cmd.ExecuteCellSet, ct);
            var list = new List<MemberMeta>();
            // Members are on axis 1 (axis 0 is empty); test Axes.Count rather than assume it.
            if (cs.Axes.Count > 1)
                foreach (Position pos in cs.Axes[1].Positions)
                {
                    var m = pos.Members[0];
                    list.Add(new MemberMeta(m.Caption, m.UniqueName));
                }
            return ($"m|{CubeKey(cube)}|{hierarchyUniqueName}", (IReadOnlyList<MemberMeta>)list);
        }, ct);
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
        string ChildrenKey() => $"c|{CubeKey(cube)}|{isHierarchy}|{parent}|{limit}";
        if (_childrenCache.TryGetValue(ChildrenKey(), out var cached)) return cached;

        string mdx = MemberChildrenQuery.Build(cube, parent, isHierarchy, limit + 1);
        var (key, result) = await session.WithConnectionAsync(conn =>
        {
            using var cmd = new AdomdCommand(mdx, conn);
            var cs = SsasSession.Run(cmd, cmd.ExecuteCellSet, ct);

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
            return (ChildrenKey(), new MemberChildren(nodes, hasMore));
        }, ct);

        _childrenCache[key] = result;
        return result;
    }

    private readonly ConcurrentDictionary<string, MemberChildren> _childrenCache = new();

    /// <summary>Drops the member lists and explorer levels of a cube (key from <see cref="CubeKey"/>).</summary>
    private void EvictMembers(string cubeKey)
    {
        foreach (var k in _memberCache.Keys.Where(k => k.StartsWith($"m|{cubeKey}|", StringComparison.Ordinal)))
            _memberCache.TryRemove(k, out _);
        foreach (var k in _childrenCache.Keys.Where(k => k.StartsWith($"c|{cubeKey}|", StringComparison.Ordinal)))
            _childrenCache.TryRemove(k, out _);
    }

    /// <summary>
    /// Thrown under the session lock when the server or catalog is no longer the one a batch of
    /// caption lookups started on: its results would be persisted under the wrong key.
    /// </summary>
    private sealed class ContextChangedException()
        : InvalidOperationException("La connexion a changé de serveur ou de catalogue pendant la requête.");

    /// <summary>Call under the session lock (inside a WithConnectionAsync piece of work).</summary>
    private void EnsureContext(string server, string catalog)
    {
        if ((session.Server ?? "") != server || (session.Catalog ?? "") != catalog)
            throw new ContextChangedException();
    }

    // Cubes whose stamp has already been validated this session (a single DMV round-trip per
    // (server, catalog, cube): on first access we compare the stamp with the SQLite cache and,
    // if it differs, we invalidate the persistent cache).
    private readonly ConcurrentDictionary<string, byte> _validatedCubes = new();

    /// <summary>Version fingerprint of the cube (LAST_SCHEMA_UPDATE|LAST_DATA_UPDATE): changes
    /// when the cube is reprocessed → used to invalidate the caption cache. "" if no row.</summary>
    private async Task<string> GetCubeStampAsync(string cube, string server, string catalog, CancellationToken ct)
    {
        var t = await session.WithConnectionAsync(conn =>
        {
            EnsureContext(server, catalog);
            return SsasSession.ExecuteDmv(conn, $"""
                SELECT [LAST_SCHEMA_UPDATE], [LAST_DATA_UPDATE]
                FROM $SYSTEM.MDSCHEMA_CUBES
                WHERE [CUBE_NAME] = '{cube.Replace("'", "''")}' AND [CUBE_SOURCE] = 1
                """, ct);
        }, ct);
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
    /// A cell in error (ADOMD throws on Cell.Value) only skips that member.
    /// </summary>
    private Task<IReadOnlyDictionary<string, string>> ResolveCaptionsViaMdxAsync(
        string cube, string server, string catalog, IReadOnlyList<string> members, CancellationToken ct)
        => session.WithConnectionAsync<IReadOnlyDictionary<string, string>>(conn =>
        {
            EnsureContext(server, catalog);
            var sb = new StringBuilder("WITH ");
            for (int i = 0; i < members.Count; i++)
                sb.Append($"MEMBER [Measures].[__cap{i}] AS StrToMember('{members[i].Replace("'", "''")}').Properties(\"MEMBER_CAPTION\") ");
            sb.Append("SELECT { ")
              .Append(string.Join(", ", Enumerable.Range(0, members.Count).Select(i => $"[Measures].[__cap{i}]")))
              .Append(" } ON 0 FROM [").Append(cube.Replace("]", "]]")).Append(']');

            using var cmd = new AdomdCommand(sb.ToString(), conn);
            var cs = SsasSession.Run(cmd, cmd.ExecuteCellSet, ct);
            var result = new Dictionary<string, string>();
            for (int i = 0; i < members.Count; i++)
            {
                object? val;
                try { val = cs.Cells[i].Value; }
                catch (AdomdException) { continue; } // cell in error: this member only stays unresolved
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
        // Every server round-trip below checks, under the session lock, that the server and
        // catalog are still these ones (EnsureContext): what gets persisted under this key was
        // really read from it, even if the user switches catalog meanwhile.
        if (!_validatedCubes.ContainsKey(key))
        {
            var stamp = await GetCubeStampAsync(cube, server, catalog, ct);
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
        // Only a failure of the query itself triggers the fallback: never a cancellation, nor a
        // change of catalog, which must reach the caller.
        bool Recoverable(Exception ex)
            => ex is not (OperationCanceledException or ContextChangedException) && !ct.IsCancellationRequested;

        const int mdxChunk = 50;
        for (int off = 0; off < misses.Count; off += mdxChunk)
        {
            var slice = misses.Skip(off).Take(mdxChunk).ToList();
            try
            {
                foreach (var kv in await ResolveCaptionsViaMdxAsync(cube, server, catalog, slice, ct)) found[kv.Key] = kv.Value;
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                foreach (var name in slice)
                    try { foreach (var kv in await ResolveCaptionsViaMdxAsync(cube, server, catalog, [name], ct)) found[kv.Key] = kv.Value; }
                    catch (Exception ex2) when (Recoverable(ex2)) { /* invalid member (stale reference): ignored */ }
            }
        }
        if (found.Count > 0) store.PutCachedCaptions(server, catalog, cube, found);

        var result = new Dictionary<string, string?>(names.Count);
        foreach (var name in names)
            result[name] = cached.TryGetValue(name, out var c) ? c : found.GetValueOrDefault(name);
        return result;
    }

    /// <summary>
    /// Pasted keys → members of <paramref name="level"/>. Each key becomes
    /// <c>{level}.&amp;[{key}]</c>, resolved by MDX by key (50 per query, member-by-member
    /// fallback) — never MDSCHEMA_MEMBERS, which scans the whole dimension.
    ///
    /// Existence is read from <c>.UniqueName</c>, NOT from the caption: real members with an
    /// EMPTY caption exist (observed on the dev cube: companies without a name), and an unknown key
    /// does not fail, it yields null (observed). The canonical unique name comes back too, so a
    /// pasted member and the same member picked in the tree are one.
    /// </summary>
    public async Task<KeyResolution> ResolveKeysAsync(
        string cube, string level, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cube)) throw new MdxBuilderException("Aucun cube sélectionné.");
        var distinct = keys.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > KeyList.MaxKeys)
            throw new MdxBuilderException($"Liste trop longue : {distinct.Count} clés (maximum {KeyList.MaxKeys}).");

        var names = distinct.Select(k => (Key: k, Name: KeyList.MemberUniqueName(level, k))).ToList();
        string server = session.Server ?? "", catalog = session.Catalog ?? "";
        bool Recoverable(Exception ex)
            => ex is not (OperationCanceledException or ContextChangedException) && !ct.IsCancellationRequested;

        var resolved = new Dictionary<string, MemberLookup>(StringComparer.Ordinal);
        const int mdxChunk = 50;
        for (int off = 0; off < names.Count; off += mdxChunk)
        {
            var slice = names.Skip(off).Take(mdxChunk).Select(n => n.Name).ToList();
            try
            {
                foreach (var kv in await ResolveMembersViaMdxAsync(cube, server, catalog, slice, ct)) resolved[kv.Key] = kv.Value;
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                // On the dev server an unknown key gives a null cell, not a failed query: a fallback
                // here means another cause (or another missing-member mode) — worth a trace.
                Console.WriteLine($"[CubeScope] Key batch failed, member-by-member fallback: {ex.GetBaseException().Message}");
                Exception? last = null;
                int failures = 0;
                foreach (var name in slice)
                {
                    try { foreach (var kv in await ResolveMembersViaMdxAsync(cube, server, catalog, [name], ct)) resolved[kv.Key] = kv.Value; }
                    catch (Exception ex2) when (Recoverable(ex2))
                    {
                        resolved[name] = new MemberLookup(null, "", Failed: true);
                        last = ex2;
                        failures++;
                    }
                }
                // Every key failing on its own is a server problem, not a list of unknown keys.
                if (failures == slice.Count && last is not null) throw last;
            }
        }

        var found = new List<ResolvedKey>();
        var missing = new List<string>();
        var failed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in names)
        {
            var m = resolved.GetValueOrDefault(name);
            if (m is { Failed: true }) failed.Add(key);
            else if (m?.UniqueName is not { } unique) missing.Add(key);
            // Two pasted forms of one member (0042 and 42, case on a case-insensitive collation): once.
            else if (seen.Add(unique)) found.Add(new ResolvedKey(key, unique, m.Caption));
        }
        return new KeyResolution(distinct, found, missing, failed);
    }

    /// <summary>One looked-up member: canonical name (null = no such member), caption, or a lookup in error.</summary>
    private sealed record MemberLookup(string? UniqueName, string Caption, bool Failed = false);

    /// <summary>
    /// Canonical unique name and caption of members, one MDX query per batch. Null UniqueName =
    /// no such member (an unknown key yields a null cell, observed); a cell in error = lookup
    /// failed for that key. The caption is read apart: one in error or empty does not lose the
    /// member (empty captions exist, observed) — the UI shows the key instead.
    /// </summary>
    private Task<IReadOnlyDictionary<string, MemberLookup>> ResolveMembersViaMdxAsync(
        string cube, string server, string catalog, IReadOnlyList<string> members, CancellationToken ct)
        => session.WithConnectionAsync<IReadOnlyDictionary<string, MemberLookup>>(conn =>
        {
            EnsureContext(server, catalog);
            var sb = new StringBuilder("WITH ");
            for (int i = 0; i < members.Count; i++)
            {
                string m = members[i].Replace("'", "''");
                sb.Append($"MEMBER [Measures].[__u{i}] AS StrToMember('{m}').UniqueName ")
                  .Append($"MEMBER [Measures].[__c{i}] AS StrToMember('{m}').Properties(\"MEMBER_CAPTION\") ");
            }
            sb.Append("SELECT { ")
              .Append(string.Join(", ", Enumerable.Range(0, members.Count)
                  .Select(i => $"[Measures].[__u{i}], [Measures].[__c{i}]")))
              .Append(" } ON 0 FROM [").Append(cube.Replace("]", "]]")).Append(']');

            using var cmd = new AdomdCommand(sb.ToString(), conn);
            var cs = SsasSession.Run(cmd, cmd.ExecuteCellSet, ct);
            var result = new Dictionary<string, MemberLookup>(StringComparer.Ordinal);
            for (int i = 0; i < members.Count; i++)
            {
                object? unique;
                try { unique = cs.Cells[2 * i].Value; }
                catch (AdomdException)
                {
                    result[members[i]] = new MemberLookup(null, "", Failed: true);
                    continue;
                }
                if (unique is null or DBNull || unique.ToString() is not { Length: > 0 } u)
                {
                    result[members[i]] = new MemberLookup(null, "");
                    continue;
                }
                string caption;
                try { caption = cs.Cells[2 * i + 1].Value is { } c and not DBNull ? c.ToString() ?? "" : ""; }
                catch (AdomdException) { caption = ""; }
                result[members[i]] = new MemberLookup(u, caption);
            }
            return result;
        }, ct);

    /// <summary>
    /// Caption of ONE member by its unique name. Delegates to the batched lookup (persistent SQLite cache).
    /// Works for any member regardless of the size of the dimension. Null if not found.
    /// </summary>
    public async Task<string?> GetMemberCaptionAsync(string cube, string memberUniqueName, CancellationToken ct = default)
    {
        var d = await GetMemberCaptionsAsync(cube, new[] { memberUniqueName }, ct);
        return d.TryGetValue(memberUniqueName, out var c) ? c : null;
    }

    /// <summary>Clears the cube's persistent caption cache and its in-memory member lists (manual refresh).</summary>
    public void InvalidateCube(string cube)
    {
        string server = session.Server ?? "", catalog = session.Catalog ?? "";
        store.InvalidateCubeCaptions(server, catalog, cube);
        _validatedCubes.TryRemove($"{server}|{catalog}|{cube}", out _);
        EvictMembers(CubeKey(cube));
    }

    /// <summary>LEVEL_TYPE = 1 is the (All) level (checked on SSAS 17.0.25.218: level 0 of every
    /// aggregatable hierarchy, never below). Missing column (older rowsets, tests) = not All.</summary>
    private static bool IsAllLevel(DataRow r)
        => r.Table.Columns.Contains("LEVEL_TYPE") && r["LEVEL_TYPE"] is not DBNull
           && Convert.ToInt32(r["LEVEL_TYPE"]) == 1;

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
                        Convert.ToInt32(r["LEVEL_NUMBER"]), IsAllLevel(r)))
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
