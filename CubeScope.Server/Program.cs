// CubeScope.Server — hôte unique : minimal API + SPA Vue 3 embarquée.
// Port libre sur localhost, ouverture du navigateur au démarrage (décisions actées).

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CubeScope.Core.Ai;
using CubeScope.Core.Models;
using CubeScope.Core.Perfmon;
using CubeScope.Core.Profiler;
using CubeScope.Core.Project;
using CubeScope.Core.Regression;
using CubeScope.Core.Script;
using CubeScope.Core.Ssas;
using CubeScope.Core.State;
using CubeScope.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;

try { Console.Title = "CubeScope"; } catch { /* pas de console (service, redirection) */ }

var builder = WebApplication.CreateBuilder(args);
// Port libre choisi par l'OS par défaut ; --port <n> pour un port fixe (proxy Vite en dev)
int portIdx = Array.IndexOf(args, "--port");
string port = portIdx >= 0 && portIdx + 1 < args.Length ? args[portIdx + 1] : "0";
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Services.AddSingleton<SsasSession>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddSingleton<ScriptService>();
builder.Services.AddSingleton<CubeProjectService>();
builder.Services.AddSingleton<FileBrowserService>();
builder.Services.AddSingleton<ScriptDeployService>();
builder.Services.AddSingleton<PerfmonService>();
builder.Services.AddSingleton<ProfilerService>();
builder.Services.AddSingleton<StateStore>(_ => new StateStore());
// Arrêt automatique à la fermeture du navigateur — inactif en dev/tests (--no-browser),
// sinon fermer la page couperait le serveur sous les pieds de Vite.
builder.Services.AddSingleton(sp => new BrowserLifetime(
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<BrowserLifetime>>(),
    enabled: !args.Contains("--no-browser")));
builder.Services.AddSignalR();

var app = builder.Build();

// --- SPA ---
// En publish : la SPA est embarquée dans l'assembly (ressources « spa/ ») → exe autonome,
// servie via EmbeddedSpaFileProvider (indépendant du dossier de l'exe). En dev : aucune
// ressource « spa/ » → repli sur le proxy Vite (qui sert la SPA et proxifie /api + /hubs).
var embedded = new EmbeddedSpaFileProvider(Assembly.GetExecutingAssembly(), "spa/");
IFileProvider? spa = embedded.Count > 0 ? embedded : null;

if (spa is not null)
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });
else
    app.UseStaticFiles();

var api = app.MapGroup("/api");

// Connexion : ouvre la session et retourne les catalogues
api.MapPost("/connection", async (ConnectRequest req, SsasSession session, StateStore store,
    PerfmonService perfmon, ProfilerService profiler, CancellationToken ct) =>
{
    var catalogs = await session.ConnectAsync(req.Server, req.Lang, ct);
    store.AddRecentConnection(req.Server, null);
    // Découverte perfmon + création de trace en arrière-plan — jamais bloquantes, dégradables
    _ = Task.Run(() => perfmon.Initialize(req.Server));
    _ = Task.Run(() => profiler.Initialize(req.Server));
    return Results.Ok(new { server = req.Server, catalogs });
});

// Choix du catalogue
api.MapPut("/connection/catalog", async (CatalogRequest req, SsasSession session, StateStore store, CancellationToken ct) =>
{
    await session.SetCatalogAsync(req.Catalog, ct);
    store.AddRecentConnection(session.Server!, req.Catalog);
    return Results.Ok();
});

// Connexions récentes (pour pré-remplir le dialogue)
api.MapGet("/connection/recent", (StateStore store) => Results.Ok(store.GetRecentConnections()));

