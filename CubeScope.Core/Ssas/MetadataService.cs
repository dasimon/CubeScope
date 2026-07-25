using System.Collections.Concurrent;
using System.Data;
using System.Text;
using CubeScope.Core.Models;
using CubeScope.Core.State;
using Microsoft.AnalysisServices.AdomdClient;

namespace CubeScope.Core.Ssas;

/// <summary>
/// Métadonnées d'un cube via DMV $SYSTEM.MDSCHEMA_* (décision actée : DMV en voie
/// principale). Cache mémoire par (serveur, catalogue, cube) — les métadonnées ne
/// bougent qu'au déploiement, bouton Rafraîchir côté UI pour forcer.
/// Piège : TOUJOURS crocheter les colonnes DMV (HIERARCHY est un mot réservé MDX).
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
            SELECT [DIMENSION_NAME], [DIMENSION_UNIQUE_NAME]
            FROM $SYSTEM.MDSCHEMA_DIMENSIONS
            WHERE [CUBE_NAME] = '{quoted}' AND [DIMENSION_IS_VISIBLE] AND [DIMENSION_UNIQUE_NAME] <> '[Measures]'
            """, ct);
        var hierarchies = await session.ExecuteDmvAsync($"""
            SELECT [DIMENSION_UNIQUE_NAME], [HIERARCHY_NAME], [HIERARCHY_UNIQUE_NAME]
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
    /// Membres d'une hiérarchie pour l'autocomplétion — lazy + cache mémoire (décision actée).
    /// Plafonné : les grosses hiérarchies (titres…) ne doivent pas noyer ni l'UI ni le serveur.
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

    // Cubes dont le stamp a déjà été validé cette session (un seul aller-retour DMV par
    // (serveur, catalogue, cube) : au premier accès on compare le stamp au cache SQLite et,
    // s'il diffère, on invalide le cache persistant).
    private readonly ConcurrentDictionary<string, byte> _validatedCubes = new();

    /// <summary>Empreinte de version du cube (LAST_SCHEMA_UPDATE|LAST_DATA_UPDATE) : change
    /// quand le cube est reprocessé → sert à invalider le cache de captions. "" si pas de ligne.</summary>
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
    /// Captions de plusieurs membres par unique name : cache persistant SQLite d'abord
    /// (invalidé une fois par session si le cube a été reprocessé), les manquants résolus par
    /// lookup ciblé MDSCHEMA_MEMBERS puis persistés. Valeur null pour un membre introuvable.
    /// </summary>
    /// <summary>
    /// Résout les captions de membres par MDX (`membre.Properties("MEMBER_CAPTION")`) : chaque
    /// membre est résolu directement par sa clé, sans scan de dimension. Une requête pour tout
    /// le paquet. Lève si un membre est invalide (l'appelant fait alors un repli membre/membre).
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

        // Validation du stamp une seule fois par (serveur, catalogue, cube) cette session.
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
        // Résolution par MDX `.Properties("MEMBER_CAPTION")` : résout chaque membre DIRECTEMENT
        // par sa clé, sans scanner la dimension — contrairement à MDSCHEMA_MEMBERS qui scanne
        // toute la hiérarchie (des milliers de titres) → gel. Et la DMV ne supporte pas `IN`.
        // Une seule requête MDX résout tout un paquet ; repli membre par membre si un membre
        // du paquet est invalide (référence périmée) et fait échouer la requête entière.
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
                    catch { /* membre invalide (référence périmée) : ignoré */ }
            }
        }
        if (found.Count > 0) store.PutCachedCaptions(server, catalog, cube, found);

        var result = new Dictionary<string, string?>(names.Count);
        foreach (var name in names)
            result[name] = cached.TryGetValue(name, out var c) ? c : found.GetValueOrDefault(name);
        return result;
    }

    /// <summary>
    /// Caption d'UN membre par son unique name. Délègue au lookup groupé (cache SQLite persistant).
    /// Fonctionne pour n'importe quel membre indépendamment de la taille de la dimension. Null si introuvable.
    /// </summary>
    public async Task<string?> GetMemberCaptionAsync(string cube, string memberUniqueName, CancellationToken ct = default)
    {
        var d = await GetMemberCaptionsAsync(cube, new[] { memberUniqueName }, ct);
        return d.TryGetValue(memberUniqueName, out var c) ? c : null;
    }

    /// <summary>Vide le cache persistant de captions du cube (rafraîchissement manuel).</summary>
    public void InvalidateCube(string cube)
    {
        string server = session.Server ?? "", catalog = session.Catalog ?? "";
        store.InvalidateCubeCaptions(server, catalog, cube);
        _validatedCubes.TryRemove($"{server}|{catalog}|{cube}", out _);
    }

    /// <summary>Construction pure du DTO à partir des rowsets (testable sans serveur).</summary>
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
                            levelsByHier.GetValueOrDefault(un, []));
                    })
                    .OrderBy(h => h.Name).ToList());

        var dims = dimensions.Rows.Cast<DataRow>()
            .Select(r =>
            {
                string un = (string)r["DIMENSION_UNIQUE_NAME"];
                return new DimensionMeta((string)r["DIMENSION_NAME"], un, hiersByDim.GetValueOrDefault(un, []));
            })
            .OrderBy(d => d.Name)
            .ToList();

        return new CubeMeta(cube, measureFolders, dims);
    }
}
