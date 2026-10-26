# CubeScope

Modern successor to MDX Studio: a workbench for a developer working alone
on SSAS **Multidimensional**. Write, understand, measure and maintain MDX
on existing cubes, with a built-in AI expert.
Open source project (MIT) meant for the community, but designed first for
its author's daily use. **Permanently out of scope: Tabular, Power BI,
DAX.** Never introduce a multi-engine abstraction "just in case".

## Architecture decisions (settled — do not reopen without a strong reason)

- **A single executable** `cubescope.exe`: ASP.NET Core 10 (Kestrel, free port
  on localhost) serving a Vue 3 SPA. **Native WPF/WebView2 shell
  (2026-09-11, `feat/coquille-webview2`): decision changed.** On launch,
  a native `CubeScope.Shell` window opens (WPF +
  `Microsoft.Web.WebView2.Wpf`) rather than the default browser — reason:
  taskbar icon, a proper window title, application keyboard shortcuts
  (F5, Ctrl+F…) that must no longer be swallowed by the browser
  chrome, process shutdown tied to closing the window rather than
  to a tab that can be left lying around. The Kestrel/SPA server remains
  a reusable library (`CubeScope.Server`), hosted either by
  `CubeScope.Shell` (native window, published exe), or as a browser fallback via
  `--force-browser` (original behaviour, kept for when the
  WebView2 runtime is missing). Technical details: see "Known pitfalls".
- **Solution**: `CubeScope.Core` (business services, no web dependency),
  `CubeScope.Server` (library: minimal API + SignalR + SPA hosting),
  `CubeScope.Shell` (WPF + WebView2, **published project**: hosts the server and
  shows the native window), `CubeScope.Server.Cli` (windowless console
  entry point for the dev loop — `--port`, `--no-browser`; not published),
  `CubeScope.Web` (Vue 3 + TypeScript + Vite).
- **SSAS connectivity**: NuGet package
  `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` (never the
  .NET Framework variant). AMO (`Microsoft.AnalysisServices.NetCore.retail.amd64`)
  only to read the MDX Script and resolve object IDs.
- **Metadata**: DMV `$SYSTEM.MDSCHEMA_*` as the main path (simple JSON
  mapping), typed schema rowsets as a complement if needed. Lazy loading
  of members (autocompletion) with an in-memory cache.
- **Execution stats**: perfmon counter deltas (categories `MSAS<ver>:*`
  or `MSOLAP$<instance>:*`, dynamic discovery by prefix). Counters are
  server-wide, not per session: accepted for the MVP.
- **Local state**: a single SQLite database (query history, recent connections,
  layouts, snippets). No scattered config files.
- **Frontend**: Monaco Editor (home-made Monarch MDX grammar), dockview for
  the layout, PrimeVue as the only UI kit (dialogs, tree, menus) whose
  virtualized `DataTable` serves as the v1 results grid — wrapped in
  `ResultsGrid.vue` (`columns`/`rows` interface) to switch to AG Grid
  Community if wide crossjoins crawl (observed, not assumed). SignalR
  for everything that streams (progress, future traces) — introduced in
  Phase 2 with the stats, not before.
- **MDX parser = pragmatic tokenizer**, no full AST. It serves
  syntax highlighting, detection of `[Dim].[Hier]` / `[Measures].[X]` references and
  the dependency graph through token matching (~95% accuracy, accepted).
- **AI**: service calling the Anthropic API (explain / optimize / detect
  anti-patterns / format), injecting the relevant cube metadata
  into the context. MDX formatting goes through the AI: **do not write a
  deterministic formatter** (identified effort trap).