// Exécution MDX — l'annulation passe par l'abandon de la requête HTTP (fetch abort côté SPA)
api.MapPost("/query", async (QueryRequest req, SsasSession session, QueryService queries, StateStore store,
    PerfmonService perfmon, ProfilerService profiler, IHubContext<StatsHub> statsHub, CancellationToken ct) =>
{
    try
    {
        // Snapshot perfmon AVANT + fenêtre profiler ; collecte et push APRÈS, en arrière-plan,
        // pour ne pas retarder l'affichage de la grille.
        var before = perfmon.Snapshot();
        var profileStart = DateTime.UtcNow;
        string? profileSession = session.SessionId;
        var result = await queries.ExecuteAsync(req.Mdx, ct);
        store.AddHistory(session.Server ?? "?", session.Catalog, req.Mdx, true, result.DurationMs, result.CellCount, null);
        if (before.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                var deltas = perfmon.DeltasSince(before);
                await statsHub.Clients.All.SendAsync("queryStats", new { durationMs = result.DurationMs, deltas });
            }, CancellationToken.None);
        }
        if (profiler.Status == ProfilerStatus.Ready && profileSession is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // les événements de trace arrivent en asynchrone (push XMLA)
                var events = profiler.DrainSince(profileSession, profileStart);
                var profile = ProfileAggregator.Aggregate(events, result.DurationMs);
                store.AddProfileRun(session.Server ?? "?", session.Catalog, req.Mdx, profile.TotalMs,
                    profile.StorageEngineMs, profile.FormulaEngineMs, profile.SubcubeCount,
                    profile.CacheHits, profile.AggregationHits);
                await statsHub.Clients.All.SendAsync("queryProfile", profile);
            }, CancellationToken.None);
        }
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        throw; // client parti : rien à répondre
    }
    catch (Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        store.AddHistory(session.Server ?? "?", session.Catalog, req.Mdx, false, 0, 0, msg);
        return Results.BadRequest(new { error = msg });
    }
});

