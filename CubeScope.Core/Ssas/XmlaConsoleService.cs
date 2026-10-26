using System.Diagnostics;
using CubeScope.Core.Models;
using CubeScope.Core.Project;
using CubeScope.Core.State;
using Microsoft.AnalysisServices;

namespace CubeScope.Core.Ssas;

/// <summary>Outcome of an XMLA script: server messages, a DMV rowset if any, and the commands found.</summary>
public sealed record XmlaExecutionResult(
    bool Success,
    long DurationMs,
    IReadOnlyList<ProcessMessage> Messages,
    QueryResult? Rowset,
    bool RowsetTruncated,
    bool MultidimensionalResult,
    IReadOnlyList<string> Commands);

/// <summary>
/// XMLA console (replaces the SSMS "XMLA query" window): runs commands through AMO
/// (<c>Server.Execute</c>) on a short connection of its own, positioned on the current catalog
/// so that a <c>&lt;Statement&gt;</c> runs there.
///
/// Production rule (same as processing, <see cref="ProductionActionGuard"/>), applied BEFORE any
/// connection: the script is parsed first (<see cref="XmlaScriptAnalyzer"/> — invalid XML refused);
/// on a server not declared dev, the server name must be typed again, and Delete, Restore, Detach,
/// Synchronize, ClearCache, ProcessClear and a dimension ProcessFull/ProcessData are refused
/// whatever the confirmation. The text sent is the parsed script, i.e. what was checked.
/// Every attempt is logged in SQLite, refused or not.
/// </summary>
public sealed class XmlaConsoleService(SsasSession session, StateStore store)
{
    public Task<XmlaExecutionResult> ExecuteAsync(string script, string? confirmServer, CancellationToken ct = default)
    {
        string server = session.Server ?? throw new InvalidOperationException("Aucune connexion ouverte.");
        return ExecuteAsync(server, session.Catalog, script, confirmServer, store.GetDevServers(), ct);
    }

    /// <remarks>Server and dev list passed in: lets a test prove a refusal comes before any connection.</remarks>
    internal async Task<XmlaExecutionResult> ExecuteAsync(string server, string? catalog, string script,
        string? confirmServer, IReadOnlyList<string> devServers, CancellationToken ct = default)
    {
        bool production = !DevServerGuard.IsDev(devServers, server);
        var sw = new Stopwatch();
        XmlaExecutionResult result;
        try
        {
            var analysis = XmlaScriptAnalyzer.Analyze(script);
            if (production && XmlaScriptAnalyzer.ProductionRefusal(analysis) is { } refusal)
                throw new ProductionActionRefusedException(refusal);
            if (ProductionActionGuard.ConfirmationRefusal(server, devServers, confirmServer) is { } notConfirmed)
                throw new ProductionActionRefusedException(notConfirmed);

            var commands = analysis.Commands
                .Select(c => c.Detail is null ? c.Name : $"{c.Name} ({c.Detail})").ToList();
            result = await Task.Run(() =>
            {
                using var amo = new Server();
                // Builder, not concatenation: a ';' or '=' in a catalog name must not break the string.
                var cs = new System.Data.Common.DbConnectionStringBuilder
                {
                    ["Data Source"] = server,
                    ["Integrated Security"] = "SSPI",
                };
                if (!string.IsNullOrEmpty(catalog)) cs["Initial Catalog"] = catalog;
                amo.Connect(cs.ConnectionString);
                try
                {
                    sw.Start();
                    var results = amo.Execute(analysis.Normalized);
                    sw.Stop();
                    var messages = new List<ProcessMessage>();
                    QueryResult? rowset = null;
                    bool truncated = false, multidimensional = false;
                    int rowsets = 0;
                    foreach (XmlaResult r in results)
                    {
                        foreach (XmlaMessage m in r.Messages)
                            messages.Add(new ProcessMessage(
                                m is XmlaError ? "Error" : m is XmlaWarning ? "Warning" : "Info", m.Description));
                        multidimensional |= XmlaRowsetParser.IsMultidimensional(r.Value);
                        if (!XmlaRowsetParser.IsRowset(r.Value)) continue;
                        rowsets++;
                        if (rowset is not null) continue;
                        rowset = XmlaRowsetParser.Parse(r.Value, out truncated, sw.ElapsedMilliseconds);
                        if (rowset is null)
                            messages.Add(new ProcessMessage("Warning", "Le rowset renvoyé par le serveur n'a pas pu être lu."));
                    }
                    if (rowsets > 1)
                        messages.Add(new ProcessMessage("Info", $"{rowsets} rowsets renvoyés : seul le premier est affiché."));
                    // Observed: a server-side error comes back as an XmlaError, Execute does not throw.
                    bool success = results.Count > 0 && messages.All(m => m.Severity != "Error");
                    return new XmlaExecutionResult(success, sw.ElapsedMilliseconds, messages, rowset, truncated,
                        multidimensional, commands);
                }
                finally
                {
                    try { amo.Disconnect(); }
                    catch (Exception ex) { Console.WriteLine($"[CubeScope] AMO disconnect failed: {ex.GetBaseException().Message}"); }
                }
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(server, catalog, script, production, sw.ElapsedMilliseconds, false, ex.GetBaseException().Message);
            throw;
        }

        Log(server, catalog, script, production, result.DurationMs, result.Success,
            result.Success ? null : result.Messages.FirstOrDefault(m => m.Severity == "Error")?.Text);
        return result;
    }

    private void Log(string server, string? catalog, string script, bool production, long durationMs, bool success, string? error)
    {
        try { store.AddXmlaLog(server, catalog, script, production, durationMs, success, error); }
        catch (Exception ex) { Console.WriteLine($"[CubeScope] XMLA log not written: {ex.GetBaseException().Message}"); }
    }
}