- **Production rule for actions that modify a server** (2026-10-26, lot 4 — processing; the
  XMLA console follows the same rule): `ProductionActionGuard` (Core), distinct from
  `DevServerGuard`, which keeps its meaning (where deployment and ClearCache are allowed at all —
  ClearCache stays dev-only). A server declared dev → allowed after a simple confirmation in the
  UI. Any other server counts as **production**: the request must carry `confirmServer` equal to
  the session's server (trimmed, case-insensitive), typed again by the user, otherwise refused.
  In production, **Full or Data on a dimension is refused** whatever the confirmation
  (it leaves the dependent measure groups unprocessed and the cube unqueryable; Data: presumed,
  not observed — owner's decision). Checked in the Core service **before any connection** to the
  target server; every attempt, refused or not, goes to the SQLite processing log.
  **ProcessAdd is not offered**: without an out-of-line source query it re-reads the partition
  source and doubles the data. **ProcessClear is not offered either**: it empties the object and
  leaves it unqueryable, with no daily use (owner's decisions, 2026-10-26 — the XMLA console
  covers the rare real need, and refuses a Clear in production).
  **XMLA console** (lot 5): same rule — every script on a non-dev server needs the server name
  typed again — and, in production, refused whatever the confirmation: Delete, Restore, Detach,
  Synchronize (owner's list), plus ClearCache, MergePartitions (deletes the source partitions),
  Attach and ImageLoad (restore-like), Create/Alter of a whole Database, ProcessClear on any
  object, ProcessFull/ProcessData on any object carrying a DimensionID. Detected by parsing the
  XML, never by text search. Accepted limit: a `<Statement>` is opaque (an MDX/DMX statement
  that writes only needs the confirmation).

## Deferred / forbidden for the MVP

Deterministic formatter, interactive graphical map (the tree view
is enough), Extended Events viewer (perfmon first), cross impact analysis
(SSRS/Excel), refactoring, plugin system.

## Dev environment

- Windows only. Targets a real SSAS **Multidimensional** (tested on
  SSAS 2022 until mid-2026, on **SSAS 2025 (17.0.25.218)** since — dev and prod
  servers alike; no 2022 server left to compare against), default instance on port 2383, plus a named instance on a fixed
  port — the `host\instance` syntax does not work if SQL Browser (UDP 2382) is
  closed, use `Data Source=host:port`). For anything that clears the cache:
  target a **dev** catalog (ClearCache is scoped to the `DatabaseID`, prod
  is not touched), never a prod catalog. Enforced since 2026-09-24:
  `CacheService.ClearCacheAsync` applies the same `DevServerGuard` as script
  deployment (explicit list, fail-closed), server side.
- Windows integrated security for all SSAS connections. No plain-text credential
  anywhere.
- .NET 10 SDK, Node LTS, Vue 3 + strict TypeScript.

## Known pitfalls (already identified, do not rediscover)

- Remote perfmon: requires membership of the "Performance Monitor
  Users" group on the SSAS server and the Remote Registry service running. On
  failure, test on the server to tell permissions apart from counter names.
  Permissions symptom: `Win32Exception "Accès refusé"` as early as
  `PerformanceCounterCategory.GetCategories` (group SID = `S-1-5-32-558`).
- SSAS perfmon categories are **localized** (if the server OS is in French):
  separator `" : "` WITH spaces and translated labels — `MSAS16 : MDX`,
  `MSAS16 : cache`, `MSAS16 : mémoire`, `MSAS16 : connexion`, `MSAS16 : requête
  du moteur de stockage`, `MSAS16 : verrous`, `MSAS16 : threads`,
  `MSAS16 : traitement`, `MSAS16 : traitement des agrégations`, `MSAS16 :
  traitement des index`, `MSAS16 : mise en cache proactive`, etc. Two
  categories stay in English without spaces: `MSAS16:Database Auto Image
  Load`, `MSAS16:Reliability Metrics`. Named instance = prefix
  `MSOLAP$<instance>` (same labels). ⇒ Match the label after the first
  `:` (trimmed) in FR AND EN; never filter counters by name (`/sec`
  becomes `/s`) but by `CounterType` (`NumberOfItems32/64` = cumulative, use
  deltas).
- An **unprocessed cube disappears from `MDSCHEMA_CUBES`** (no more
  `CUBE_SOURCE=1` row; the `$` dimensions are still listed) — symptom: "empty
  database" although the catalog exists. Check `LAST_DATA_UPDATE` with the spike's
  `--discover` mode (observed on a redeployed, unprocessed catalog). AMO still
  sees it: the **State panel** (`ProcessingStateService`) lists it in red —
  re-checked on SSAS 17.0.25.218 with AMO 19.84.1 by an integration test.
- **Processing state through AMO** (`ProcessingStateService`, read-only probes on
  SSAS 17.0.25.218, dev and prod, 2026-10-26 — observed, not assumed):
  - `Database.LastProcessed` is **not** updated by a cube process (database at
    May, its cubes at last night) → never show it as "last processing".
  - A measure group's `LastProcessed` = its newest partition (or a few seconds
    later), a cube's = its newest measure group, after a cube-level process
    (0 partition out of 118 newer than its measure group). A **partition-only**
    ProcessFull does **NOT** move the `LastProcessed` of its measure group nor of its
    cube (observed 2026-10-26, integration test) → freshness is computed from the
    partitions (oldest/newest), never from the parent's own date.
  - An **Unprocessed object keeps a non-null `LastProcessed`** (the date of its
    last state change) → the state wins over the date.
  - `LastProcessed` comes back as `DateTimeKind.Local` → `ToUniversalTime()` in Core.
    Client and server share a time zone here: a different one is untested.
  - `EstimatedSize`: measure group = Σ of its partitions, values differ between
    dev and prod → reported by the server. Whether a process refreshes it is
    not proven yet (a ProcessFull of an unchanged small partition kept 1233 bytes —
    consistent, not conclusive).
- **Processing through AMO** (`ProcessService`, observed on 17.0.25.218 dev):
  `Server.CaptureXml = true` + `obj.Process(type)` **records without executing** (the object
  kept its date) and `ConcatenateCaptureLog(true, false)` gives a `<Batch Transaction='true'>`
  that addresses objects by **ID** (`MeasureGroupID` "Fait Flux" for the group named "Flux") —
  the preview shows that text and the run captures it again from the same object and mode,
  then executes it (`Server.Execute`). `Execute` **does not throw** for a server-side error: an
  unknown ID or a malformed command comes back as an `XmlaError` in the results
  (`ContainsErrors = true`); a success is one result without messages. 10 of the
  51 database dimensions of the dev base are used by no measure group: an empty "affected
  measure groups" list is normal there.
  - `EstimatedRows` is the **design-time** estimate (SSDT): identical in dev and prod,
    measure group ≠ Σ partitions, many zeros → shown only as a tooltip, flagged as such.
  - Without Read Definition, the database is assumed to be absent from
    `Server.Databases` (`FindByName` → null → "Unavailable"): **not verified**,
    no account without the permission was available.
  - First AMO `Connect` of a fresh PowerShell process took **42 s** once (60-180 ms
    afterwards, ~1 s for the whole read of 2 cubes / 118 partitions / 51 dimensions
    from CubeScope). Cause not elucidated; not reproduced in `cubescope.exe`.
- **(All) level**: `MDSCHEMA_LEVELS.LEVEL_TYPE = 1` (ADOMD `LevelTypeEnum.All`, official
  docs) — an exact value, not a bit (`GeoContinent` = 8193 shares the low bit). Observed on
  17.0.25.218: level 0 of all 99 non-measure hierarchies of the dev cube, never below; time
  levels come back as 20/68/132/516 (`TimeYears`…). Exposed as `LevelMeta.IsAll`; the builder's
  default level is the first non-All one (a non-aggregatable hierarchy has no All — none on the
  test cube, so that case rests on the docs only).
- **Member lookup by key** (`StrToMember('{level}.&[key]')` in a calculated measure, observed
  on 17.0.25.218, dev cube): an **unknown key gives a NULL cell**, not an error nor a failed query;
  real members can have an **empty caption and Name** (`[Entreprises].[Entreprise]`) → judge
  existence by `.UniqueName`, never by the caption; `.UniqueName` returns the canonical name
  (`[Dim].[Hier].&[k]`) even when asked with the level-qualified form; **numeric keys are
  converted** by the server (`0066249961`, ` 66249961` and `08` find the member); **date keys are
  not**: only the stored form `1900-04-11T00:00:00` is found — `11/04/1900`, `1900-04-11`,
  `19000411` give null. Behaviour under another `MdxMissingMemberMode` or dimension security: not
  observed (a failed batch is logged and falls back member by member).