// DRILLTHROUGH de la requête courante — enveloppe le MDX et retourne le rowset source.
// Limitation connue : pas de drillthrough précis par cellule, uniquement la requête entière
// (typiquement une requête à une cellule ; voir QueryService.ExecuteDrillthroughAsync).
api.MapPost("/drillthrough", async (DrillthroughRequest req, QueryService queries, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await queries.ExecuteDrillthroughAsync(req.Mdx, req.MaxRows, ct));
    }
    catch (OperationCanceledException)
    {
        throw; // client parti : rien à répondre
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// ClearCache du catalogue courant (DatabaseID résolu via AMO — la confirmation est côté UI)
api.MapPost("/cache/clear", async (CacheService cache, CancellationToken ct) =>
{
    try
    {
        var (databaseId, durationMs) = await cache.ClearCacheAsync(ct);
        return Results.Ok(new { databaseId, durationMs });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// MDX Script du cube (AMO), graphe de dépendances d'un élément, doc Markdown
api.MapGet("/script/{cube}", async (string cube, ScriptService scripts,
    [FromQuery] bool refresh, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await scripts.GetScriptAsync(cube, refresh, ct));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapGet("/script/{cube}/dependencies", async (string cube, [FromQuery] string name,
    ScriptService scripts, MetadataService meta, CancellationToken ct) =>
{
    try
    {
        var script = await scripts.GetScriptAsync(cube, ct: ct);
        var cubeMeta = await meta.GetCubeMetaAsync(cube, ct: ct);
        return Results.Ok(DependencyService.Resolve(script, cubeMeta, name));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
// Tracer IA : explique en français comment un membre calculé / set construit sa valeur,
// à partir de son expression + des expressions des membres calculés dont il dépend
// (transitif, via le graphe de dépendances existant).
api.MapGet("/script/{cube}/explain", async (string cube, [FromQuery] string name,
    [FromQuery] string? lang, ScriptService scripts, MetadataService meta, AiService ai, CancellationToken ct) =>
{
    try
    {
        var script = await scripts.GetScriptAsync(cube, ct: ct);
        var target = script.Commands.FirstOrDefault(c => c.Name == name);
        if (target is null)
            return Results.BadRequest(new { error = $"Membre introuvable dans le script : {name}" });

        if (!AiService.IsConfigured)
            return Results.BadRequest(new { error = "Clé API absente : ANTHROPIC_API_KEY non configurée." });

        var cubeMeta = await meta.GetCubeMetaAsync(cube, ct: ct);
        var graph = DependencyService.Resolve(script, cubeMeta, name);

        // Dépendances calculées (membre/set), transitives, dédupliquées, plafonnées.
        const int maxDeps = 30;
        const int maxChars = 8000;
        var byName = script.Commands
            .Where(c => c.Kind is "CalculatedMember" or "NamedSet")
            .ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var deps = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        void Walk(DependencyNode node)
        {
            foreach (var child in node.Dependencies)
            {
                if (child.Kind is "CalculatedMember" or "NamedSet" && seen.Add(child.Name))
                {
                    if (deps.Count < maxDeps) deps.Add(child.Name);
                    Walk(child);
                }
            }
        }
        Walk(graph.Root);

        var sb = new System.Text.StringBuilder();
        sb.Append("MEMBRE CIBLE: ").Append(target.Name).Append('\n');
        sb.Append("AS ").Append(target.Expression).Append("\n\n");
        sb.Append("DÉPEND DE:\n");
        foreach (var depName in deps)
        {
            if (sb.Length >= maxChars) break;
            if (byName.TryGetValue(depName, out var depCmd))
                sb.Append("- ").Append(depName).Append(": ").Append(depCmd.Expression).Append('\n');
        }
        string context = sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();

        string text = await ai.RunAsync(AiAction.Tracer, context, lang ?? "fr", ct);
        return Results.Ok(new { text });
    }
    catch (OperationCanceledException)
    {
        throw; // client parti
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapGet("/doc/{cube}", async (string cube, ScriptService scripts, MetadataService meta,
    SsasSession session, CancellationToken ct) =>
{
    try
    {
        var script = await scripts.GetScriptAsync(cube, ct: ct);
        var cubeMeta = await meta.GetCubeMetaAsync(cube, ct: ct);
        string md = DocGenerator.Generate(cubeMeta, script, session.Server ?? "?", session.Catalog ?? "?");
        return Results.Text(md, "text/markdown; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Mode projet SSDT : le MDX Script est lu/écrit dans le .cube (source de vérité = projet)
api.MapPost("/project/open", (ProjectOpenRequest req, CubeProjectService projects, StateStore store) =>
{
    try
    {
        var script = projects.Load(req.Path);
        store.AddRecentProject(req.Path);
        return Results.Ok(script);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapPost("/project/save", (ProjectSaveRequest req, CubeProjectService projects) =>
{
    try
    {
        return Results.Ok(new { warnings = projects.Save(req.Path, req.FullText) });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapPost("/project/deploy", async (ProjectDeployRequest req, CubeProjectService projects,
    ScriptDeployService deploy, StateStore store, CancellationToken ct) =>
{
    try
    {
        var script = projects.Load(req.Path); // toujours l'état DISQUE du projet (l'UI sauvegarde avant)
        var result = await Task.Run(
            () => deploy.Deploy(req.Server, req.Catalog, script.CubeName, script.FullText, req.Force), ct);
        if (result.Deployed)
            store.AddDeployLog(req.Server, req.Catalog, script.CubeName, req.Path, script.FullText.Length, req.Force);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapGet("/project/recent", (StateStore store) => Results.Ok(store.GetRecentProjects()));
api.MapGet("/project/deploylog", (StateStore store) =>
{
    try { return Results.Ok(store.GetDeployLog()); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
api.MapGet("/project/calcprops", (string path, CubeProjectService projects) =>
{
    try
    {
        return Results.Ok(projects.GetCalculationProperties(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapPost("/project/calcprops", (CalcPropRequest req, CubeProjectService projects) =>
{
    try
    {
        projects.SaveCalculationProperty(
            req.Path, req.Reference, req.FormatString, req.DisplayFolder, req.Description);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Renommage sûr d'un membre calculé / set nommé : réécrit la définition + toutes les
// références textuelles dans le MDX Script (mode projet — le texte à réécrire est celui
// de l'éditeur, jamais lu depuis le disque côté serveur).
api.MapPost("/script/rename", (RenameRequest req) =>
{
    try
    {
        return Results.Ok(MemberRenamer.Rename(req.Script, req.OldName, req.NewName));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Analyse d'impact : diff de deux versions du MDX Script (membres calculés / sets ajoutés,
// supprimés, modifiés) + fermeture transitive des membres du nouveau script impactés en aval.
// Pur texte, aucune session ni accès disque côté serveur.
api.MapPost("/script/impact", (ImpactRequest req) =>
{
    try
    {
        return Results.Ok(ImpactAnalyzer.Analyze(req.OldScript, req.NewScript));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Bibliothèque de snippets MDX (locale, SQLite)
api.MapGet("/snippets", (StateStore store) => Results.Ok(store.GetSnippets()));
api.MapPost("/snippets", (SnippetRequest req, StateStore store) =>
{
    try
    {
        return Results.Ok(new { id = store.AddSnippet(req.Name, req.Mdx) });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapDelete("/snippets/{id:long}", (long id, StateStore store) =>
{
    store.DeleteSnippet(id);
    return Results.Ok();
});

// Non-régression MDX : baseline (requête + résultat courant) puis relance/diff après un
// changement de script. On sérialise le QueryResult courant tel quel comme référence.
api.MapPost("/regression", (RegressionSaveRequest req, StateStore store) =>
{
    try
    {
        var json = JsonSerializer.Serialize(req.Expected);
        return Results.Ok(new { id = store.AddRegressionCase(req.Name, req.Mdx, json) });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Liste légère : on ne renvoie PAS l'ExpectedJson (lourd), seulement l'identité des cas.
api.MapGet("/regression", (StateStore store) =>
    Results.Ok(store.GetRegressionCases().Select(c => new { c.Id, c.Name, c.Mdx, c.CreatedUtc })));

// Relance tous les cas et compare à la baseline. Nécessite une connexion SSAS vive.
// Crux JSON : on re-sérialise le résultat vivant (« actual ») pour que ses cellules soient des
// JsonElement, comme la baseline désérialisée → comparaison .ToString() symétrique et stable.
// Un cas qui plante n'échoue pas toute la relance : il est reporté match=false + message.
api.MapPost("/regression/run", async (StateStore store, QueryService queries, CancellationToken ct) =>
{
    var cases = store.GetRegressionCases();
    var results = new List<object>(cases.Count);
    foreach (var c in cases)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<QueryResult>(c.ExpectedJson)!;
            var live = await queries.ExecuteAsync(c.Mdx, ct);
            var actual = JsonSerializer.Deserialize<QueryResult>(JsonSerializer.Serialize(live))!;
            var cmp = ResultComparer.Compare(expected, actual);
            results.Add(new
            {
                id = c.Id, name = c.Name, match = cmp.Match, summary = cmp.Summary,
                diffCount = cmp.Diffs.Count, diffs = cmp.Diffs.Take(20).ToList(),
            });
        }
        catch (OperationCanceledException)
        {
            throw; // client parti : on abandonne toute la relance
        }
        catch (Exception ex)
        {
            results.Add(new
            {
                id = c.Id, name = c.Name, match = false, summary = ex.GetBaseException().Message,
                diffCount = 0, diffs = new List<CellDiff>(),
            });
        }
    }
    return Results.Ok(results);
});

api.MapDelete("/regression/{id:long}", (long id, StateStore store) =>
{
    store.DeleteRegressionCase(id);
    return Results.Ok();
});
api.MapGet("/fs/list", (FileBrowserService fs, [FromQuery] string? path) =>
{
    try { return Results.Ok(fs.List(path)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Panneau IA : statut (clé configurée ?) et exécution d'une action sur le MDX courant
api.MapGet("/ai/status", () => Results.Ok(new { configured = AiService.IsConfigured, model = AiService.ActiveModel }));
api.MapPost("/ai/{action}", async (string action, AiRequest req, AiService ai, CancellationToken ct) =>
{
    if (!Enum.TryParse<AiAction>(action, ignoreCase: true, out var aiAction))
        return Results.BadRequest(new { error = $"Action inconnue : {action}" });
    try
    {
        var sw = Stopwatch.StartNew();
        string text = await ai.RunAsync(aiAction, req.Mdx, req.Lang ?? "fr", ct);
        return Results.Ok(new { text, durationMs = sw.ElapsedMilliseconds });
    }
    catch (OperationCanceledException)
    {
        throw; // client parti
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// Optimisation IA adossée au PROFIL d'exécution réel (FE/SE, sous-cubes, hits) : contexte
// = résumé du profil + MDX, injecté dans le prompt OptimiserProfil.
api.MapPost("/ai/optimize-profile", async (AiOptimizeProfileRequest req, AiService ai, CancellationToken ct) =>
{
    if (!AiService.IsConfigured)
        return Results.BadRequest(new { error = "ANTHROPIC_API_KEY non configurée" });
    try
    {
        var p = req.Profile;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PROFIL D'EXÉCUTION (ms) : total {p.TotalMs}, Formula Engine {p.FormulaEngineMs}, Storage Engine {p.StorageEngineMs}");
        sb.AppendLine($"Sous-cubes scannés : {p.SubcubeCount} · hits cache : {p.CacheHits} · hits agrégation : {p.AggregationHits}");
        if (p.Subcubes.Count > 0)
        {
            sb.AppendLine("Sous-cubes les plus coûteux :");
            foreach (var s in p.Subcubes.OrderByDescending(s => s.DurationMs).Take(10))
                sb.AppendLine($"- {s.DurationMs} ms : {(s.Text.Length > 200 ? s.Text[..200] : s.Text)}");
        }
        sb.AppendLine().AppendLine("REQUÊTE MDX :").AppendLine(req.Mdx);

        var swp = Stopwatch.StartNew();
        string text = await ai.RunAsync(AiAction.OptimiserProfil, sb.ToString(), req.Lang ?? "fr", ct);
        return Results.Ok(new { text, durationMs = swp.ElapsedMilliseconds });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Génération de MDX depuis une demande en langage naturel : métadonnées du cube (mesures,
// dimensions, hiérarchies) + la demande, injectées dans le prompt GenererMdx.
api.MapPost("/ai/generate-mdx", async (GenerateMdxRequest req, MetadataService meta, AiService ai, CancellationToken ct) =>
{
    if (!AiService.IsConfigured)
        return Results.BadRequest(new { error = "ANTHROPIC_API_KEY non configurée" });
    try
    {
        var m = await meta.GetCubeMetaAsync(req.Cube, ct: ct);

        var measuresSb = new System.Text.StringBuilder();
        measuresSb.AppendLine("MESURES :");
        foreach (var f in m.MeasureFolders)
            foreach (var mes in f.Measures)
                measuresSb.AppendLine($"- {mes.UniqueName}{(string.IsNullOrEmpty(f.Folder) ? "" : $"  (dossier {f.Folder})")}");

        var dimsSb = new System.Text.StringBuilder();
        dimsSb.AppendLine("DIMENSIONS :");
        foreach (var d in m.Dimensions)
        {
            dimsSb.AppendLine($"- {d.UniqueName}{(string.IsNullOrEmpty(d.Description) ? "" : $"  — {d.Description}")} :");
            foreach (var h in d.Hierarchies)
                dimsSb.AppendLine($"    - {h.UniqueName}{(string.IsNullOrEmpty(h.Description) ? "" : $"  — {h.Description}")}  (niveaux : {string.Join(" > ", h.Levels.Select(l => l.Name))})");
        }

        // Chaque section bornée indépendamment (pas un budget global) : sur un cube avec des
        // centaines de mesures, la liste des mesures ne doit jamais évincer les dimensions —
        // c'est justement la dimension d'analyse (ROWS) que l'IA doit choisir correctement.
        const int maxSection = 20000;
        string metaCtx = $"MÉTADONNÉES DU CUBE [{m.CubeName}]\n"
            + TruncateSection(measuresSb.ToString(), maxSection)
            + TruncateSection(dimsSb.ToString(), maxSection);
        string context = $"{metaCtx}\n\nDEMANDE : {req.Question}";

        static string TruncateSection(string s, int max) =>
            s.Length <= max ? s : s[..max] + "\n… (section tronquée)\n";

        var sw = Stopwatch.StartNew();
        string text = await ai.RunAsync(AiAction.GenererMdx, context, req.Lang ?? "fr", ct);
        return Results.Ok(new { text, durationMs = sw.ElapsedMilliseconds });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Statut perfmon (l'UI affiche pourquoi les stats sont absentes le cas échéant)
api.MapGet("/stats/status", (PerfmonService perfmon) =>
    Results.Ok(new { status = perfmon.Status.ToString(), detail = perfmon.StatusDetail }));

// Statut profiler (trace SSAS) — Unavailable si droits admin absents
api.MapGet("/profiler/status", (ProfilerService profiler) =>
    Results.Ok(new { status = profiler.Status.ToString(), detail = profiler.StatusDetail }));

// Historique des runs profiler (comparaison avant/après)
api.MapGet("/profiler/history", (StateStore store) =>
{
    try { return Results.Ok(store.GetProfileRuns()); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Historique des requêtes
api.MapGet("/history", (StateStore store, [FromQuery] int limit = 100) => Results.Ok(store.GetHistory(limit)));

// Métadonnées : cubes du catalogue courant, puis arbre d'un cube (cache mémoire, ?refresh=true pour forcer)
api.MapGet("/metadata/cubes", async (MetadataService meta, CancellationToken ct) =>
    Results.Ok(await meta.GetCubesAsync(ct)));
api.MapGet("/metadata/cube/{cube}", async (string cube, MetadataService meta,
    [FromQuery] bool refresh, CancellationToken ct) =>
    Results.Ok(await meta.GetCubeMetaAsync(cube, refresh, ct)));
// Membres d'une hiérarchie (autocomplétion, lazy + cache serveur, plafonné)
api.MapGet("/metadata/members", async ([FromQuery] string cube, [FromQuery] string hierarchy,
    MetadataService meta, CancellationToken ct) =>
    Results.Ok(await meta.GetMembersAsync(cube, hierarchy, ct: ct)));
// Caption d'UN membre par unique name (lookup ciblé — pour le survol, indépendant du cap 1000)
api.MapGet("/metadata/member", async ([FromQuery] string cube, [FromQuery] string name,
    MetadataService meta, CancellationToken ct) =>
{
    try { return Results.Ok(new { caption = await meta.GetMemberCaptionAsync(cube, name, ct) }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
// Captions de plusieurs membres d'un coup (prefetch groupé — cache SQLite persistant)
api.MapPost("/metadata/captions", async (CaptionsRequest req, MetadataService meta, CancellationToken ct) =>
{
    try { return Results.Ok(await meta.GetMemberCaptionsAsync(req.Cube, req.Names, ct)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
// Rafraîchissement manuel : vide le cache persistant des captions du cube
api.MapPost("/metadata/captions/refresh", (CaptionRefreshRequest req, MetadataService meta) =>
{
    try { meta.InvalidateCube(req.Cube); return Results.Ok(); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Balise de départ de page (navigator.sendBeacon sur `pagehide`) : distingue une fermeture
// ou un rechargement — délai d'arrêt court — d'un simple transport qui lâche, où le client
// va se reconnecter tout seul et où couper vite tuerait le serveur sous une page vivante.
api.MapPost("/leaving", (BrowserLifetime browser) =>
{
    browser.NoticeClientLeaving();
    return Results.NoContent();
});

// SignalR : push des stats perfmon post-exécution (décision actée : SignalR pour ce qui streame)
app.MapHub<StatsHub>("/hubs/stats");

// Fallback SPA (routes côté client) — même provider que les fichiers statiques
if (spa is not null)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spa });
else
    app.MapFallbackToFile("index.html");

// Ouverture du navigateur une fois Kestrel démarré (sauf --no-browser, utile en dev/tests)
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault();
    if (url is null || args.Contains("--no-browser")) return;
    Console.WriteLine($"CubeScope démarré : {url}");
    try
    {
        Process.Start(new ProcessStartInfo(url.Replace("127.0.0.1", "localhost")) { UseShellExecute = true });
    }
    catch { /* pas de navigateur : l'URL est affichée en console */ }
});

app.Run();

internal sealed record ConnectRequest(string Server, string? Lang);
internal sealed record CatalogRequest(string Catalog);
internal sealed record QueryRequest(string Mdx);
internal sealed record DrillthroughRequest(string Mdx, int MaxRows);
internal sealed record AiRequest(string Mdx, string? Lang);
internal sealed record AiOptimizeProfileRequest(string Mdx, QueryProfile Profile, string? Lang);
internal sealed record GenerateMdxRequest(string Cube, string Question, string? Lang);
internal sealed record ProjectOpenRequest(string Path);
internal sealed record ProjectSaveRequest(string Path, string FullText);
internal sealed record ProjectDeployRequest(string Path, string Server, string Catalog, bool Force);
internal sealed record CaptionsRequest(string Cube, string[] Names);
internal sealed record CaptionRefreshRequest(string Cube);
internal sealed record SnippetRequest(string Name, string Mdx);
internal sealed record RegressionSaveRequest(string Name, string Mdx, QueryResult Expected);
internal sealed record CalcPropRequest(
    string Path, string Reference, string? FormatString, string? DisplayFolder, string? Description);
internal sealed record RenameRequest(string Script, string OldName, string NewName);
internal sealed record ImpactRequest(string OldScript, string NewScript);

/// <summary>
/// Hub sans méthode client→serveur : uniquement du push serveur ("queryStats").
/// Ses connexions servent aussi de signal de vie du navigateur (voir <see cref="BrowserLifetime"/>).
/// </summary>
internal sealed class StatsHub(BrowserLifetime browser) : Hub
{
    public override Task OnConnectedAsync()
    {
        browser.ClientConnected();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        browser.ClientDisconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
