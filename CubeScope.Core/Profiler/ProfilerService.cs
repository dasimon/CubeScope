using System.Collections.Concurrent;
using CubeScope.Core.Models;
using Microsoft.AnalysisServices;
using AmoTrace = Microsoft.AnalysisServices.Trace; // ambiguïté avec System.Diagnostics.Trace

namespace CubeScope.Core.Profiler;

/// <summary>
/// Profiler par requête via trace SSAS (validé au spike 2026-07-24). Une seule trace
/// serveur persistante par connexion, événements bufferisés par SessionID. Dégradable :
/// si la création de trace échoue (droits admin absents), le service passe en Unavailable
/// et l'application continue sans profiler. Trace serveur = globale → Stop()+Drop() au dispose.
/// </summary>
public sealed class ProfilerService : IDisposable
{
    // Nom scopé au PID : deux instances CubeScope (ou un test + l'app) sur le même serveur
    // ne se volent pas la trace. Le nettoyage d'orphelines ne drop que les PID morts.
    private const string TracePrefix = "CubeScope_Profiler_";
    private static readonly string TraceName = $"{TracePrefix}{Environment.ProcessId}";
    private static readonly TimeSpan BufferWindow = TimeSpan.FromMinutes(2);

    // Événements « complétés » uniquement (les « Begin » n'ont pas de Duration → rejet serveur).
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
    // Buffer par session : file d'événements récents (élaguée par fenêtre glissante).
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(ProfileEvent Ev, DateTime At)>> _bySession = new();
    private Server? _amo;
    private AmoTrace? _trace;

    public ProfilerStatus Status { get; private set; } = ProfilerStatus.NotInitialized;
    public string? StatusDetail { get; private set; }

    /// <summary>Crée et démarre la trace pour un serveur. Jamais bloquant / jamais fatal.</summary>
    public void Initialize(string dataSource)
    {
        lock (_lock)
        {
            if (Status == ProfilerStatus.Ready && string.Equals(_amo?.Name, dataSource, StringComparison.OrdinalIgnoreCase))
                return;
            Teardown();
            try
            {
                _amo = new Server();
                _amo.Connect($"Data Source={dataSource};Integrated Security=SSPI;");

                // Balayer les traces orphelines : seulement celles dont le process CubeScope
                // local est mort (crash précédent) — jamais celle d'une instance sœur vivante.
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
    /// Boucle auto-corrective : chaque EventClass a sa liste blanche de colonnes, validée
    /// serveur au Update(). Le message d'erreur donne (eventId, columnId) = valeurs d'enum
    /// AMO → on retire la colonne fautive de l'événement et on réessaie.
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
        // Élaguer les vieux événements (fenêtre glissante) pour borner la mémoire.
        var cutoff = DateTime.UtcNow - BufferWindow;
        while (q.TryPeek(out var head) && head.At < cutoff) q.TryDequeue(out _);
    }

    /// <summary>Événements d'une session capturés depuis un instant donné (fin de requête).</summary>
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

    public void Dispose()
    {
        lock (_lock) Teardown();
    }

    /// <summary>Une trace CubeScope_Profiler_&lt;pid&gt; est orpheline si son process local n'existe plus.</summary>
    internal static bool IsOrphan(string traceName)
    {
        if (traceName == TraceName) return true; // notre propre nom résiduel (crash puis relance même PID)
        if (!int.TryParse(traceName.AsSpan(TracePrefix.Length), out int pid)) return false; // nom inattendu : ne pas toucher
        try { using var _ = System.Diagnostics.Process.GetProcessById(pid); return false; } // process vivant
        catch (ArgumentException) { return true; } // process mort → orpheline
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
}
