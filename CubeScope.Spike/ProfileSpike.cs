using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.AdomdClient;
using AmoTrace = Microsoft.AnalysisServices.Trace; // disambiguates from System.Diagnostics.Trace

/// <summary>
/// Go/no-go spike for the profiler (post-MVP): validates that an SSAS trace created via AMO
/// .NET Core really pushes events live (Trace.OnEvent), that we have the admin rights to
/// create it, and that we can derive the Formula Engine / Storage Engine breakdown per query.
/// READ-ONLY: the trace writes nothing to the cube; the MDX query is a plain SELECT.
/// </summary>
internal static class ProfileSpike
{
    private sealed record Captured(string EventClass, int Subclass, long Duration, long Cpu,
        string? Text, string? Session, string? Spid);

    // Only "completed" events (they carry a Duration): "Begin" events reject the end
    // columns (Duration/CpuTime/EndTime) → server-side rejection on Update.
    private static readonly TraceEventClass[] WantedEvents =
    [
        TraceEventClass.QueryEnd,
        TraceEventClass.QuerySubcube, TraceEventClass.QuerySubcubeVerbose,
        TraceEventClass.GetDataFromAggregation, TraceEventClass.GetDataFromCache,
        TraceEventClass.CalculateNonEmptyEnd,
        TraceEventClass.SerializeResultsEnd,
        TraceEventClass.ExecuteMdxScriptEnd,
    ];

