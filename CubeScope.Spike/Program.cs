// CubeScope — Phase 0 : spike go/no-go contre un serveur SSAS Multidimensional réel.
// Valide : (1) connexion AdomdClient NuGet, (2) DMV $SYSTEM.MDSCHEMA_*, (3) schema rowset
// typé (GetSchemaDataSet), (4) ClearCache XMLA + ExecuteCellSet froid/chaud, (5) deltas perfmon.
// Usage : CubeScope.Spike [serveur]   (défaut : var d'env CUBESCOPE_SPIKE_SERVER, sinon localhost.
// Cibler un serveur de dev/recette, jamais la prod — le spike vide le cache.)

using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.AnalysisServices.AdomdClient;

Console.OutputEncoding = Encoding.UTF8;

string server = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("CUBESCOPE_SPIKE_SERVER") ?? "localhost";
var verdicts = new List<(string Etape, bool Ok, string Detail)>();

// Mode lecture seule : identifier l'instance (nom, version, catalogues) sans toucher au cache
if (args.Contains("--discover"))
{
    using var c = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
    c.Open();
    Console.WriteLine($"[{server}] ServerVersion : {c.ServerVersion}");
    var props = c.GetSchemaDataSet("DISCOVER_PROPERTIES", null).Tables[0];
    foreach (DataRow r in props.Rows)
        if (r["PropertyName"] is "ServerName" or "DBMSVer" or "ServerMode")
            Console.WriteLine($"[{server}] {r["PropertyName"]} = {r["Value"]}");
    var cats2 = c.GetSchemaDataSet("DBSCHEMA_CATALOGS", null).Tables[0];
    Console.WriteLine($"[{server}] Catalogues : " +
        string.Join(", ", cats2.Rows.Cast<DataRow>().Select(r => r["CATALOG_NAME"])));
    foreach (DataRow rc in cats2.Rows)
    {
        string cat = (string)rc["CATALOG_NAME"];
        c.ChangeDatabase(cat);
        var cubes3 = Dmv(c, "SELECT CUBE_NAME, CUBE_SOURCE, LAST_DATA_UPDATE FROM $SYSTEM.MDSCHEMA_CUBES");
        Console.WriteLine($"  [{cat}] MDSCHEMA_CUBES : {cubes3.Rows.Count} lignes" +
            (cubes3.Rows.Count == 0 ? " (base probablement NON PROCESSÉE)" : ""));
        foreach (DataRow r in cubes3.Rows.Cast<DataRow>().Where(r => Convert.ToInt32(r["CUBE_SOURCE"]) == 1))
            Console.WriteLine($"    cube [{r["CUBE_NAME"]}], LAST_DATA_UPDATE = {r["LAST_DATA_UPDATE"]}");
    }
    return 0;
}

// Spike profiler (post-MVP) : trace SSAS, découpage Formula Engine / Storage Engine par requête.
// Usage : CubeScope.Spike <serveur-SSAS> --profile [--catalog <catalogue>]
if (args.Contains("--profile"))
{
    int iCat = Array.IndexOf(args, "--catalog");
    string profileCatalog = iCat >= 0 && iCat + 1 < args.Length ? args[iCat + 1]
        : Environment.GetEnvironmentVariable("CUBESCOPE_TEST_CATALOG") ?? "SsasDb";
    return ProfileSpike.Run(server, profileCatalog);
}

Console.WriteLine("==============================================================");
Console.WriteLine($" CubeScope.Spike — Phase 0 — serveur cible : {server}");
Console.WriteLine("==============================================================");

AdomdConnection? conn = null;
string catalog = "", cube = "", measure = "";

// ---------------------------------------------------------------- Étape 1 : connexion
try
{
    Console.WriteLine("\n--- Étape 1 : connexion AdomdClient (Integrated Security) ---");
    conn = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
    var sw = Stopwatch.StartNew();
    conn.Open();
    sw.Stop();
    Console.WriteLine($"Connecté en {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  ServerVersion : {conn.ServerVersion}");
    Console.WriteLine($"  SessionID     : {conn.SessionID}");
    verdicts.Add(("1. Connexion", true, $"ServerVersion {conn.ServerVersion}"));
}
catch (Exception ex)
{
    Console.WriteLine($"ÉCHEC connexion : {ex.GetBaseException().Message}");
    verdicts.Add(("1. Connexion", false, ex.GetBaseException().Message));
    PrintSummary();
    return 1;
}

