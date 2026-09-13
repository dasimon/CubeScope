using System.Collections.Concurrent;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices;
using AmoTrace = Microsoft.AnalysisServices.Trace; // ambiguous with System.Diagnostics.Trace

namespace CubeScope.Core.Profiler;

/// <summary>
/// Per-query profiler via SSAS trace (validated in the 2026-07-24 spike). A single persistent
/// server trace per connection, events buffered by SessionID. Degradable:
/// if trace creation fails (no admin rights), the service switches to Unavailable
/// and the application carries on without a profiler. Server trace = global → Stop()+Drop() on dispose.
/// </summary>
public sealed class ProfilerService : IDisposable
{
    // Name scoped to the PID: two CubeScope instances (or a test + the app) on the same server
    // do not steal each other's trace. Orphan cleanup only drops dead PIDs.
    private const string TracePrefix = "CubeScope_Profiler_";
    private static readonly string TraceName = $"{TracePrefix}{Environment.ProcessId}";
    private static readonly TimeSpan BufferWindow = TimeSpan.FromMinutes(2);

    // "Completed" events only ("Begin" events have no Duration → rejected by the server).
    private static readonly TraceEventClass[] Events =
    [
        TraceEventClass.QueryEnd,
        TraceEventClass.QuerySubcube, TraceEventClass.QuerySubcubeVerbose,
        TraceEventClass.GetDataFromAggregation, TraceEventClass.GetDataFromCache,
        TraceEventClass.CalculateNonEmptyEnd, TraceEventClass.SerializeResultsEnd,
        TraceEventClass.ExecuteMdxScriptEnd,
    ];