- **Monaco keybindings across editors**: `editor.addCommand(keybinding, handler)` registers a
  keybinding on the ONE standalone keybinding service shared by every editor, with no condition
  (monaco 0.57 `standaloneCodeEditor.addCommand` → `addDynamicKeybinding`): the last editor to
  register wins and the binding outlives `dispose`. With two editors (MDX + XMLA console), F5 in
  the MDX editor ran the XMLA script. Use `editor.addAction({ id, keybindings, run })`, scoped to
  its editor. Checked for real: F5 in each editor runs its own content only.
- **XMLA through AMO `Server.Execute`** (observed on 17.0.25.218 dev): the script is wrapped in
  `Execute/Command`, so a `<Discover>` is refused by the server; a `<Statement>` with a DMV
  query returns an XSD schema followed by `<row>` elements in the `…:xml-analysis:rowset`
  namespace (a fragment without a single root); an MDX `<Statement>` returns an
  `…:mddataset` cellset; a `<Statement>` runs on the connection's `Initial Catalog`.
- SQLite migrations must stay **re-runnable**: tests rewind `user_version` to replay a step,
  and SQLite has no `ADD COLUMN IF NOT EXISTS` — check `pragma_table_info` first (v13).
- **Drag from the explorer (PrimeVue 4.5 `Tree`)**: each node's content div handles `dragstart`
  and, when the tree has no node drag-drop of its own (`draggableNodes` off), calls
  `preventDefault()` — which **cancels any drag starting inside a node**, ours included
  (symptom: `dragstart` then nothing — no `drag`, `dragover` nor `dragend`). Its `dragend`
  handler also throws (`stopDrag` of an absent service). Hence `e.stopPropagation()` in our
  `dragstart` and `@dragend.stop` on the draggable span. The explorer → editor drag had never
  worked in the real app since it was added (2026-09-11) — found by the owner on 2026-10-26.
- **Drop into Monaco**: Monaco's `dropIntoEditor` contribution (imported in `monaco-core.ts`)
  already inserts dropped `text/plain` at the release point and marks the event handled; a
  host-level `drop` handler must skip `e.defaultPrevented`, otherwise the text comes in twice.
- **E2E drag and drop**: Playwright's real `dragTo` DOES drive HTML5 drag-and-drop in Chromium.
  ⚠️ Lesson (2026-10-26): when it fired `dragstart` and nothing else, that was this app's bug
  (the drag was cancelled), NOT a harness limitation — synthetic `DragEvent`s dispatched on the
  target bypass the source and hid it for three lots. Test drags with the real `dragTo` first,
  and log `defaultPrevented` on `dragstart`. During `dragover` only `dataTransfer.types` is
  readable, never `getData`.
- ADOMD rowsets → `DataTable.Load`: rowsets (DMVs as well as schema rowsets)
  declare uniqueness constraints that their own data violate
  (`ConstraintException "Failed to enable constraints"`). Always load the
  `DataTable` inside a `DataSet { EnforceConstraints = false }` before `Load`.
- `CellSet`: a single-axis query has no `Axes[1]`; always test
  `Axes.Count`. (Confirmed in the spike: 1 axis → `Axes.Count = 1`.)
- XMLA `ClearCache`: requires the `DatabaseID`, which differs from the name after
  a rename — resolve it through AMO, not by convention. (In the spike, `DatabaseID` =
  catalog name worked on a database that was never renamed; do not make it
  a rule.)
- Do NOT put a non-existent `Initial Catalog` in the ADOMD connection string: opening
  without a catalog then calling `ChangeDatabase()` works fine and allows listing
  `DBSCHEMA_CATALOGS` first.
- Profiler (SSAS trace, `CubeScope.Spike --profile`): AMO .NET Core 19.84.1
  does support the live `Trace.OnEvent` subscription (events pushed in
  real time). Requires **SSAS admin** rights (trace creation).
  **MAJOR PITFALL**: each `TraceEventClass` has its own whitelist of
  columns, validated **server-side at `Trace.Update()`** (not at `Columns.Add`,
  client-side, which throws nothing). Invalid pair → `OperationException`
  « L'ID d'événement Id=X ne contient pas l'ID Id=Y » with
  X=`(int)TraceEventClass`, Y=`(int)TraceColumn`. Chosen solution
  (`ProfileSpike.cs`): a self-correcting loop that parses (X,Y), removes
  column Y from event X and retries `Update()`. Only follow the
  "completed" events (`QueryEnd`, `QuerySubcube(Verbose)`,
  `GetDataFrom*`, `*End`) — the "Begin" ones have no `Duration`. Breakdown:
  `QueryEnd`.Duration = total, Σ `QuerySubcube`.Duration = Storage Engine,
  FE = total − SE, + cache/agg hits. Filter by `SessionID` (trace column =
  `AdomdConnection.SessionID`). Server trace = **global** → always
  `Stop()`+`Drop()` in `finally`; clean up orphaned `CubeScope_*` traces
  after a crash.
- Package `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` 19.84.1:
  pulled `Microsoft.Identity.Client` 4.56.0 transitively, with 2 known
  vulnerabilities (NU1901/NU1902, low/moderate severity). **Resolved**: direct pin of
  `Microsoft.Identity.Client` 4.86.1 in `CubeScope.Core` and `CubeScope.Spike`
  (forces the transitive dependency to the patched version — `dotnet list --vulnerable` audit
  empty, build 0 warnings). MSAL 4.86.1 is compatible with ADOMD 19.84.1 (Entra auth not
  used anyway, we run with Integrated Security). Resync this pin
  if ADOMD/AMO move up a version.