    // MINIMAL set supported by all the targeted completed events (each EventClass has its
    // own column allowlist, validated by the server on Update).
    private static readonly TraceColumn[] WantedColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass,
        TraceColumn.Duration, TraceColumn.TextData, TraceColumn.SessionID,
    ];

    public static int Run(string server, string catalog)
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine($" CubeScope.Spike — Profiler (SSAS trace) — {server} / {catalog}");
        Console.WriteLine("==============================================================");

        const string traceName = "CubeScope_Profiler_Spike";
        var captured = new ConcurrentQueue<Captured>();
        Server? amo = null;
        AmoTrace? trace = null;

        // --- 1. AMO connection + trace creation (admin rights required) ---
        try
        {
            amo = new Server();
            amo.Connect($"Data Source={server};Integrated Security=SSPI;");
            Console.WriteLine($"AMO connected (ServerMode={amo.ServerMode}, Version={amo.Version}).");

            var stale = amo.Traces.FindByName(traceName);
            stale?.Drop();

            trace = amo.Traces.Add(traceName);
            foreach (var ev in WantedEvents)
            {
                var te = new TraceEvent(ev);
                foreach (var col in WantedColumns) te.Columns.Add(col);
                trace.Events.Add(te);
            }

            // Each EventClass has its own column allowlist, validated server-side on Update.
            // The error message gives (event ID, column ID) = AMO enum values → remove the
            // offending column from the event concerned and retry. Bounded loop.
            int pruned = 0;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try { trace.Update(); break; }
                catch (OperationException ex)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Id=(\d+)\D+Id=(\d+)");
                    if (!m.Success) throw;
                    int evId = int.Parse(m.Groups[1].Value), colId = int.Parse(m.Groups[2].Value);
                    var te = trace.Events.Cast<TraceEvent>().FirstOrDefault(t => (int)t.EventID == evId);
                    var col = te?.Columns.Cast<TraceColumn>().FirstOrDefault(c => (int)c == colId);
                    if (te is null || col is null) throw;
                    te.Columns.Remove(col.Value);
                    if (te.Columns.Count == 0) trace.Events.Remove(te); // event emptied: drop it
                    pruned++;
                }
            }
            Console.WriteLine($"Trace created: {trace.Events.Count} events tracked " +
                $"({pruned} invalid column(s) pruned automatically).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED trace creation: [{ex.GetType().Name}] {ex.GetBaseException().Message}");
            Console.WriteLine("→ Likely cause: insufficient rights (creating a trace requires the SSAS instance " +
                "administrator role), or AMO .NET Core does not support this operation.");
            amo?.Disconnect();
            PrintVerdict(false, "trace creation impossible");
            return 1;
        }

        // --- 2. Live subscription + execution of a traced query ---
        int eventCount = 0;
        trace.OnEvent += (_, e) =>
        {
            Interlocked.Increment(ref eventCount);
            captured.Enqueue(new Captured(
                SafeEnum(() => e.EventClass.ToString()),
                SafeInt(() => (int)e.EventSubclass),
                SafeLong(() => e.Duration),
                0,
                SafeStr(() => e.TextData),
                SafeStr(() => e.SessionID),
                null));
        };
        trace.Stopped += (_, _) => Console.WriteLine("(trace stopped server-side)");

        string? ourSession = null;
        long queryMs = 0;
        try
        {
            trace.Start();
            Console.WriteLine("Trace started (live subscription). Running the test query…");

            using var conn = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
            conn.Open();
            conn.ChangeDatabase(catalog);
            ourSession = conn.SessionID;
            Console.WriteLine($"SessionID ADOMD : {ourSession}");

            // Deliberately non-trivial query (moderate crossjoin) to generate Storage Engine work.
            // Cube/measure/hierarchy names via env variables (otherwise generic placeholders):
            // CUBESCOPE_TEST_CUBE, CUBESCOPE_TEST_MEASURE, CUBESCOPE_TEST_HIERARCHY[2].
            string cube = Environment.GetEnvironmentVariable("CUBESCOPE_TEST_CUBE") ?? "Cube";
            string measure = Environment.GetEnvironmentVariable("CUBESCOPE_TEST_MEASURE") ?? "[Measures].[Amount]";
            string hier1 = Environment.GetEnvironmentVariable("CUBESCOPE_TEST_HIERARCHY") ?? "[Dim].[Hier]";
            string hier2 = Environment.GetEnvironmentVariable("CUBESCOPE_TEST_HIERARCHY2") ?? "[Dim2].[Hier2]";
            string mdx = $$"""
                SELECT NON EMPTY { {{measure}} } ON COLUMNS,
                       NON EMPTY Head({{hier1}}.Members, 20)
                               * Head({{hier2}}.Members, 20) ON ROWS
                FROM [{{cube}}]
                """;
            var sw = Stopwatch.StartNew();
            using (var cmd = new AdomdCommand(mdx, conn))
            {
                var cs = cmd.ExecuteCellSet(); // CellSet is not IDisposable
                _ = cs.Cells.Count;
            }
            sw.Stop();
            queryMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"Query executed in {queryMs} ms. Waiting for the events to flush…");

            // Trace events arrive asynchronously: give the XMLA push time.
            for (int i = 0; i < 20 && eventCount == 0; i++) Thread.Sleep(150);
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED execution/subscription: [{ex.GetType().Name}] {ex.GetBaseException().Message}");
        }
        finally
        {
            try { trace.Stop(); } catch { /* ignore */ }
            try { trace.Drop(); } catch { /* ignore */ }
            amo.Disconnect();
        }

        // --- 3. Analysis ---
        var all = captured.ToList();
        Console.WriteLine($"\nEvents captured: {all.Count} (all sessions).");
        if (all.Count == 0)
        {
            Console.WriteLine("No event received → either AMO .NET Core does not push events " +
                "live (Trace.OnEvent not working), or the query produced no tracked event.");
            PrintVerdict(false, "no live event received");
            return 1;
        }

        var mine = all.Where(c => string.Equals(c.Session, ourSession, StringComparison.OrdinalIgnoreCase)).ToList();
        var scope = mine.Count > 0 ? mine : all; // no session match: show everything
        Console.WriteLine($"Events from OUR session: {mine.Count}" +
            (mine.Count == 0 ? " (no SessionID match — showing all)" : ""));

        Console.WriteLine("\n--- Detail (by event class) ---");
        foreach (var g in scope.GroupBy(c => c.EventClass).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-28} : {g.Count(),3} evt, total duration {g.Sum(c => c.Duration),6} ms");

        long total = scope.Where(c => c.EventClass == "QueryEnd").Sum(c => c.Duration);
        if (total == 0) total = queryMs;
        long se = scope.Where(c => c.EventClass is "QuerySubcube").Sum(c => c.Duration);
        int subcubes = scope.Count(c => c.EventClass is "QuerySubcube" or "QuerySubcubeVerbose");
        int cacheHits = scope.Count(c => c.EventClass == "GetDataFromCache");
        int aggHits = scope.Count(c => c.EventClass == "GetDataFromAggregation");
        long fe = Math.Max(0, total - se);

        Console.WriteLine("\n--- Profiler-style breakdown ---");
        Console.WriteLine($"  Total query duration   : {total} ms");
        Console.WriteLine($"  Storage Engine (SE)    : {se} ms  ({subcubes} Query Subcube)");
        Console.WriteLine($"  Formula Engine (FE)    : {fe} ms  (total - SE)");
        Console.WriteLine($"  Cache hits             : {cacheHits}");
        Console.WriteLine($"  Aggregation hits       : {aggHits}");

        PrintVerdict(true, $"{all.Count} live events, FE/SE breakdown obtained");
        return 0;
    }

    private static void PrintVerdict(bool go, string detail)
    {
        Console.WriteLine("\n==============================================================");
        Console.WriteLine($" VERDICT PROFILER : {(go ? "GO" : "NO-GO")} — {detail}");
        Console.WriteLine("==============================================================");
    }

    private static string SafeEnum(Func<string> f) { try { return f(); } catch { return "?"; } }
    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
    private static string? SafeStr(Func<string?> f) { try { return f(); } catch { return null; } }
}
