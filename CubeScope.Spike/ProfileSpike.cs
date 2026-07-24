using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.AdomdClient;
using AmoTrace = Microsoft.AnalysisServices.Trace; // lève l'ambiguïté avec System.Diagnostics.Trace

/// <summary>
/// Spike go/no-go du profiler (post-MVP) : valide qu'une trace SSAS créée via AMO .NET Core
/// pousse bien les événements en live (Trace.OnEvent), qu'on a les droits admin pour la
/// créer, et qu'on en tire le découpage Formula Engine / Storage Engine par requête.
/// LECTURE SEULE : la trace n'écrit rien dans le cube ; la requête MDX est un simple SELECT.
/// </summary>
internal static class ProfileSpike
{
    private sealed record Captured(string EventClass, int Subclass, long Duration, long Cpu,
        string? Text, string? Session, string? Spid);

    // Uniquement les événements « complétés » (ils portent une Duration) : les événements
    // « Begin » refusent les colonnes de fin (Duration/CpuTime/EndTime) → rejet serveur au Update.
    private static readonly TraceEventClass[] WantedEvents =
    [
        TraceEventClass.QueryEnd,
        TraceEventClass.QuerySubcube, TraceEventClass.QuerySubcubeVerbose,
        TraceEventClass.GetDataFromAggregation, TraceEventClass.GetDataFromCache,
        TraceEventClass.CalculateNonEmptyEnd,
        TraceEventClass.SerializeResultsEnd,
        TraceEventClass.ExecuteMdxScriptEnd,
    ];

    // Ensemble MINIMAL supporté par tous les événements complétés visés (chaque EventClass
    // a sa propre liste blanche de colonnes, validée par le serveur au Update).
    private static readonly TraceColumn[] WantedColumns =
    [
        TraceColumn.EventClass, TraceColumn.EventSubclass,
        TraceColumn.Duration, TraceColumn.TextData, TraceColumn.SessionID,
    ];

    public static int Run(string server, string catalog)
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine($" CubeScope.Spike — Profiler (trace SSAS) — {server} / {catalog}");
        Console.WriteLine("==============================================================");

        const string traceName = "CubeScope_Profiler_Spike";
        var captured = new ConcurrentQueue<Captured>();
        Server? amo = null;
        AmoTrace? trace = null;

        // --- 1. Connexion AMO + création de la trace (droits admin requis) ---
        try
        {
            amo = new Server();
            amo.Connect($"Data Source={server};Integrated Security=SSPI;");
            Console.WriteLine($"AMO connecté (ServerMode={amo.ServerMode}, Version={amo.Version}).");

            var stale = amo.Traces.FindByName(traceName);
            stale?.Drop();

            trace = amo.Traces.Add(traceName);
            foreach (var ev in WantedEvents)
            {
                var te = new TraceEvent(ev);
                foreach (var col in WantedColumns) te.Columns.Add(col);
                trace.Events.Add(te);
            }

            // Chaque EventClass a sa propre liste blanche de colonnes, validée serveur au Update.
            // Le message d'erreur donne (event ID, column ID) = valeurs des enums AMO → on retire
            // la colonne fautive de l'événement concerné et on réessaie. Boucle bornée.
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
                    if (te.Columns.Count == 0) trace.Events.Remove(te); // événement vidé : on l'abandonne
                    pruned++;
                }
            }
            Console.WriteLine($"Trace créée : {trace.Events.Count} événements suivis " +
                $"({pruned} colonne(s) invalide(s) élaguée(s) automatiquement).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ÉCHEC création de trace : [{ex.GetType().Name}] {ex.GetBaseException().Message}");
            Console.WriteLine("→ Cause probable : droits insuffisants (créer une trace exige le rôle " +
                "administrateur de l'instance SSAS), ou AMO .NET Core ne supporte pas cette opération.");
            amo?.Disconnect();
            PrintVerdict(false, "création de trace impossible");
            return 1;
        }

        // --- 2. Souscription live + exécution d'une requête tracée ---
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
        trace.Stopped += (_, _) => Console.WriteLine("(trace arrêtée côté serveur)");

        string? ourSession = null;
        long queryMs = 0;
        try
        {
            trace.Start();
            Console.WriteLine("Trace démarrée (souscription live). Exécution de la requête test…");

            using var conn = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
            conn.Open();
            conn.ChangeDatabase(catalog);
            ourSession = conn.SessionID;
            Console.WriteLine($"SessionID ADOMD : {ourSession}");

            // Requête volontairement non triviale (crossjoin modéré) pour générer du Storage Engine.
            // Noms du cube/mesure/hiérarchies via variables d'env (sinon placeholders génériques) :
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
                var cs = cmd.ExecuteCellSet(); // CellSet n'est pas IDisposable
                _ = cs.Cells.Count;
            }
            sw.Stop();
            queryMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"Requête exécutée en {queryMs} ms. Attente du flush des événements…");

            // Les événements de trace arrivent en asynchrone : laisser le temps au push XMLA.
            for (int i = 0; i < 20 && eventCount == 0; i++) Thread.Sleep(150);
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ÉCHEC exécution/souscription : [{ex.GetType().Name}] {ex.GetBaseException().Message}");
        }
        finally
        {
            try { trace.Stop(); } catch { /* ignore */ }
            try { trace.Drop(); } catch { /* ignore */ }
            amo.Disconnect();
        }

        // --- 3. Analyse ---
        var all = captured.ToList();
        Console.WriteLine($"\nÉvénements capturés : {all.Count} (toutes sessions).");
        if (all.Count == 0)
        {
            Console.WriteLine("Aucun événement reçu → soit AMO .NET Core ne pousse pas les événements " +
                "en live (Trace.OnEvent inopérant), soit la requête n'a produit aucun événement suivi.");
            PrintVerdict(false, "aucun événement live reçu");
            return 1;
        }

        var mine = all.Where(c => string.Equals(c.Session, ourSession, StringComparison.OrdinalIgnoreCase)).ToList();
        var scope = mine.Count > 0 ? mine : all; // à défaut de match session, on montre tout
        Console.WriteLine($"Événements de NOTRE session : {mine.Count}" +
            (mine.Count == 0 ? " (pas de match SessionID — affichage global)" : ""));

        Console.WriteLine("\n--- Détail (par classe d'événement) ---");
        foreach (var g in scope.GroupBy(c => c.EventClass).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-28} : {g.Count(),3} évt, durée cumulée {g.Sum(c => c.Duration),6} ms");

        long total = scope.Where(c => c.EventClass == "QueryEnd").Sum(c => c.Duration);
        if (total == 0) total = queryMs;
        long se = scope.Where(c => c.EventClass is "QuerySubcube").Sum(c => c.Duration);
        int subcubes = scope.Count(c => c.EventClass is "QuerySubcube" or "QuerySubcubeVerbose");
        int cacheHits = scope.Count(c => c.EventClass == "GetDataFromCache");
        int aggHits = scope.Count(c => c.EventClass == "GetDataFromAggregation");
        long fe = Math.Max(0, total - se);

        Console.WriteLine("\n--- Découpage type profiler ---");
        Console.WriteLine($"  Durée totale requête   : {total} ms");
        Console.WriteLine($"  Storage Engine (SE)    : {se} ms  ({subcubes} Query Subcube)");
        Console.WriteLine($"  Formula Engine (FE)    : {fe} ms  (total - SE)");
        Console.WriteLine($"  Hits cache             : {cacheHits}");
        Console.WriteLine($"  Hits agrégation        : {aggHits}");

        PrintVerdict(true, $"{all.Count} événements live, découpage FE/SE obtenu");
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
