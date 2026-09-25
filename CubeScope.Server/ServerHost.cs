// CubeScope.Server — single host: minimal API + embedded Vue 3 SPA.
// Free port on localhost. The display is NOT decided here: StartAsync returns
// with the URL without opening anything (the Shell then shows its native window), only RunAsync —
// the Cli path — opens the browser.

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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
// Moved off Microsoft.NET.Sdk.Web (task 1, step 5): the ASP.NET Core usings above,
// implicit under the Web SDK, must be explicit with the standard SDK.

namespace CubeScope.Server;

/// <summary>
/// Single host: minimal API + embedded Vue 3 SPA. Free port on localhost.
/// Two entry points: <see cref="StartAsync"/> returns with the URL (the Shell
/// then shows its window), <see cref="RunAsync"/> behaves as before —
/// opens the browser and waits for shutdown.
/// </summary>
public static class ServerHost
{
    /// <summary>
    /// Starts Kestrel and returns the actual URL. Opens NO browser: the
    /// caller decides on the display surface.
    /// The caller is responsible for the StopAsync + DisposeAsync pair (see task 3).
    /// </summary>
    public static async Task<(WebApplication App, string Url)> StartAsync(
        string[] args, bool browserLifetime)
    {
    try { Console.Title = "CubeScope"; } catch { /* no console (service, redirection) */ }

    var builder = WebApplication.CreateBuilder(args);
// Free port chosen by the OS by default; --port <n> for a fixed port (Vite proxy in dev)
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
builder.Services.AddSingleton<SessionsService>();
builder.Services.AddSingleton<CatalogComparisonService>();
builder.Services.AddSingleton<StateStore>(_ => new StateStore());
// Automatic shutdown when the browser closes — inactive in dev/tests (--no-browser),
// otherwise closing the page would pull the server out from under Vite.
builder.Services.AddSingleton(sp => new BrowserLifetime(
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<BrowserLifetime>>(),
    enabled: browserLifetime));
builder.Services.AddSignalR();

var app = builder.Build();

// FIRST in the pipeline: no token on this API, so a request from another web page
// (DNS rebinding, CSRF, cross-origin WebSocket) must be turned away before reaching
// static files, endpoints or hubs. See LocalRequestGuard.
app.Use(async (ctx, next) =>
{
    var h = ctx.Request.Headers;
    string? refusal = LocalRequestGuard.Check(h.Host, h.Origin, h["Sec-Fetch-Site"]);
    if (refusal is not null)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync(refusal);
        return;
    }
    ctx.Response.Headers.ContentSecurityPolicy = LocalRequestGuard.ContentSecurityPolicy;
    await next(ctx);
});

// --- SPA ---
// On publish: the SPA is embedded in the assembly ("spa/" resources) → self-contained exe,
// served through EmbeddedSpaFileProvider (independent of the exe's folder). In dev: no
// "spa/" resource → fallback to the Vite proxy (which serves the SPA and proxies /api + /hubs).
var embedded = new EmbeddedSpaFileProvider(Assembly.GetExecutingAssembly(), "spa/");
IFileProvider? spa = embedded.Count > 0 ? embedded : null;

if (spa is not null)
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });
else
    app.UseStaticFiles();

var api = app.MapGroup("/api");

// Connection: opens the session and returns the catalogs
api.MapPost("/connection", async (ConnectRequest req, SsasSession session, StateStore store,
    PerfmonService perfmon, ProfilerService profiler, CancellationToken ct) =>
{
    var catalogs = await session.ConnectAsync(req.Server, req.Lang, ct);
    store.AddRecentConnection(req.Server, null);
    // Perfmon discovery + trace creation in the background — never blocking, can degrade
    _ = Task.Run(() => perfmon.Initialize(req.Server));
    _ = Task.Run(() => profiler.Initialize(req.Server));
    return Results.Ok(new { server = req.Server, catalogs });
});

