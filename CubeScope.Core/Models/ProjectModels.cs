namespace CubeScope.Core.Models;

/// <summary>The MDX Script read from an SSDT project .cube file (source of truth).</summary>
public sealed record ProjectScript(
    string Path,
    string CubeName,
    string FullText,
    IReadOnlyList<ScriptCommand> Commands,
    bool CanEdit,
    string? ReadOnlyReason);

/// <summary>Recently opened .cube project (persisted in SQLite).</summary>
public sealed record RecentProject(string Path, DateTime LastUsedUtc);

/// <summary>Result of a script-only deployment. Differs = the server held a different
/// script and force was false: nothing was written, ServerText needs reviewing.</summary>
public sealed record DeployScriptResult(
    bool Deployed,
    bool Differs,
    string? ServerText,
    long DurationMs);

/// <summary>Calculation properties (FormatString, DisplayFolder, Description) of a calculated
/// member/set of the MdxScript, read/written through a CalculationProperty element. Reference =
/// CalculationReference (e.g. "[Measures].[Marge]"). Fields are null when the matching child
/// element is missing from the XML (never an empty string).</summary>
public sealed record CalculationProp(
    string Reference, string? FormatString, string? DisplayFolder, string? Description);

/// <summary>An entry of the file browser (folder or .cube file).</summary>
public sealed record FileEntry(string Name, string Path, bool IsDirectory);

/// <summary>Contents of a folder for the server-side file browser.</summary>
public sealed record DirectoryListing(
    string Path,
    string? Parent,
    IReadOnlyList<string> Drives,
    IReadOnlyList<FileEntry> Directories,
    IReadOnlyList<FileEntry> CubeFiles);

/// <summary>A reusable MDX snippet, persisted in SQLite (local library).</summary>
public sealed record Snippet(long Id, string Name, string Mdx, DateTime CreatedUtc);

/// <summary>An MDX regression case: a query + its reference result (baseline)
/// serialized as JSON (ExpectedJson = a QueryResult). Re-run after a script change to
/// detect any value that changes.</summary>
public sealed record RegressionCase(long Id, string Name, string Mdx, string ExpectedJson, DateTime CreatedUtc);

/// <summary>A persisted profiler run (scalar metrics only — not the list of
/// subcubes) to allow a before/after comparison between two queries.</summary>
public sealed record ProfileRun(
    long Id, string Server, string? Catalog, string Mdx,
    long TotalMs, long StorageEngineMs, long FormulaEngineMs,
    int SubcubeCount, int CacheHits, int AggregationHits, DateTime ExecutedUtc);

/// <summary>An entry of the audit log of successful MDX Script deployments
/// (persisted in SQLite) — recorded only when the deployment actually took place.</summary>
public sealed record DeployLogEntry(
    long Id, string Server, string? Catalog, string CubeName, string ProjectPath,
    int ScriptChars, bool Forced, DateTime DeployedUtc);