- Target `net10.0-windows` (not `net10.0`): `System.Diagnostics.PerformanceCounter`
  is Windows-only and produces ~30 CA1416 warnings otherwise.
- DMV: **bracket every column** (`SELECT [HIERARCHY_UNIQUE_NAME] …`) —
  `HIERARCHY` (among others) is an MDX reserved word, the unbracketed query
  fails with a syntax error. `CUBE_NAME`/`MEASURE_NAME` get through bare by luck.
- **Caption of a member from its key** (hovering `…&[key]` in the script): do NOT
  go through `$SYSTEM.MDSCHEMA_MEMBERS`. (a) The DMV **does not support `IN (…)`**
  (« La syntaxe de "IN" est incorrecte »). (b) Filtering by `MEMBER_UNIQUE_NAME`
  alone (even with `HIERARCHY_UNIQUE_NAME`) **scans the whole dimension** →
  freeze on a securities dimension (thousands of ISINs). The right method = **MDX**
  `StrToMember('[Dim].[Hier].[Level].&[key]').Properties("MEMBER_CAPTION")`:
  direct resolution by key, zero scan; a single query resolves a whole batch
  (`WITH MEMBER [Measures].[__capN] AS … SELECT {…} ON 0 FROM [cube]`), with a
  member-by-member fallback if a stale reference makes the whole batch fail.
  Persistent SQLite cache (`MemberCaption`) invalidated on the cube's
  `LAST_SCHEMA_UPDATE|LAST_DATA_UPDATE` fingerprint.
- `CELL PROPERTIES VALUE` (queries copied from Excel/SSMS): the server returns
  ONLY the listed properties → `Cell.FormattedValue` is an **empty string, not
  null** (`??` is not enough). Always fall back to `Cell.Value` when
  FormattedValue is null OR empty (done in `CellSetMapper.CellValue`), otherwise
  the grid shows empty columns although the data is there.
- **Cell in error**: XMLA returns `<Cell><Value><Error><Description>…`, and ADOMD
  relays that Description as an `AdomdErrorResponseException` thrown on `Cell.Value`
  **AND** `Cell.FormattedValue` **AND** the `VALUE`/`FORMATTED_VALUE` `CellProperties`
  (verified on a real cube — no accessor returns the error without throwing). Never
  swallow the exception: `CellSetMapper.CellValue` keeps `ex.Message` and `Build` writes it
  under a twin key `v{c}__err` in the row (no change to the model or to
  serialization; the CSV/TSV export only iterates over `Columns` and ignores it). The grid
  shows `#Erreur` in red, with the message in a tooltip and on click.
- `CellSet`: `axis.Set.Hierarchies` triggers a lazy resolution of schema
  objects that can fail (`ArgumentException "Impossible de trouver l'objet
  [Dimension].[Membre]"`, observed on a real cube) although positions/cells
  are already there. `CellSetMapper` has a fallback: labels inferred from the members'
  `UniqueName` (careful, for a measure `[Measures].[X]` the 2nd segment is the
  member, not the hierarchy).
- PrimeVue: **stay on v4.5.x + `@primeuix/themes`**. npm installs v5 by
  default, which requires `@primeuix/styled` ^1.0 (incompatible with `@primevue/themes` 4.x)
  and ships an unaudited `license-manager`. Permanent dark mode: class
  `p-dark` on `<html>` + `darkModeSelector: '.p-dark'` (`':root'` does not work).
- PrimeVue `InputNumber` updates its `v-model` **only on blur** (observed with real
  keystrokes): a button disabled while "unchanged" stays disabled during typing, and an
  `@keydown.enter` handler saves the previous value. Read the value from its `@input`
  event (`{ value }`) — see `ProcessingPanel.vue`. In E2E tests, Playwright `fill()`
  bypasses its key handlers: type key by key.
- monaco-editor ≥ 0.56: exports map `"./*" → "./esm/vs/*.js"` — import
  `monaco-editor/editor/editor.worker?worker`, never again the `esm/vs/…` path
  (Vite/Rolldown no longer resolves it). Monaco uses EditContext: no more
  `textarea.inputarea` for E2E tests, click `.view-lines` then use the keyboard.
- dockview-vue: `DockviewVue` is **multi-root** (portals) → the parent's scoped CSS
  does not reach it (silent zero height). Wrap it in a sized div
  and pass it `style="width:100%;height:100%"`.
- Slimmed-down Monaco: `src/monaco-core.ts` reproduces `editor.main.js` WITHOUT the
  81 languages or the 4 worker-based features (dist 26 MB → 6 MB). The import list
  must be **resynced on every monaco version bump** (generated from
  `esm/vs/editor/editor.main.js`).
- Perfmon in practice: useful categories per query = `MDX`, `cache`,
  `requête du moteur de stockage` (~53 cumulative counters, observed).
  Accepted MVP limit: `MSAS*` prefix (default instance) — for a
  connection to a named instance on a fixed port, the port→instance mapping is
  not discoverable, the counters remain those of the default instance. Discovery
  (`Initialize`) takes ~2-4 s → started in the background on
  connection; a query started before it ends simply has no stats.
- **Self-contained** single-file exe: by default publish leaves the SPA (`wwwroot`)
  AND the native DLLs (`e_sqlite3`, `msalruntime`…) as **loose files next to**
  the exe → moved on its own, 404 on `index.html` (`ContentRoot` = the exe's
  folder) and SQLite crashes. Fixed: `IncludeNativeLibrariesForSelfExtract=true`
  + SPA embedded in the assembly (`EmbeddedResource` with prefix `spa/`, served by
  `EmbeddedSpaFileProvider`). MSBuild pitfalls: hook the embedding target to
  `BeforeTargets="PrepareForBuild"` (at `CoreCompile` it is too late, 0 resources
  embedded); a `LogicalName` using `%(Filename)` on a self-referencing Include
  evaluates to empty (`CS1508` collision) → go through a qualified intermediate item;
  glob `**\*.*` (not `**\*`, which matches folders). Target active only on
  publish (`_IsPublishing` OR `-p:EmbedSpa=true`).
