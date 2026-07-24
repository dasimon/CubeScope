// CubeScope.Server — hôte unique : minimal API + SPA Vue 3 embarquée.
// Port libre sur localhost, ouverture du navigateur au démarrage (décisions actées).

using System.Diagnostics;
using System.Reflection;
using CubeScope.Core.Ai;
using CubeScope.Core.Models;
using CubeScope.Core.Perfmon;
using CubeScope.Core.Profiler;
using CubeScope.Core.Project;
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
    ScriptDeployService deploy, CancellationToken ct) =>
{
    try
    {
        var script = projects.Load(req.Path); // toujours l'état DISQUE du projet (l'UI sauvegarde avant)
        var result = await Task.Run(
            () => deploy.Deploy(req.Server, req.Catalog, script.CubeName, script.FullText, req.Force), ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});
api.MapGet("/project/recent", (StateStore store) => Results.Ok(store.GetRecentProjects()));
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
api.MapGet("/fs/list", (FileBrowserService fs, [FromQuery] string? path) =>
{
    try { return Results.Ok(fs.List(path)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Panneau IA : statut (clé configurée ?) et exécution d'une action sur le MDX courant
api.MapGet("/ai/status", () => Results.Ok(new { configured = AiService.IsConfigured }));
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
internal sealed record AiRequest(string Mdx, string? Lang);
internal sealed record ProjectOpenRequest(string Path);
internal sealed record ProjectSaveRequest(string Path, string FullText);
internal sealed record ProjectDeployRequest(string Path, string Server, string Catalog, bool Force);
internal sealed record SnippetRequest(string Name, string Mdx);
internal sealed record CalcPropRequest(
    string Path, string Reference, string? FormatString, string? DisplayFolder, string? Description);

/// <summary>Hub sans méthode client→serveur : uniquement du push serveur ("queryStats").</summary>
internal sealed class StatsHub : Hub;
