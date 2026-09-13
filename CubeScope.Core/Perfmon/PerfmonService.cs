using System.Diagnostics;
using CubeScope.Core.Models;

namespace CubeScope.Core.Perfmon;

/// <summary>
/// Perfmon counter deltas around a query (settled decision: counters are server-wide,
/// accepted for the MVP). Findings from the 2026-07-17 spike:
/// - categories are LOCALIZED on a French OS: "MSAS16 : MDX" (" : " separator with spaces)
///   → match the label after the first ':' (trimmed) in FR AND EN;
/// - never filter counters by name ("/sec" becomes "/s") but by CounterType
///   (NumberOfItems32/64 = cumulative, delta-able);
/// - degradable: permission failure (Win32Exception "Accès refusé") → service Unavailable,
///   the application carries on without stats.
/// Accepted MVP limitation: default instance (MSAS* prefix) only — for a named instance
/// reached by port (host:port), the port→instance name mapping cannot be
/// discovered, so we would stay on the default instance's counters.
/// </summary>
public sealed class PerfmonService : IDisposable
{
    // Categories useful per query (FR and EN labels, after the "MSASxx :" prefix)
    private static readonly string[] WantedCategories =
        ["mdx", "cache", "requête du moteur de stockage", "storage engine query"];

    private readonly Lock _lock = new();
    private List<PerformanceCounter> _counters = [];
    private string? _machine;

    public PerfmonStatus Status { get; private set; } = PerfmonStatus.NotInitialized;
    public string? StatusDetail { get; private set; }

    /// <summary>Initializes (or reinitializes) the counters for an SSAS server. Never blocks the caller.</summary>
    public void Initialize(string dataSource)
    {
        // "host:port" → machine "host" (the port is only used by ADOMD)
        string machine = dataSource.Split(':')[0].Split('\\')[0];
        lock (_lock)
        {
            if (Status == PerfmonStatus.Ready && machine.Equals(_machine, StringComparison.OrdinalIgnoreCase)) return;
            DisposeCounters();
            _machine = machine;
            try
            {
                var cats = PerformanceCounterCategory.GetCategories(machine)
                    .Where(c => c.CategoryName.StartsWith("MSAS", StringComparison.OrdinalIgnoreCase)
                             && IsWantedCategory(c.CategoryName))
                    .ToList();
                var counters = new List<PerformanceCounter>();
                foreach (var cat in cats)
                {
                    foreach (var pc in cat.GetCounters())
                    {
                        if (pc.CounterType is PerformanceCounterType.NumberOfItems32 or PerformanceCounterType.NumberOfItems64)
                            counters.Add(new PerformanceCounter(cat.CategoryName, pc.CounterName, "", machine));
                        pc.Dispose();
                    }
                }
                _counters = counters;
                Status = counters.Count > 0 ? PerfmonStatus.Ready : PerfmonStatus.Unavailable;
                StatusDetail = counters.Count > 0
                    ? $"{counters.Count} compteurs suivis sur {machine} ({cats.Count} catégories)"
                    : $"aucune catégorie MSAS* pertinente trouvée sur {machine}";
            }
            catch (Exception ex)
            {
                Status = PerfmonStatus.Unavailable;
                StatusDetail = $"[{ex.GetType().Name}] {ex.GetBaseException().Message} — " +
                    "vérifier l'appartenance au groupe 'Performance Monitor Users' (SID S-1-5-32-558) " +
                    "et le service Remote Registry sur le serveur SSAS.";
            }
        }
    }

    /// <summary>Is the label after the first ':' (trimmed, case-insensitive) a wanted category?</summary>
    internal static bool IsWantedCategory(string categoryName)
    {
        int sep = categoryName.IndexOf(':');
        if (sep < 0) return false;
        string label = categoryName[(sep + 1)..].Trim();
        return WantedCategories.Contains(label, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot of the raw values (to take BEFORE the query).</summary>
    public Dictionary<string, long> Snapshot()
    {
        lock (_lock)
        {
            if (Status != PerfmonStatus.Ready) return [];
            var snap = new Dictionary<string, long>(_counters.Count);
            foreach (var pc in _counters)
            {
                try { snap[$"{pc.CategoryName}|{pc.CounterName}"] = pc.RawValue; }
                catch { /* counter gone: ignored, the delta will be missing */ }
            }
            return snap;
        }
    }

    /// <summary>Non-zero deltas since a snapshot (to call AFTER the query).</summary>
    public IReadOnlyList<CounterDelta> DeltasSince(Dictionary<string, long> before)
    {
        lock (_lock)
        {
            if (Status != PerfmonStatus.Ready || before.Count == 0) return [];
            var deltas = new List<CounterDelta>();
            foreach (var pc in _counters)
            {
                string key = $"{pc.CategoryName}|{pc.CounterName}";
                if (!before.TryGetValue(key, out long prev)) continue;
                try
                {
                    long delta = pc.RawValue - prev;
                    if (delta != 0)
                        deltas.Add(new CounterDelta(CategoryLabel(pc.CategoryName), pc.CounterName, delta));
                }
                catch { /* counter disappeared along the way */ }
            }
            return deltas.OrderBy(d => d.Category).ThenBy(d => d.Counter).ToList();
        }
    }

    /// <summary>"MSAS16 : requête du moteur de stockage" → "requête du moteur de stockage".</summary>
    internal static string CategoryLabel(string categoryName)
    {
        int sep = categoryName.IndexOf(':');
        return sep < 0 ? categoryName : categoryName[(sep + 1)..].Trim();
    }

    private void DisposeCounters()
    {
        foreach (var pc in _counters) pc.Dispose();
        _counters = [];
        Status = PerfmonStatus.NotInitialized;
        StatusDetail = null;
    }

    public void Dispose()
    {
        lock (_lock) DisposeCounters();
    }
}

public enum PerfmonStatus
{
    NotInitialized,
    Ready,
    Unavailable,
}