- Dev server restarting: always check that no orphaned process
  holds the port or locks the DLLs (leftover `cubescope.exe`) — otherwise
  an outdated binary is served silently (the SPA fallback returns index.html for
  any unknown API route: an HTML 200 on an expected endpoint = symptom of an
  old binary, not a front-end bug).
- **Automatic shutdown when the browser closes** (`BrowserLifetime`): the
  `StatsHub` connections serve as a heartbeat. ⚠️ **PITFALL — a hub disconnection
  does NOT mean the page is gone**: the client uses
  `withAutomaticReconnect()`, which retries after **0, 2, 10 then 30 s**. Shutting down after
  a short delay on a mere transport drop kills the server under a page that is
  still open ("Failed to fetch") — and since the exe takes a **free port** on
  launch, restarting it yields another port: the tab left open points at a dead
  port. Hence two delays: the page announces its departure with
  `navigator.sendBeacon('/api/leaving')` on `pagehide` (close **or** F5) →
  short grace period (10 s); without notice, it is the transport that dropped → long grace period
  (45 s), beyond the reconnection window. The beacon and the socket close
  race each other: both arrival orders are handled (`NoticeClientLeaving`
  shortens an already armed shutdown). ⚠️ **Coupled with `--no-browser`**: this flag
  also disables automatic shutdown, otherwise the dev loop and the tests would
  stop as soon as the page is closed. So in dev the server never stops
  on its own — that is intended, not a failure. Nothing is armed until a client has
  connected (the exe cannot shut down while the browser is opening).
- **Comparison between catalogs** (`CatalogComparisonService`): the same query is run
  on the current catalog (through the session, hence visible to the Profiler) and on another catalog
  of the same server through `SsasSession.WithTransientConnectionAsync` — a fresh connection, **same
  connection string and therefore same locale**, otherwise column labels would differ and
  `ResultComparer` would see false differences. Accepted consequence: the right-hand query has its
  own SessionID, the Profiler does not see it. ⚠️ The comparison is on the
  **formatted** values (`CellSetMapper` prefers `FormattedValue`, like the regression harness): a
  mere `FORMAT_STRING` change between two catalogs therefore shows up as a difference.
  Diffs capped at 200.