    private static readonly TraceColumn[] Columns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass,
        TraceColumn.Duration, TraceColumn.TextData, TraceColumn.SessionID,
    ];

    private readonly Lock _lock = new();
    // Per-session buffer: queue of recent events (pruned with a sliding window).
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(ProfileEvent Ev, DateTime At)>> _bySession = new();
    private Server? _amo;
    private AmoTrace? _trace;

    public ProfilerStatus Status { get; private set; } = ProfilerStatus.NotInitialized;
    public string? StatusDetail { get; private set; }

    /// <summary>Creates and starts the trace for a server. Never blocking / never fatal.</summary>
    public void Initialize(string dataSource)
    {
        lock (_lock)
        {
            // Real race: `ServerHost` runs Initialize in the background on connection,
            // and shutdown can win it. Without this guard, Dispose() finishes its cleanup,
            // releases the lock, then Initialize creates and starts a trace that nobody will
            // ever release — an orphan `CubeScope_Profiler_<pid>` trace on an SSAS server
            // shared with production, the only path where DisposeAsync is of no use.
            if (_disposed) return;

            if (Status == ProfilerStatus.Ready && string.Equals(_amo?.Name, dataSource, StringComparison.OrdinalIgnoreCase))
                return;
            Teardown();
            try
            {
                _amo = new Server();
                _amo.Connect($"Data Source={dataSource};Integrated Security=SSPI;");

                // Sweep orphan traces: only those whose local CubeScope process
                // is dead (previous crash) — never the one of a live sibling instance.
                foreach (var t in _amo.Traces.Cast<AmoTrace>().Where(t => t.Name.StartsWith(TracePrefix)).ToList())
                    if (IsOrphan(t.Name))
                        try { t.Drop(); } catch { /* ignore */ }

                _trace = _amo.Traces.Add(TraceName);
                foreach (var ev in Events)
                {
                    var te = new TraceEvent(ev);
                    foreach (var col in Columns) te.Columns.Add(col);
                    _trace.Events.Add(te);
                }
                PruneAndUpdate(_trace);

                _trace.OnEvent += OnTraceEvent;
                _trace.Start();

                Status = ProfilerStatus.Ready;
                StatusDetail = $"trace active sur {dataSource} ({_trace.Events.Count} événements suivis)";
            }
            catch (Exception ex)
            {
                Status = ProfilerStatus.Unavailable;
                StatusDetail = $"[{ex.GetType().Name}] {ex.GetBaseException().Message} — " +
                    "créer une trace exige le rôle administrateur de l'instance SSAS.";
                Teardown();
            }
        }
    }

    /// <summary>
    /// Self-correcting loop: each EventClass has its own column allow-list, validated
    /// server-side on Update(). The error message gives (eventId, columnId) = AMO enum
    /// values → we remove the offending column from the event and retry.
    /// </summary>
    private static void PruneAndUpdate(AmoTrace trace)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try { trace.Update(); return; }
            catch (OperationException ex)
            {
                var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Id=(\d+)\D+Id=(\d+)");
                if (!m.Success) throw;
                int evId = int.Parse(m.Groups[1].Value), colId = int.Parse(m.Groups[2].Value);
                var te = trace.Events.Cast<TraceEvent>().FirstOrDefault(t => (int)t.EventID == evId);
                var col = te?.Columns.Cast<TraceColumn>().FirstOrDefault(c => (int)c == colId);
                if (te is null || col is null) throw;
                te.Columns.Remove(col.Value);
                if (te.Columns.Count == 0) trace.Events.Remove(te);
            }
        }
        throw new InvalidOperationException("Impossible de valider la définition de trace après élagage.");
    }

    private void OnTraceEvent(object sender, TraceEventArgs e)
    {
        string? session = Safe(() => e.SessionID);
        if (string.IsNullOrEmpty(session)) return;
        var pe = new ProfileEvent(
            Safe(() => e.EventClass.ToString()) ?? "?",
            SafeInt(() => (int)e.EventSubclass),
            SafeLong(() => e.Duration),
            Safe(() => e.TextData),
            DateTime.UtcNow);

        var q = _bySession.GetOrAdd(session, _ => new());
        q.Enqueue((pe, pe.CapturedUtc));
        // Prune old events (sliding window) to bound memory.
        var cutoff = DateTime.UtcNow - BufferWindow;
        while (q.TryPeek(out var head) && head.At < cutoff) q.TryDequeue(out _);
    }

    /// <summary>Events of a session captured since a given instant (end of query).</summary>
    public IReadOnlyList<ProfileEvent> DrainSince(string session, DateTime sinceUtc)
    {
        if (Status != ProfilerStatus.Ready || string.IsNullOrEmpty(session)) return [];
        if (!_bySession.TryGetValue(session, out var q)) return [];
        return q.Where(x => x.At >= sinceUtc).Select(x => x.Ev)
            .OrderBy(e => e.CapturedUtc).ToList();
    }

    private void Teardown()
    {
        if (_trace is not null)
        {
            try { _trace.OnEvent -= OnTraceEvent; } catch { /* ignore */ }
            try { _trace.Stop(); } catch { /* ignore */ }
            try { _trace.Drop(); } catch { /* ignore */ }
            _trace = null;
        }
        try { _amo?.Disconnect(); } catch { /* ignore */ }
        _amo = null;
        _bySession.Clear();
        Status = ProfilerStatus.NotInitialized;
        StatusDetail = null;
    }

    private bool _disposed;

    /// <summary>
    /// Throws if the service has already been disposed. Exists so that a test can check that
    /// disposing the DI container did reach this singleton: this same path is the one
    /// that runs the Stop() + Drop() of the SSAS trace.
    /// </summary>
    public void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Teardown();
        }
    }

    /// <summary>A CubeScope_Profiler_&lt;pid&gt; trace is an orphan if its local process no longer exists.</summary>
    internal static bool IsOrphan(string traceName)
    {
        if (traceName == TraceName) return true; // our own leftover name (crash then restart with the same PID)
        if (!int.TryParse(traceName.AsSpan(TracePrefix.Length), out int pid)) return false; // unexpected name: leave it alone
        try { using var _ = System.Diagnostics.Process.GetProcessById(pid); return false; } // process alive
        catch (ArgumentException) { return true; } // process dead → orphan
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
}
