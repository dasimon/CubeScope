// CubeScope — Phase 0: go/no-go spike against a real SSAS Multidimensional server.
// Validates: (1) NuGet AdomdClient connection, (2) $SYSTEM.MDSCHEMA_* DMVs, (3) typed schema
// rowset (GetSchemaDataSet), (4) XMLA ClearCache + cold/warm ExecuteCellSet, (5) perfmon deltas.
//
// Usage: CubeScope.Spike [server] [--discover | --clear-cache | --profile] [--catalog <catalog>]
//   server         SSAS data source (hostname, or host:port for a named instance on a fixed port).
//                  Default: CUBESCOPE_SPIKE_SERVER env var, otherwise localhost.
//   (no flag)      same as --discover.
//   --discover     READ-ONLY (default): server name/version/mode, catalogs, cubes and their
//                  LAST_DATA_UPDATE. Never touches the cache.
//   --clear-cache  full go/no-go run (steps 1-5 above). DESTRUCTIVE for performance: it issues an
//                  XMLA ClearCache on the selected catalog (the first one with a cube, or --catalog).
//                  Refused (exit code 2) unless the server is listed in the CUBESCOPE_SPIKE_DEV_SERVERS
//                  env var (';'-separated, case-insensitive exact match on the server argument).
//                  Empty or missing list = nothing allowed (fail-closed).
//   --profile      profiler spike (ProfileSpike.cs): creates a server-side SSAS trace (admin rights
//                  required), runs one query, then stops and drops the trace. No cache clear.
//   --catalog <c>  catalog to use for --clear-cache / --profile.

using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.AnalysisServices.AdomdClient;

Console.OutputEncoding = Encoding.UTF8;

int iCatalogArg = Array.IndexOf(args, "--catalog");
string? catalogArg = iCatalogArg >= 0 && iCatalogArg + 1 < args.Length ? args[iCatalogArg + 1] : null;
// Server = first positional argument (not a flag, not the value of --catalog)
string server = args.Where((a, i) => !a.StartsWith("--") && !(iCatalogArg >= 0 && i == iCatalogArg + 1))
                    .FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CUBESCOPE_SPIKE_SERVER") ?? "localhost";
var verdicts = new List<(string Etape, bool Ok, string Detail)>();

string[] modes = args.Where(a => a is "--discover" or "--clear-cache" or "--profile").Distinct().ToArray();
if (modes.Length > 1)
{
    Console.Error.WriteLine($"Choose a single mode, got: {string.Join(" ", modes)}");
    return 2;
}
string mode = modes.Length == 1 ? modes[0] : "--discover";

// Read-only mode (default): identify the instance (name, version, catalogs) without touching the cache
if (mode == "--discover")
{
    using var c = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
    c.Open();
    Console.WriteLine($"[{server}] ServerVersion : {c.ServerVersion}");
    var props = c.GetSchemaDataSet("DISCOVER_PROPERTIES", null).Tables[0];
    foreach (DataRow r in props.Rows)
        if (r["PropertyName"] is "ServerName" or "DBMSVer" or "ServerMode")
            Console.WriteLine($"[{server}] {r["PropertyName"]} = {r["Value"]}");
    var cats2 = c.GetSchemaDataSet("DBSCHEMA_CATALOGS", null).Tables[0];
    Console.WriteLine($"[{server}] Catalogs: " +
        string.Join(", ", cats2.Rows.Cast<DataRow>().Select(r => r["CATALOG_NAME"])));
    foreach (DataRow rc in cats2.Rows)
    {
        string cat = (string)rc["CATALOG_NAME"];
        c.ChangeDatabase(cat);
        var cubes3 = Dmv(c, "SELECT CUBE_NAME, CUBE_SOURCE, LAST_DATA_UPDATE FROM $SYSTEM.MDSCHEMA_CUBES");
        Console.WriteLine($"  [{cat}] MDSCHEMA_CUBES : {cubes3.Rows.Count} rows" +
            (cubes3.Rows.Count == 0 ? " (database probably NOT PROCESSED)" : ""));
        foreach (DataRow r in cubes3.Rows.Cast<DataRow>().Where(r => Convert.ToInt32(r["CUBE_SOURCE"]) == 1))
            Console.WriteLine($"    cube [{r["CUBE_NAME"]}], LAST_DATA_UPDATE = {r["LAST_DATA_UPDATE"]}");
    }
    return 0;
}