- **SSAS sessions** (`SessionsService`, Sessions panel): the DMV engine accepts
  **neither JOIN, nor GROUP BY, nor LIKE, nor CAST** → `DISCOVER_SESSIONS` and
  `DISCOVER_COMMANDS` are read separately then matched in C# on `SESSION_SPID`.
  Reading these DMVs requires **server admin rights**. Useful columns (observed on
  SSAS 2022): `SESSION_ID` (GUID), `SESSION_SPID`, `SESSION_USER_NAME`,
  `SESSION_CURRENT_DATABASE`, `SESSION_LAST_COMMAND`, `SESSION_CPU_TIME_MS`,
  `SESSION_IDLE_TIME_MS`; durations are `UInt64` on the sessions side, `Int64` on the commands side
  (convert, do not cast). ⚠️ The list contains the sessions of **prod jobs and
  other users** — hence the detailed confirmation before cancelling.
  Cancellation = XMLA `<Cancel>` with `<SPID>` + `<CancelAssociated>` ([MS docs](https://learn.microsoft.com/analysis-services/instances/disconnect-users-and-sessions-on-analysis-services-server)).
  A **SPID gets stale**: the displayed list may target a session that is already gone, hence the
  existence check before issuing the Cancel (otherwise « La session spécifiée est
  introuvable » reaches the user raw).
- **Cancelling YOUR OWN session** leaves ADOMD with a connection in the **`Open`** state whose
  session ID no longer exists on the server: the next call fails with « L'ID de
  session … est introuvable. Soit la session n'existe pas, soit elle a déjà expiré »,
  then ADOMD negotiates a new one (so the call after that succeeds). `conn.State` gives
  nothing away — it is a failure mode **distinct** from « La connexion n'est pas
  ouverte ». Hence `SsasSession.ResetAsync()`, called after cancelling one's own
  session.
- **Keyboard shortcuts in the browser** (`--force-browser` fallback
  only — the default native window no longer uses it, see below):
  `F12` is taken by the Edge/Chrome developer tools and **cannot be
  intercepted** by page content — no point binding it in Monaco.
  "Go to definition" uses `Alt+F12` and `Ctrl+Alt+G` (both
  verified in the browser), plus the context menu. `F5` can be intercepted,
  though (already used for execution). **In the native window
  (`CubeScope.Shell`, the default since 2026-09-11)**: `F12` is
  freed for the application (`AreBrowserAcceleratorKeysEnabled = false`
  stops the Edge devtools from grabbing it — see the "WPF/WebView2
  shell" block below), so it can be bound in Monaco again if needed.
- **Local API hardening (2026-09-24)**: any page open in the browser can reach
  `http://127.0.0.1:<port>` (CSRF, DNS rebinding), and an AI answer rendered with
  `v-html` can carry script. Hence: (a) `LocalRequestGuard` (first middleware)
  refuses a non-loopback `Host`, a non-loopback `Origin` and `Sec-Fetch-Site:
  cross-site|same-site` with a plain-text 403 — the Vite proxy (`localhost:5173`)
  passes, opening Vite from a LAN IP does not; (b) a CSP on every response
  (`LocalRequestGuard.ContentSecurityPolicy`: no inline script, no eval) — adjust
  it there if a new library needs more; (c) AI Markdown goes through
  `src/markdown.ts` (DOMPurify, links forced to `_blank`), never `marked` +
  `v-html` directly; (d) `LocalPathGuard` refuses UNC/device paths and URIs
  (`\\host\share` would leak the NTLM hash) and requires `.cube` for project
  files; (e) the Shell only hands http/https to the OS and cancels any WebView
  navigation away from the local origin (`NavigationPolicy`). No launch token:
  deliberate, the checks above cover the threats without plumbing.
- `.cube` save: atomic (`.tmp` + `File.Replace`) and guarded by a content hash —
  `/project/open` returns `contentHash`, `/project/save` takes `expectedHash` and
  answers **409** if the file changed outside CubeScope; `/project/calcprops`
  returns the new hash, which the client MUST keep, otherwise its next save is a
  false 409.
- The name "MDX" is polluted by Markdown+JSX in the npm/GitHub ecosystem: do not
  name front-end packages `mdx-*`.
- `.cube` round-trip (SSDT project mode): `XDocument.Load` must use
  `LoadOptions.PreserveWhitespace`, otherwise the XML indentation is silently
  reformatted on save.
- `Save` and `Load` (`CubeProjectService`) must agree on the definition
  of an editable `Command` — a `<Text>` made only of whitespace does not count
  as content, otherwise `CanEdit` (computed at `Load`) and the save
  guard (recounted at `Save`) diverge.
- Monaco folding by regions (`// #region` / `// #endregion`): declared through
  `folding.markers` (regex) in the language config `monaco-mdx.ts`; the
  folding contribution itself is already imported by `monaco-core.ts`.
- `en.ts` typed as `typeof fr` enforces completeness of the i18n keys at
  compile time: a missing key becomes a TypeScript error, not a silent empty
  text in prod.
- **WPF/WebView2 shell (2026-09-11 work)** — pitfalls observed while
  building it:
  - By default, WebView2 creates its data folder **next to the exe**
    (`{name}.exe.WebView2`) — which breaks a moved executable (Microsoft
    docs). ⇒ explicitly force a folder under
    `%LOCALAPPDATA%\CubeScope\WebView2` in `CoreWebView2Environment.CreateAsync`.
  - `IHostApplicationLifetime.StopApplication()` triggers **only**
    `ApplicationStopping`; the bridge to `ApplicationStopped` lives in
    `WaitForShutdownAsync()`, which `ServerHost.StartAsync` does not call.
    Subscribing to `ApplicationStopped` from the Shell would wait for an event
    that never comes.
  - `StopAsync()` does **not** release `IDisposable` singletons: it is
    `DisposeAsync()` on the host that destroys the DI container, and therefore
    triggers the `Stop()`+`Drop()` of the SSAS trace in
    `ProfilerService.Dispose()`. `app.Run()` did it in its `finally`;
    when driving the lifecycle yourself (Shell), you take over that
    obligation — call both when the window closes.
  - `SystemParameters.WorkArea` only describes the **primary screen**. To
    reason about several screens (restoring the window geometry), you
    need `SystemParameters.VirtualScreenLeft/Top/Width/Height`.
  - `AreBrowserAcceleratorKeysEnabled = false` also disables **zoom**
    (Ctrl+Plus/Minus/0), which has to be wired back by hand; the other
    disabled shortcuts (Ctrl+F, F3, F5…) still reach the web
    content, so Monaco gets them normally.
  - `-p:EmbedSpa=true` must be **explicit** on publish: the target lives in
    `CubeScope.Server.csproj`, which is no longer the published project (it is
    `CubeScope.Shell` now — see README, Publish section).
  - **Fate of `WebView2Loader.dll` in single-file — observed on 2026-09-11**
    (final verification task, exe isolated in a blank folder, launched
    without arguments): the loader **survives** the single-file publish. It is
    copied neither next to the exe nor into `publish/` — it self-extracts at
    launch (standard .NET `IncludeNativeLibrariesForSelfExtract` mechanism,
    not a WebView2-specific behaviour). **Self-contained proof**:
    relaunched with `DOTNET_BUNDLE_EXTRACT_BASE_DIR` pointed at a folder created
    for the occasion and confirmed **empty before launch** (0 items) —
    `WebView2Loader.dll` does appear there afterwards, so nothing pre-existing
    on the machine could have been used. The isolated exe started normally (window
    "CubeScope", local port listening, `GET /index.html`, `/assets/*.js`,
    `/api/*` all 200) and no `*.WebView2` folder appeared next to
    it — the data did go under
    `%LOCALAPPDATA%\CubeScope\WebView2\EBWebView`, as expected.
  - **A Windows session shutdown can leave an SSAS trace alive**
    (found by reasoning on 2026-09-11, final review — not reproduced
    for real): a restart or a logoff calls `Shutdown()` and shuts down
    the Dispatcher without guaranteeing that the `StopAsync` continuation resumes. The
    host's `DisposeAsync` — hence the trace's `Stop()` + `Drop()` — can
    be **skipped**, and `CubeScope_Profiler_<pid>` survive the restart.
    Self-healing (the orphan cleanup, on the next `Initialize`,
    drops traces whose PID is dead), but in the meantime the trace runs
    on an SSAS server shared with production. Structural: Windows does not
    promise a process extra time at logoff. Cannot be fixed
    cleanly → accepted and documented here rather than patched over.
  - **`beforeunload` is never evaluated in the native window**: destroying a
    WebView2 control does not go through the browser's closing path, so the
    `ScriptPanel.vue` handler now only serves the
    `--force-browser` fallback. The safeguard has been rebuilt on the shell side since
    2026-09-11: the page publishes its state in `window.__cubescopeDirty` (watch
    on `dirty`, `immediate`), and `MainWindow.Closing` — synchronous whereas the
    answer is asynchronous — cancels the close, reads the flag through
    `ExecuteScriptAsync` (answer = **JSON string** `"true"`/`"false"`, anything
    that is not exactly `true` meaning "nothing to lose"), asks for
    confirmation with a `MessageBox` if needed, then calls `Close()` again;
    `_fermetureConfirmee` tells the two passes apart. Three safeguards make
    the close button unbreakable, because a safeguard that prevents QUITTING would be worse
    than no safeguard: (a) any exception lets the window close (WebView2 not
    initialized, page not loaded); (b) the query is capped at **2 s** by
    a `Task.WhenAny` — `ExecuteScriptAsync` is posted to the renderer's JS thread
    and never resolves while synchronous work keeps it busy, which
    would make the close button inert; (c) a `_verificationEnCours` flag prevents
    a second click on the close button from starting a concurrent check (stacked
    dialogs, `Close()` throwing on an already closed window). The geometry is
    saved on EVERY pass and not only on the confirmed pass: WPF ignores
    `e.Cancel` during an `Application.Shutdown()` (end of Windows session), where the
    geometry would otherwise be lost. **Accepted
    limit: it only covers what the page exposes** — only the MDX Script of an
    SSDT project feeds this flag (neither a running query nor a results
    tab), and if the Script panel is unmounted, the last published value
    stays in place (at worst one question too many, never a silent loss).

## Working conventions

- Each phase ends with a binary usable day to day; no
  large speculative refactoring.
- Any new architecture proposal must be justified against:
  simplicity, robustness, low maintenance, speed of delivery.
- Tests: cover the MDX tokenizer and the Core services; no
  coverage target for the UI.

## Status

**Roadmap complete, product in daily use.** Published on
`github.com/dasimon/CubeScope`, tagged versions up to **v0.16.0** (each tag
triggers the GitHub Actions Release). Detailed, dated history of every
change: kept in the author's private notes (not published) — this section
is only its summary.

MVP delivered (Phases 1–5): connection + Monaco editor + execution + grid;
metadata explorer, autocompletion, perfmon stats, ClearCache,
history; AI panel (Anthropic API, `claude-opus-4-8`); MDX Script +
dependency graph + exportable Markdown doc; GitHub publication (MIT,
CI + GitHub Actions Release). Post-MVP extras: **Profiler** (per-query
Formula/Storage Engine breakdown through an SSAS trace), **FR/EN i18n**.

Delivered afterwards (v0.2 → v0.10), by theme:

- **Editor productivity**: CSV/clipboard export, snippet library,
  calculated member scaffold, run selection, results tabs,
  drillthrough, function signatures, search in the script, member
  renaming (`MemberRenamer`), structural folding `{ }` / `( )` / SCOPE.
- **Metadata on hover**: hover resolving a measure/member reference to its
  caption + description, including `&[key]` keys and composite keys, with
  progressive preloading and a **persistent SQLite cache** invalidated on the
  cube fingerprint. Measure descriptions in the explorer and in autocompletion.
- **SSDT project mode** (detailed below): `.cube` file browser,
  editing of `CalculationProperty`, side-by-side Monaco diff on deployment.
- **Analysis**: MDX regression harness (reference queries, re-execution,
  diff), change impact analysis (script version diff + downstream
  impact), Profiler run history with before/after comparison.
- **AI**: "Explain this calculation" (calculated member tracer), "Optimize
  (profile)" backed by the real figures of the execution profile (FE/SE, subcubes,
  hits), **NL → MDX** generation grounded in the cube metadata, and
  **alternative OpenAI-compatible providers** in addition to the Anthropic API.

**Admin tools, lot 1 — "State" panel: DONE (2026-10-26)**, branch
`feat/etat-cube`. Read-only processing state of the current database through a
short AMO connection (`ProcessingStateService`, separate from the ADOMD session):
cubes → measure groups → partitions, plus dimensions, with state, last processing,
freshness range from the partitions, storage mode, server-reported size, slice,
aggregation design. Pure verdict in `ProcessingStateEvaluator` (Unprocessed wins
over the date; Stale beyond a threshold, default 24 h, stored in SQLite —
`Setting` table, schema **v10**). Red/orange badges, summary in the status bar
(click opens the panel), `GET /api/processing/state`, `PUT /api/processing/threshold`.
Read once per catalog selection and on Refresh — never polled. Accepted limits:
on a cube whose history partitions are never reprocessed, they all show as stale;
ROLAP partitions and linked measure groups get no special rule (none on the test
database); the threshold can only be edited once a catalog is selected.
Verified for real against the dev server (SSAS 17.0.25.218, AMO 19.84.1).

**Admin tools, lot 2 — graphical query builder: DONE (2026-10-26)**, branch
`feat/constructeur` (on top of `feat/etat-cube`). "Builder" panel (below the explorer):
Measures / Columns / Rows / Filters zones fed by dragging from the explorer, which now
adds `application/x-cubescope-item` (`{kind, uniqueName, hierarchyUniqueName, caption}`)
next to the unchanged `text/plain`. One direction only: model → MDX, generated in Core by
the pure `MdxQueryBuilder` (`POST /api/builder/mdx`) and written into the editor
(`store.mdx`, `mdxRevision++`, undoable with Ctrl+Z; a toast says so when a hand-typed
query is replaced; filters alone never write). Rules: measures innermost on COLUMNS;
NON EMPTY **per axis** (an MDX axis keyword — per item would need `NonEmpty()`, different
semantics); one entry per hierarchy and per zone, dropping on the other axis moves it;
item = whole level (`.Members`) or chosen members (lazy member picker), optional
`TOPCOUNT`/`ORDER … BDESC|BASC` on a measure, evaluated per hierarchy set before the
crossjoin; filters: one member → WHERE, several (or a hierarchy also on an axis) → nested
sub-selects; rows without columns → `{[Measures].DefaultMember}` on COLUMNS. Unique names
are passed verbatim after a strict check (bracketed segments only), the cube name is
bracketed (`]`→`]]`); no string literal, so `'` needs no doubling. State persisted in
`localStorage` (versioned), dropped when server|catalog|cube differs from the saved one;
the last cube chosen per server+catalog is remembered so a restart reopens it.

**Admin tools, lot 3 — pasted key lists and named lists: DONE (2026-10-26)**, branch
`feat/listes` (on top of `feat/constructeur`). "Paste a list" on the builder's Rows and
Filters zones: choose a target level, paste keys (line breaks, commas, semicolons, tabs;
trimmed, CSV quotes and zero-width spaces removed, duplicates dropped — `KeyList.Parse`, Core),
check, then add. `POST /api/metadata/resolve-keys` (raw `text` or `keys`) builds
`{level}.&[key]` (`]` doubled; `&[a]&[b]` composite passed as is) and resolves by MDX by key,
50 per query, member-by-member fallback (`MetadataService.ResolveKeysAsync`), never
MDSCHEMA_MEMBERS. Result = found (canonical unique name + caption) / missing (no such member) /
failed (lookup in error — a server problem never reads as "unknown keys"; a batch failing key by
key is rethrown); only found keys enter the query. Named lists in SQLite (`NamedList`, schema
**v11**, keys in pasted order, found or not), reloaded with a fresh check. Cap: 5000 keys.
Deliberate deviation from "reuse `ResolveCaptionsViaMdxAsync`": it judges existence by the
caption, and members with an empty caption exist (see Known pitfalls).

**Admin tools, lot 4 — processing from the State panel: DONE (2026-10-26)**, branch
`feat/traitement` (on top of `feat/listes`). Right-click (or the row's cog) → "Process…":
mode (Default, Full, Data, Update for a dimension — no Add nor Clear, see settled decisions),
XMLA preview captured by AMO (copyable), production banner and server name to retype on a
non-dev server, warning listing the measure groups a dimension Full/Data leaves
unprocessed (Update: flexible aggregations and indexes dropped, warned without refusal),
elapsed time while running (dialog non-modal then, so the Sessions panel stays reachable to
cancel), result with server messages, state read again afterwards. `ProcessService` (short AMO
connection, one run at a time, the XMLA re-captured server-side — never taken from the
client), `ProductionActionGuard` (see settled decisions), `POST /api/processing/preview`,
`/run` (403 `productionRefused`), `GET /log`; SQLite `ProcessLog` (schema **v12**, every
attempt including refusals, with the targeted IDs). Verified on the dev server: preview
without execution, ProcessFull of one small partition from the UI and from the tests.

**Admin tools, lot 5 — XMLA console: DONE (2026-10-26)**, branch `feat/console-xmla` (on top
of `feat/traitement`). "XMLA console" panel (tab next to MDX/Script) with its own Monaco editor
(XML colouring: only `languages/definitions/xml/register` added to `monaco-core.ts` — main
bundle +10 KB, grammar lazy chunk 1.9 KB), Ctrl+Enter/F5, server name to retype on a non-dev
server, XMLA snippets (same SQLite library, `Snippet.Language` mdx|xmla), log (SQLite `XmlaLog`,
schema **v13**, 200 entries, 1 MB per script, preview in the list), "Open in the XMLA console"
from the processing dialog. `XmlaScriptAnalyzer` (Core, pure) parses the script with XDocument
before anything is sent: invalid XML, `Discover` and unknown elements inside a Batch refused;
commands found only at structural positions (root, Batch, Parallel), by local name whatever the
namespace; the PARSED script is what is sent. Production refusals: see settled decisions.
`XmlaConsoleService`: AMO `Server.Execute` on a short connection with `Initial Catalog`
(connection-string builder), messages/warnings/errors, a DMV rowset in `ResultsGrid` (capped at
10,000 rows), an MDX cellset only reported (the MDX editor is for queries). Verified on the dev
server: DMV in the grid, invalid XML refused, snippet save/load/delete, open-in-console then run,
log, F5 isolation between the two editors.

**SSDT project mode: DONE (2026-07-24)** — opening/editing the
`.cube` file of an SSDT Multidimensional project (`CubeProjectService`,
`CubeScope.Core/Project/`): XML read with `PreserveWhitespace`, script
editable only if there is exactly 1 non-empty `Command` (`CanEdit`), regions
`// #region` / `// #endregion` (parsed by `ScriptParser`, matching Monaco
folding), round-trip save into the `.cube` + text export
`<name>.mdxscript.mdx` (readable Git diffs) + `.bak` once per session,
report of orphaned `CalculationProperty` entries (reference to a vanished calculated
member/set — reported, never deleted automatically). Deployment of the
script alone to a dev cube through AMO, BIDS Helper style
(`ScriptDeployService`) with a divergence guard (compares server vs project,
refuses without `force` if different) and a **dev server guard**.

⚠️ **The server guard changed nature on 2026-09-11.** It sniffed the
catalog name ("contains dev") and lived **only in the UI** — a direct call to
the API bypassed it. Two reasons to rebuild it: (a) once dev stops sharing
the production server, prod and dev carry the **same catalog name** and the name
no longer tells anything apart; (b) a substring rule would file "SRV-DEV-PROD" on
the dev side. From now on: an **explicit** list of servers (`DevServerGuard`, StateStore
`DevServer` table, declared in the connection dialog — deliberately not
in the deployment dialog, where the safeguard would get disarmed under the pressure of the action).
The guard lives **in `ScriptDeployService.Deploy`**, before any AMO connection, and
`force` does not bypass it: `force` means "overwrite a server script that has
diverged", never "deploy to production". Empty list = no server allowed
(fail-closed). On the test side, `TestTarget.ServerDev` + `AssertDevServerDistinct()`
aborts any destructive test whose dev server equals the production one.

`cubescope.exe` is a self-contained single-file: the SPA and native DLLs are
embedded in the assembly (see `EmbeddedSpaFileProvider` + the `EmbedSpa` target
of the `.csproj`) → the exe works on its own and can be moved.

Roadmap (US): Phase 1 = connection + editor + execution + grid (US 1-4);
Phase 2 = explorer, autocompletion, stats, cache, history (US 5-12);
Phase 3 = AI panel (US 13-15); Phase 4 = script, dependencies, doc (US 16-20);
Phase 5 = GitHub publication. The `CubeScope.Spike` project stays in the solution
as a server regression harness: read-only (`--discover`) by default; clearing the
cache needs `--clear-cache` AND the server listed in `CUBESCOPE_SPIKE_DEV_SERVERS`.
