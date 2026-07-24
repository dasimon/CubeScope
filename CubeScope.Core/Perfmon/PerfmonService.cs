using System.Diagnostics;
using CubeScope.Core.Models;

namespace CubeScope.Core.Perfmon;

/// <summary>
/// Deltas de compteurs perfmon autour d'une requête (décision actée : compteurs globaux
/// au serveur, assumé pour le MVP). Acquis du spike 2026-07-17 :
/// - catégories LOCALISÉES sur OS français : "MSAS16 : MDX" (séparateur " : " avec espaces)
///   → matcher le libellé après le premier ':' (trim) en FR ET EN ;
/// - ne jamais filtrer les compteurs par nom ("/sec" devient "/s") mais par CounterType
///   (NumberOfItems32/64 = cumulatifs à delta) ;
/// - dégradable : échec de droits (Win32Exception "Accès refusé") → service Unavailable,
///   l'application continue sans stats.
/// Limite MVP assumée : instance par défaut (préfixe MSAS*) uniquement — pour une instance
/// nommée jointe par port (hôte:port), le mapping port→nom d'instance n'est pas
/// découvrable, on resterait sur les compteurs de l'instance par défaut.
/// </summary>
public sealed class PerfmonService : IDisposable
{
    // Catégories utiles par requête (libellés FR et EN, après le préfixe "MSASxx :")
    private static readonly string[] WantedCategories =
        ["mdx", "cache", "requête du moteur de stockage", "storage engine query"];

    private readonly Lock _lock = new();
    private List<PerformanceCounter> _counters = [];
    private string? _machine;

    public PerfmonStatus Status { get; private set; } = PerfmonStatus.NotInitialized;
    public string? StatusDetail { get; private set; }

    /// <summary>Initialise (ou réinitialise) les compteurs pour un serveur SSAS. Jamais bloquant pour l'appelant.</summary>
    public void Initialize(string dataSource)
    {
        // "hôte:port" → machine "hôte" (le port ne sert qu'à ADOMD)
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

    /// <summary>Le libellé après le premier ':' (trim, insensible casse) est-il une catégorie voulue ?</summary>
    internal static bool IsWantedCategory(string categoryName)
    {
        int sep = categoryName.IndexOf(':');
        if (sep < 0) return false;
        string label = categoryName[(sep + 1)..].Trim();
        return WantedCategories.Contains(label, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot des valeurs brutes (à prendre AVANT la requête).</summary>
    public Dictionary<string, long> Snapshot()
    {
        lock (_lock)
        {
            if (Status != PerfmonStatus.Ready) return [];
            var snap = new Dictionary<string, long>(_counters.Count);
            foreach (var pc in _counters)
            {
                try { snap[$"{pc.CategoryName}|{pc.CounterName}"] = pc.RawValue; }
                catch { /* compteur disparu : ignoré, le delta sera absent */ }
            }
            return snap;
        }
    }

    /// <summary>Deltas non nuls depuis un snapshot (à appeler APRÈS la requête).</summary>
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
                catch { /* compteur disparu en cours de route */ }
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