// Catalog selection
api.MapPut("/connection/catalog", async (CatalogRequest req, SsasSession session, StateStore store, CancellationToken ct) =>
{
    await session.SetCatalogAsync(req.Catalog, ct);
    store.AddRecentConnection(session.Server!, req.Catalog);
    return Results.Ok();
});

// Recent connections (to prefill the dialog)
api.MapGet("/connection/recent", (StateStore store) => Results.Ok(store.GetRecentConnections()));

// MDX execution — cancellation goes through aborting the HTTP request (fetch abort on the SPA side)
api.MapPost("/query", async (QueryRequest req, SsasSession session, QueryService queries, StateStore store,
    PerfmonService perfmon, ProfilerService profiler, IHubContext<StatsHub> statsHub, CancellationToken ct) =>
{
    try
    {
        // Perfmon snapshot BEFORE + profiler window; collection and push AFTER, in the background,
        // so as not to delay displaying the grid.
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
                await Task.Delay(1000); // trace events arrive asynchronously (XMLA push)
                // Only this query's events: later queries on the same session (hover, explorer) are cut off
                var events = ProfileAggregator.QueryWindow(profiler.DrainSince(profileSession, profileStart), req.Mdx);
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
        throw; // client gone: nothing to answer
    }
    catch (Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        store.AddHistory(session.Server ?? "?", session.Catalog, req.Mdx, false, 0, 0, msg);
        return Results.BadRequest(new { error = msg });
    }
});

// DRILLTHROUGH of the current query — wraps the MDX and returns the source rowset.
// Known limitation: no precise per-cell drillthrough, only the whole query
// (typically a single-cell query; see QueryService.ExecuteDrillthroughAsync).
api.MapPost("/drillthrough", async (DrillthroughRequest req, QueryService queries, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await queries.ExecuteDrillthroughAsync(req.Mdx, req.MaxRows, ct));
    }
    catch (OperationCanceledException)
    {
        throw; // client gone: nothing to answer
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// ClearCache of the current catalog (DatabaseID resolved through AMO — confirmation is on the UI side)
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

// Cube MDX Script (AMO), dependency graph of an item, Markdown doc
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
// AI tracer: explains how a calculated member / set builds its value (in the UI language, French by default),
// from its expression + the expressions of the calculated members it depends on
// (transitive, through the existing dependency graph).
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

        // Calculated dependencies (member/set), transitive, deduplicated, capped.
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
        throw; // client gone
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

// SSDT project mode: the MDX Script is read/written in the .cube (source of truth = project)
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
        var script = projects.Load(req.Path); // always the project's ON-DISK state (the UI saves first)
        var result = await Task.Run(
            () => deploy.Deploy(req.Server, req.Catalog, script.CubeName, script.FullText,
                req.Force, store.GetDevServers()), ct);
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

// Safe rename of a calculated member / named set: rewrites the definition + every
// textual reference in the MDX Script (project mode — the text to rewrite is the
// editor's, never read from disk on the server side).
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

// Impact analysis: diff of two versions of the MDX Script (calculated members / sets added,
// removed, modified) + transitive closure of the new script's members impacted downstream.
// Pure text, no session or disk access on the server side.
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

// MDX snippet library (local, SQLite)
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

// MDX regression testing: baseline (query + current result) then re-run/diff after a
// script change. The current QueryResult is serialized as is as the reference.
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

// Lightweight list: the ExpectedJson (heavy) is NOT returned, only the identity of the cases.
api.MapGet("/regression", (StateStore store) =>
    Results.Ok(store.GetRegressionCases().Select(c => new { c.Id, c.Name, c.Mdx, c.CreatedUtc })));

// Re-runs every case and compares it to the baseline. Requires a live SSAS connection.
// JSON crux: the live result ("actual") is re-serialized so that its cells are
// JsonElement, like the deserialized baseline → symmetric and stable .ToString() comparison.
// A case that crashes does not fail the whole run: it is reported as match=false + message.
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
            throw; // client gone: abort the whole run
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

// AI panel: status (key configured?) and running an action on the current MDX
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
        throw; // client gone
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.GetBaseException().Message });
    }
});