// ---------------------------------------------------------------- Étape 2 : DMV MDSCHEMA_*
try
{
    Console.WriteLine("\n--- Étape 2 : DMV $SYSTEM.MDSCHEMA_* ---");
    // Catalogues via schema rowset (fonctionne sans catalogue courant)
    var catalogs = conn.GetSchemaDataSet("DBSCHEMA_CATALOGS", null).Tables[0];
    Console.WriteLine($"Catalogues ({catalogs.Rows.Count}) : " +
        string.Join(", ", catalogs.Rows.Cast<DataRow>().Select(r => r["CATALOG_NAME"])));

    // Catalogue forcé par --catalog <nom>, sinon premier catalogue avec un vrai cube (CUBE_SOURCE = 1)
    int iCat = Array.IndexOf(args, "--catalog");
    string? forced = iCat >= 0 && iCat + 1 < args.Length ? args[iCat + 1] : null;
    foreach (DataRow r in catalogs.Rows)
    {
        if (forced != null && !string.Equals((string)r["CATALOG_NAME"], forced, StringComparison.OrdinalIgnoreCase)) continue;
        conn.ChangeDatabase((string)r["CATALOG_NAME"]);
        var t = Dmv(conn, "SELECT CUBE_NAME FROM $SYSTEM.MDSCHEMA_CUBES WHERE CUBE_SOURCE = 1");
        if (t.Rows.Count > 0)
        {
            catalog = (string)r["CATALOG_NAME"];
            cube = (string)t.Rows[0]["CUBE_NAME"];
            break;
        }
    }
    if (catalog == "") throw new InvalidOperationException("Aucun catalogue avec un cube (CUBE_SOURCE=1) trouvé.");
    Console.WriteLine($"Catalogue retenu : [{catalog}], cube : [{cube}]");

    var cubes = Dmv(conn, "SELECT CUBE_NAME, CUBE_SOURCE FROM $SYSTEM.MDSCHEMA_CUBES");
    int reels = cubes.Rows.Cast<DataRow>().Count(r => Convert.ToInt32(r["CUBE_SOURCE"]) == 1);
    Console.WriteLine($"MDSCHEMA_CUBES      : {cubes.Rows.Count} lignes ({reels} cubes réels, {cubes.Rows.Count - reels} dimensions exposées en $)");

    var measures = Dmv(conn, $"SELECT MEASURE_NAME, MEASURE_UNIQUE_NAME FROM $SYSTEM.MDSCHEMA_MEASURES WHERE CUBE_NAME = '{cube.Replace("'", "''")}'");
    measure = (string)measures.Rows[0]["MEASURE_UNIQUE_NAME"];
    Console.WriteLine($"MDSCHEMA_MEASURES   : {measures.Rows.Count} mesures sur [{cube}] — ex. : " +
        string.Join(", ", measures.Rows.Cast<DataRow>().Take(5).Select(r => r["MEASURE_NAME"])));

    var dims = Dmv(conn, $"SELECT DIMENSION_NAME FROM $SYSTEM.MDSCHEMA_DIMENSIONS WHERE CUBE_NAME = '{cube.Replace("'", "''")}'");
    Console.WriteLine($"MDSCHEMA_DIMENSIONS : {dims.Rows.Count} dimensions sur [{cube}] — ex. : " +
        string.Join(", ", dims.Rows.Cast<DataRow>().Take(5).Select(r => r["DIMENSION_NAME"])));

    verdicts.Add(("2. DMV MDSCHEMA_*", true, $"{catalogs.Rows.Count} catalogues, cube test [{cube}]"));
}
catch (Exception ex)
{
    Console.WriteLine($"ÉCHEC DMV : {ex.GetBaseException().Message}");
    verdicts.Add(("2. DMV MDSCHEMA_*", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Étape 3 : schema rowset typé
try
{
    Console.WriteLine("\n--- Étape 3 : schema rowset typé (GetSchemaDataSet) ---");
    var restr = new AdomdRestrictionCollection
    {
        { "CATALOG_NAME", catalog },
        { "CUBE_NAME", cube }
    };
    var hier = conn.GetSchemaDataSet("MDSCHEMA_HIERARCHIES", restr).Tables[0];
    Console.WriteLine($"MDSCHEMA_HIERARCHIES restreint à [{catalog}].[{cube}] : {hier.Rows.Count} hiérarchies");
    Console.WriteLine("Typage des colonnes (extrait) : " + string.Join(", ",
        hier.Columns.Cast<DataColumn>().Take(6).Select(c => $"{c.ColumnName}:{c.DataType.Name}")));
    verdicts.Add(("3. Schema rowset typé", true, $"{hier.Rows.Count} hiérarchies, colonnes typées .NET"));
}
catch (Exception ex)
{
    Console.WriteLine($"ÉCHEC schema rowset : {ex.GetBaseException().Message}");
    verdicts.Add(("3. Schema rowset typé", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Étape 5a : perfmon (découverte + snapshot avant)
List<PerformanceCounter> counters = new();
Dictionary<string, long> before = new();
bool perfmonOk = false;
try
{
    Console.WriteLine("\n--- Étape 5a : perfmon distant — découverte des catégories ---");
    var cats = PerformanceCounterCategory.GetCategories(server)
        .Where(c => c.CategoryName.StartsWith("MSAS", StringComparison.OrdinalIgnoreCase)
                 || c.CategoryName.StartsWith("MSOLAP$", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.CategoryName)
        .ToList();
    Console.WriteLine($"Catégories MSAS*/MSOLAP$* trouvées ({cats.Count}) :");
    foreach (var c in cats) Console.WriteLine($"  {c.CategoryName}");

    // Sous-ensemble utile au futur panneau de stats.
    // Piège : sur un OS en français les noms de catégories sont LOCALISÉS et le
    // séparateur devient " : " avec espaces ("MSAS16 : MDX", "MSAS16 : mémoire") →
    // on compare le libellé après le premier ':' (trim), en français ET en anglais.
    string[] wanted = { "mdx", "cache", "mémoire", "memory", "connexion", "connection",
                        "requête du moteur de stockage", "storage engine query" };
    // Instance par défaut = préfixe MSAS<ver> ; instance nommée = MSOLAP$<nom>.
    // Le spike vise l'instance par défaut → MSAS uniquement (sinon on suivrait aussi MSOLAP$<instance>).
    foreach (var c in cats.Where(c => c.CategoryName.StartsWith("MSAS", StringComparison.OrdinalIgnoreCase)))
    {
        int sep = c.CategoryName.IndexOf(':');
        if (sep < 0 || !wanted.Contains(c.CategoryName[(sep + 1)..].Trim().ToLowerInvariant())) continue;
        foreach (var pc in c.GetCounters())
        {
            // Compteurs cumulatifs uniquement, filtrés par TYPE (les noms sont localisés,
            // "/sec" devient "/s" en français — un filtre par nom n'est pas fiable)
            if (pc.CounterType is PerformanceCounterType.NumberOfItems32 or PerformanceCounterType.NumberOfItems64)
                counters.Add(new PerformanceCounter(c.CategoryName, pc.CounterName, "", server));
            pc.Dispose();
        }
    }
    foreach (var pc in counters) before[$"{pc.CategoryName}|{pc.CounterName}"] = pc.RawValue;
    Console.WriteLine($"Snapshot 'avant' pris sur {counters.Count} compteurs cumulatifs (catégories MDX / Storage Engine Query / Cache / Memory / Connection).");
    perfmonOk = true;
}
catch (Exception ex)
{
    Console.WriteLine($"PERFMON KO (non bloquant) : [{ex.GetType().Name}] {ex.GetBaseException().Message}");
    verdicts.Add(("5. Perfmon distant", false, $"[{ex.GetType().Name}] {ex.GetBaseException().Message}"));
}

// ---------------------------------------------------------------- Étape 4 : ClearCache + froid/chaud
try
{
    Console.WriteLine("\n--- Étape 4 : ClearCache XMLA puis ExecuteCellSet froid/chaud ---");
    string xmla = $"""
        <ClearCache xmlns="http://schemas.microsoft.com/analysisservices/2003/engine">
          <Object>
            <DatabaseID>{System.Security.SecurityElement.Escape(catalog)}</DatabaseID>
          </Object>
        </ClearCache>
        """;
    using (var cmd = new AdomdCommand(xmla, conn))
    {
        try
        {
            cmd.ExecuteNonQuery();
            Console.WriteLine($"ClearCache OK avec DatabaseID = nom du catalogue ('{catalog}')");
        }
        catch (Exception exId)
        {
            // Piège CLAUDE.md : le DatabaseID peut différer du nom (base renommée) → à résoudre via AMO
            Console.WriteLine($"ClearCache avec DatabaseID = nom a échoué : {exId.GetBaseException().Message}");
            Console.WriteLine("→ résolution du vrai DatabaseID via AMO nécessaire (hors périmètre spike).");
            throw;
        }
    }

    string mdx = $"SELECT {{ {measure} }} ON COLUMNS FROM [{cube.Replace("]", "]]")}]";
    Console.WriteLine($"MDX test : {mdx}");

    var swCold = Stopwatch.StartNew();
    CellSet csCold;
    using (var cmd = new AdomdCommand(mdx, conn)) csCold = cmd.ExecuteCellSet();
    swCold.Stop();
    // Piège CLAUDE.md : une requête mono-axe n'a pas d'Axes[1] → toujours tester Axes.Count
    Console.WriteLine($"FROID : {swCold.ElapsedMilliseconds} ms — Axes.Count = {csCold.Axes.Count}, " +
        $"{csCold.Cells.Count} cellule(s), valeur[0] = {csCold.Cells[0].FormattedValue}");

    var swWarm = Stopwatch.StartNew();
    CellSet csWarm;
    using (var cmd = new AdomdCommand(mdx, conn)) csWarm = cmd.ExecuteCellSet();
    swWarm.Stop();
    Console.WriteLine($"CHAUD : {swWarm.ElapsedMilliseconds} ms — valeur[0] = {csWarm.Cells[0].FormattedValue}");
    Console.WriteLine($"Ratio froid/chaud : {(swWarm.ElapsedMilliseconds > 0 ? (double)swCold.ElapsedMilliseconds / swWarm.ElapsedMilliseconds : double.NaN):F1}x");

    verdicts.Add(("4. ClearCache + froid/chaud", true,
        $"froid {swCold.ElapsedMilliseconds} ms / chaud {swWarm.ElapsedMilliseconds} ms"));
}
catch (Exception ex)
{
    Console.WriteLine($"ÉCHEC ClearCache/CellSet : {ex.GetBaseException().Message}");
    verdicts.Add(("4. ClearCache + froid/chaud", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Étape 5b : deltas perfmon
if (perfmonOk)
{
    try
    {
        Console.WriteLine("\n--- Étape 5b : deltas perfmon (avant → après les 2 requêtes) ---");
        int moved = 0;
        foreach (var pc in counters)
        {
            long delta = pc.RawValue - before[$"{pc.CategoryName}|{pc.CounterName}"];
            if (delta != 0)
            {
                Console.WriteLine($"  {pc.CategoryName} \\ {pc.CounterName} : {delta:+#;-#;0}");
                moved++;
            }
        }
        Console.WriteLine($"{moved} compteur(s) ayant bougé sur {counters.Count} suivis.");
        verdicts.Add(("5. Perfmon distant", moved > 0,
            moved > 0 ? $"{moved} deltas non nuls / {counters.Count} compteurs" : "aucun delta — compteurs globaux pollués ou mauvais choix de compteurs"));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PERFMON deltas KO (non bloquant) : [{ex.GetType().Name}] {ex.GetBaseException().Message}");
        verdicts.Add(("5. Perfmon distant", false, $"[{ex.GetType().Name}] {ex.GetBaseException().Message}"));
    }
}
foreach (var pc in counters) pc.Dispose();
conn.Dispose();

PrintSummary();
return verdicts.Where(v => !v.Etape.StartsWith("5")).All(v => v.Ok) ? 0 : 1;

void PrintSummary()
{
    Console.WriteLine("\n==============================================================");
    Console.WriteLine(" BILAN GO/NO-GO (critère : étapes 1-4 OK, perfmon dégradable)");
    Console.WriteLine("==============================================================");
    foreach (var v in verdicts)
        Console.WriteLine($"  [{(v.Ok ? "OK " : "KO ")}] {v.Etape} — {v.Detail}");
    bool go = verdicts.Count(v => v.Ok && !v.Etape.StartsWith("5")) >= 4;
    Console.WriteLine($"\n  VERDICT : {(go ? "GO" : "NO-GO")}");
}

static DataTable Dmv(AdomdConnection c, string query)
{
    using var cmd = new AdomdCommand(query, c);
    using var rdr = cmd.ExecuteReader();
    // Piège : les rowsets ADOMD déclarent des contraintes d'unicité que leurs propres
    // données violent → DataTable.Load lève "Failed to enable constraints" si la table
    // n'est pas dans un DataSet avec EnforceConstraints = false.
    var ds = new DataSet { EnforceConstraints = false };
    var t = new DataTable();
    ds.Tables.Add(t);
    t.Load(rdr);
    return t;
}