// Profiler spike (post-MVP): SSAS trace, Formula Engine / Storage Engine breakdown per query.
// Usage: CubeScope.Spike <SSAS-server> --profile [--catalog <catalog>]
if (mode == "--profile")
{
    string profileCatalog = catalogArg
        ?? Environment.GetEnvironmentVariable("CUBESCOPE_TEST_CATALOG") ?? "SsasDb";
    return ProfileSpike.Run(server, profileCatalog);
}

// --clear-cache: explicit allowlist of dev servers, fail-closed. The catalog name cannot tell
// dev from prod (both may be called the same), and a substring rule would be too permissive.
string[] devServers = (Environment.GetEnvironmentVariable("CUBESCOPE_SPIKE_DEV_SERVERS") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (!devServers.Contains(server, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refused: --clear-cache clears the SSAS cache, and '{server}' is not listed in " +
        "CUBESCOPE_SPIKE_DEV_SERVERS" + (devServers.Length == 0 ? " (variable empty or missing)." : $" ({string.Join(";", devServers)})."));
    Console.Error.WriteLine("Add the exact server name (as passed on the command line) to that ';'-separated list " +
        "to allow it — dev servers only, never a server shared with production. Use --discover for a read-only run.");
    return 2;
}

Console.WriteLine("==============================================================");
Console.WriteLine($" CubeScope.Spike — Phase 0 — target server: {server}");
Console.WriteLine("==============================================================");

AdomdConnection? conn = null;
string catalog = "", cube = "", measure = "";

// ---------------------------------------------------------------- Step 1: connection
try
{
    Console.WriteLine("\n--- Step 1: AdomdClient connection (Integrated Security) ---");
    conn = new AdomdConnection($"Data Source={server};Integrated Security=SSPI;");
    var sw = Stopwatch.StartNew();
    conn.Open();
    sw.Stop();
    Console.WriteLine($"Connected in {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  ServerVersion : {conn.ServerVersion}");
    Console.WriteLine($"  SessionID     : {conn.SessionID}");
    verdicts.Add(("1. Connection", true, $"ServerVersion {conn.ServerVersion}"));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED connection: {ex.GetBaseException().Message}");
    verdicts.Add(("1. Connection", false, ex.GetBaseException().Message));
    PrintSummary();
    return 1;
}

// ---------------------------------------------------------------- Step 2: MDSCHEMA_* DMVs
try
{
    Console.WriteLine("\n--- Step 2: DMV $SYSTEM.MDSCHEMA_* ---");
    // Catalogs via schema rowset (works without a current catalog)
    var catalogs = conn.GetSchemaDataSet("DBSCHEMA_CATALOGS", null).Tables[0];
    Console.WriteLine($"Catalogs ({catalogs.Rows.Count}): " +
        string.Join(", ", catalogs.Rows.Cast<DataRow>().Select(r => r["CATALOG_NAME"])));

    // Catalog forced by --catalog <name>, otherwise the first catalog with a real cube (CUBE_SOURCE = 1)
    string? forced = catalogArg;
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
    if (catalog == "") throw new InvalidOperationException("No catalog with a cube (CUBE_SOURCE=1) found.");
    Console.WriteLine($"Selected catalog: [{catalog}], cube: [{cube}]");

    var cubes = Dmv(conn, "SELECT CUBE_NAME, CUBE_SOURCE FROM $SYSTEM.MDSCHEMA_CUBES");
    int reels = cubes.Rows.Cast<DataRow>().Count(r => Convert.ToInt32(r["CUBE_SOURCE"]) == 1);
    Console.WriteLine($"MDSCHEMA_CUBES      : {cubes.Rows.Count} rows ({reels} real cubes, {cubes.Rows.Count - reels} dimensions exposed as $)");

    var measures = Dmv(conn, $"SELECT MEASURE_NAME, MEASURE_UNIQUE_NAME FROM $SYSTEM.MDSCHEMA_MEASURES WHERE CUBE_NAME = '{cube.Replace("'", "''")}'");
    measure = (string)measures.Rows[0]["MEASURE_UNIQUE_NAME"];
    Console.WriteLine($"MDSCHEMA_MEASURES   : {measures.Rows.Count} measures on [{cube}] — e.g.: " +
        string.Join(", ", measures.Rows.Cast<DataRow>().Take(5).Select(r => r["MEASURE_NAME"])));

    var dims = Dmv(conn, $"SELECT DIMENSION_NAME FROM $SYSTEM.MDSCHEMA_DIMENSIONS WHERE CUBE_NAME = '{cube.Replace("'", "''")}'");
    Console.WriteLine($"MDSCHEMA_DIMENSIONS : {dims.Rows.Count} dimensions on [{cube}] — e.g.: " +
        string.Join(", ", dims.Rows.Cast<DataRow>().Take(5).Select(r => r["DIMENSION_NAME"])));

    verdicts.Add(("2. DMV MDSCHEMA_*", true, $"{catalogs.Rows.Count} catalogs, test cube [{cube}]"));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED DMV: {ex.GetBaseException().Message}");
    verdicts.Add(("2. DMV MDSCHEMA_*", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Step 3: typed schema rowset
try
{
    Console.WriteLine("\n--- Step 3: typed schema rowset (GetSchemaDataSet) ---");
    var restr = new AdomdRestrictionCollection
    {
        { "CATALOG_NAME", catalog },
        { "CUBE_NAME", cube }
    };
    var hier = conn.GetSchemaDataSet("MDSCHEMA_HIERARCHIES", restr).Tables[0];
    Console.WriteLine($"MDSCHEMA_HIERARCHIES restricted to [{catalog}].[{cube}]: {hier.Rows.Count} hierarchies");
    Console.WriteLine("Column types (excerpt): " + string.Join(", ",
        hier.Columns.Cast<DataColumn>().Take(6).Select(c => $"{c.ColumnName}:{c.DataType.Name}")));
    verdicts.Add(("3. Typed schema rowset", true, $"{hier.Rows.Count} hierarchies, .NET-typed columns"));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED schema rowset: {ex.GetBaseException().Message}");
    verdicts.Add(("3. Typed schema rowset", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Step 5a: perfmon (discovery + "before" snapshot)
List<PerformanceCounter> counters = new();
Dictionary<string, long> before = new();
bool perfmonOk = false;
try
{
    Console.WriteLine("\n--- Step 5a: remote perfmon — category discovery ---");
    var cats = PerformanceCounterCategory.GetCategories(server)
        .Where(c => c.CategoryName.StartsWith("MSAS", StringComparison.OrdinalIgnoreCase)
                 || c.CategoryName.StartsWith("MSOLAP$", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.CategoryName)
        .ToList();
    Console.WriteLine($"MSAS*/MSOLAP$* categories found ({cats.Count}):");
    foreach (var c in cats) Console.WriteLine($"  {c.CategoryName}");

    // Subset useful for the future stats panel.
    // Pitfall: on a French OS the category names are LOCALIZED and the
    // separator becomes " : " with spaces ("MSAS16 : MDX", "MSAS16 : mémoire") →
    // compare the label after the first ':' (trimmed), in French AND in English.
    string[] wanted = { "mdx", "cache", "mémoire", "memory", "connexion", "connection",
                        "requête du moteur de stockage", "storage engine query" };
    // Default instance = MSAS<ver> prefix; named instance = MSOLAP$<name>.
    // The spike targets the default instance → MSAS only (otherwise MSOLAP$<instance> would be tracked too).
    foreach (var c in cats.Where(c => c.CategoryName.StartsWith("MSAS", StringComparison.OrdinalIgnoreCase)))
    {
        int sep = c.CategoryName.IndexOf(':');
        if (sep < 0 || !wanted.Contains(c.CategoryName[(sep + 1)..].Trim().ToLowerInvariant())) continue;
        foreach (var pc in c.GetCounters())
        {
            // Cumulative counters only, filtered by TYPE (names are localized,
            // "/sec" becomes "/s" in French — filtering by name is not reliable)
            if (pc.CounterType is PerformanceCounterType.NumberOfItems32 or PerformanceCounterType.NumberOfItems64)
                counters.Add(new PerformanceCounter(c.CategoryName, pc.CounterName, "", server));
            pc.Dispose();
        }
    }
    foreach (var pc in counters) before[$"{pc.CategoryName}|{pc.CounterName}"] = pc.RawValue;
    Console.WriteLine($"'Before' snapshot taken on {counters.Count} cumulative counters (categories MDX / Storage Engine Query / Cache / Memory / Connection).");
    perfmonOk = true;
}
catch (Exception ex)
{
    Console.WriteLine($"PERFMON FAILED (non-blocking): [{ex.GetType().Name}] {ex.GetBaseException().Message}");
    verdicts.Add(("5. Remote perfmon", false, $"[{ex.GetType().Name}] {ex.GetBaseException().Message}"));
}

// ---------------------------------------------------------------- Step 4: ClearCache + cold/warm
try
{
    Console.WriteLine("\n--- Step 4: XMLA ClearCache then cold/warm ExecuteCellSet ---");
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
            Console.WriteLine($"ClearCache OK with DatabaseID = catalog name ('{catalog}')");
        }
        catch (Exception exId)
        {
            // CLAUDE.md pitfall: the DatabaseID can differ from the name (renamed database) → resolve via AMO
            Console.WriteLine($"ClearCache with DatabaseID = name failed: {exId.GetBaseException().Message}");
            Console.WriteLine("→ the real DatabaseID must be resolved via AMO (out of the spike's scope).");
            throw;
        }
    }

    string mdx = $"SELECT {{ {measure} }} ON COLUMNS FROM [{cube.Replace("]", "]]")}]";
    Console.WriteLine($"Test MDX: {mdx}");

    var swCold = Stopwatch.StartNew();
    CellSet csCold;
    using (var cmd = new AdomdCommand(mdx, conn)) csCold = cmd.ExecuteCellSet();
    swCold.Stop();
    // CLAUDE.md pitfall: a single-axis query has no Axes[1] → always test Axes.Count
    Console.WriteLine($"COLD: {swCold.ElapsedMilliseconds} ms — Axes.Count = {csCold.Axes.Count}, " +
        $"{csCold.Cells.Count} cell(s), value[0] = {csCold.Cells[0].FormattedValue}");

    var swWarm = Stopwatch.StartNew();
    CellSet csWarm;
    using (var cmd = new AdomdCommand(mdx, conn)) csWarm = cmd.ExecuteCellSet();
    swWarm.Stop();
    Console.WriteLine($"WARM: {swWarm.ElapsedMilliseconds} ms — value[0] = {csWarm.Cells[0].FormattedValue}");
    Console.WriteLine($"Cold/warm ratio: {(swWarm.ElapsedMilliseconds > 0 ? (double)swCold.ElapsedMilliseconds / swWarm.ElapsedMilliseconds : double.NaN):F1}x");

    verdicts.Add(("4. ClearCache + cold/warm", true,
        $"cold {swCold.ElapsedMilliseconds} ms / warm {swWarm.ElapsedMilliseconds} ms"));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED ClearCache/CellSet: {ex.GetBaseException().Message}");
    verdicts.Add(("4. ClearCache + cold/warm", false, ex.GetBaseException().Message));
}

// ---------------------------------------------------------------- Step 5b: perfmon deltas
if (perfmonOk)
{
    try
    {
        Console.WriteLine("\n--- Step 5b: perfmon deltas (before → after the 2 queries) ---");
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
        Console.WriteLine($"{moved} counter(s) changed out of {counters.Count} tracked.");
        verdicts.Add(("5. Remote perfmon", moved > 0,
            moved > 0 ? $"{moved} non-zero deltas / {counters.Count} counters" : "no delta — polluted global counters or wrong choice of counters"));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PERFMON deltas FAILED (non-blocking): [{ex.GetType().Name}] {ex.GetBaseException().Message}");
        verdicts.Add(("5. Remote perfmon", false, $"[{ex.GetType().Name}] {ex.GetBaseException().Message}"));
    }
}
foreach (var pc in counters) pc.Dispose();
conn.Dispose();

PrintSummary();
return verdicts.Where(v => !v.Etape.StartsWith("5")).All(v => v.Ok) ? 0 : 1;

void PrintSummary()
{
    Console.WriteLine("\n==============================================================");
    Console.WriteLine(" GO/NO-GO SUMMARY (criterion: steps 1-4 OK, perfmon may degrade)");
    Console.WriteLine("==============================================================");
    foreach (var v in verdicts)
        Console.WriteLine($"  [{(v.Ok ? "OK  " : "FAIL")}] {v.Etape} — {v.Detail}");
    bool go = verdicts.Count(v => v.Ok && !v.Etape.StartsWith("5")) >= 4;
    Console.WriteLine($"\n  VERDICT : {(go ? "GO" : "NO-GO")}");
}

static DataTable Dmv(AdomdConnection c, string query)
{
    using var cmd = new AdomdCommand(query, c);
    using var rdr = cmd.ExecuteReader();
    // Pitfall: ADOMD rowsets declare uniqueness constraints that their own data
    // violates → DataTable.Load throws "Failed to enable constraints" if the table
    // is not in a DataSet with EnforceConstraints = false.
    var ds = new DataSet { EnforceConstraints = false };
    var t = new DataTable();
    ds.Tables.Add(t);
    t.Load(rdr);
    return t;
}