// AI optimization backed by the real execution PROFILE (FE/SE, subcubes, hits): context
// = profile summary + MDX, injected into the OptimiserProfil prompt.
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

// MDX generation from a natural-language request: cube metadata (measures,
// dimensions, hierarchies) + the request, injected into the GenererMdx prompt.
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

        // Each section capped independently (not a global budget): on a cube with
        // hundreds of measures, the measure list must never crowd out the dimensions —
        // the analysis dimension (ROWS) is precisely what the AI must pick correctly.
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

// Perfmon status (the UI shows why stats are missing, if they are)
api.MapGet("/stats/status", (PerfmonService perfmon) =>
    Results.Ok(new { status = perfmon.Status.ToString(), detail = perfmon.StatusDetail }));

// Profiler status (SSAS trace) — Unavailable if admin rights are missing
api.MapGet("/profiler/status", (ProfilerService profiler) =>
    Results.Ok(new { status = profiler.Status.ToString(), detail = profiler.StatusDetail }));

// Profiler run history (before/after comparison)
api.MapGet("/profiler/history", (StateStore store) =>
{
    try { return Results.Ok(store.GetProfileRuns()); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Query history
api.MapGet("/history", (StateStore store, [FromQuery] int limit = 100) => Results.Ok(store.GetHistory(limit)));

// Metadata: cubes of the current catalog, then the tree of a cube (in-memory cache, ?refresh=true to force)
api.MapGet("/metadata/cubes", async (MetadataService meta, CancellationToken ct) =>
    Results.Ok(await meta.GetCubesAsync(ct)));
api.MapGet("/metadata/cube/{cube}", async (string cube, MetadataService meta,
    [FromQuery] bool refresh, CancellationToken ct) =>
    Results.Ok(await meta.GetCubeMetaAsync(cube, refresh, ct)));
// Members of a hierarchy (autocompletion, lazy + server cache, capped)
api.MapGet("/metadata/members", async ([FromQuery] string cube, [FromQuery] string hierarchy,
    MetadataService meta, CancellationToken ct) =>
    Results.Ok(await meta.GetMembersAsync(cube, hierarchy, ct: ct)));
// Servers declared as development: explicit list, the only place a script deployment
// is allowed to. The catalog name no longer tells anything apart (prod and dev carry the same one).
api.MapGet("/dev-servers", (StateStore store) => Results.Ok(store.GetDevServers()));
api.MapPut("/dev-servers", (DevServerRequest req, StateStore store) =>
{
    store.SetDevServer(req.Server, req.IsDev);
    return Results.Ok(store.GetDevServers());
});

// One level of the member tree (explorer, SSMS style). `hierarchy=true` at the first level,
// under the "Membres" folder; `false` afterwards, when `parent` is a member.
api.MapGet("/metadata/children", async ([FromQuery] string cube, [FromQuery] string parent,
    [FromQuery] bool hierarchy, MetadataService meta, CancellationToken ct) =>
    Results.Ok(await meta.GetChildrenAsync(cube, parent, hierarchy, ct: ct)));

// Caption of ONE member by unique name (targeted lookup — for hover, independent of the 1000 cap)
api.MapGet("/metadata/member", async ([FromQuery] string cube, [FromQuery] string name,
    MetadataService meta, CancellationToken ct) =>
{
    try { return Results.Ok(new { caption = await meta.GetMemberCaptionAsync(cube, name, ct) }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
// Captions of several members at once (batched prefetch — persistent SQLite cache)
api.MapPost("/metadata/captions", async (CaptionsRequest req, MetadataService meta, CancellationToken ct) =>
{
    try { return Results.Ok(await meta.GetMemberCaptionsAsync(req.Cube, req.Names, ct)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});
// Manual refresh: clears the cube's persistent caption cache
api.MapPost("/metadata/captions/refresh", (CaptionRefreshRequest req, MetadataService meta) =>
{
    try { meta.InvalidateCube(req.Cube); return Results.Ok(); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Comparison of the same query between the current catalog and another one on the same server:
// "did a figure move?" after a script change.
api.MapPost("/compare", async (CompareRequest req, CatalogComparisonService comparison,
    CancellationToken ct) =>
{
    try { return Results.Ok(await comparison.CompareAsync(req.Mdx, req.Catalog, ct)); }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Open sessions on the instance. Reading is restricted to SSAS admins: when refused, the
// message is returned as is, the UI degrades (same approach as the Profiler).
api.MapGet("/sessions", async (SessionsService sessions, CancellationToken ct) =>
{
    try { return Results.Ok(await sessions.ListAsync(ct)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Cancelling a session by its SPID. ⚠️ The list contains the sessions of production
// jobs: confirmation is handled by the UI, this endpoint does not repeat it.
api.MapPost("/sessions/{spid:int}/cancel", async (int spid, SessionsService sessions, CancellationToken ct) =>
{
    try
    {
        bool cancelled = await sessions.CancelAsync(spid, ct);
        return Results.Ok(new { spid, cancelled });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.GetBaseException().Message }); }
});

// Page departure beacon (navigator.sendBeacon on `pagehide`): tells a close
// or a reload — short shutdown delay — apart from a mere transport drop, where the client
// will reconnect on its own and where shutting down quickly would kill the server under a live page.
api.MapPost("/leaving", (BrowserLifetime browser) =>
{
    browser.NoticeClientLeaving();
    return Results.NoContent();
});

// SignalR: push of post-execution perfmon stats (settled decision: SignalR for what streams)
app.MapHub<StatsHub>("/hubs/stats");

// SPA fallback (client-side routes) — same provider as the static files
if (spa is not null)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spa });
else
    app.MapFallbackToFile("index.html");

    await app.StartAsync();
    string url = app.Urls.First();
    return (app, url);
    }

    /// <summary>
    /// Historical behaviour, kept for the dev loop and the tests:
    /// starts, opens the browser (unless --no-browser), waits for shutdown.
    /// </summary>
    public static async Task RunAsync(string[] args)
    {
        bool browser = !args.Contains("--no-browser");
        var (app, url) = await StartAsync(args, browserLifetime: browser);
        try
        {
            Console.WriteLine($"CubeScope started: {url}");
            if (browser)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        url.Replace("127.0.0.1", "localhost")) { UseShellExecute = true });
                }
                catch { /* no browser: the URL is shown in the console */ }
            }
            await app.WaitForShutdownAsync();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}

internal sealed record ConnectRequest(string Server, string? Lang);
internal sealed record CatalogRequest(string Catalog);
internal sealed record QueryRequest(string Mdx);
internal sealed record CompareRequest(string Mdx, string Catalog);
internal sealed record DrillthroughRequest(string Mdx, int MaxRows);
internal sealed record AiRequest(string Mdx, string? Lang);
internal sealed record AiOptimizeProfileRequest(string Mdx, QueryProfile Profile, string? Lang);
internal sealed record GenerateMdxRequest(string Cube, string Question, string? Lang);
internal sealed record ProjectOpenRequest(string Path);
internal sealed record ProjectSaveRequest(string Path, string FullText);
internal sealed record ProjectDeployRequest(string Path, string Server, string Catalog, bool Force);
internal sealed record DevServerRequest(string Server, bool IsDev);
internal sealed record CaptionsRequest(string Cube, string[] Names);
internal sealed record CaptionRefreshRequest(string Cube);
internal sealed record SnippetRequest(string Name, string Mdx);
internal sealed record RegressionSaveRequest(string Name, string Mdx, QueryResult Expected);
internal sealed record CalcPropRequest(
    string Path, string Reference, string? FormatString, string? DisplayFolder, string? Description);
internal sealed record RenameRequest(string Script, string OldName, string NewName);
internal sealed record ImpactRequest(string OldScript, string NewScript);

/// <summary>
/// Hub with no client→server method: server push only ("queryStats").
/// Its connections also serve as the browser heartbeat (see <see cref="BrowserLifetime"/>).
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
