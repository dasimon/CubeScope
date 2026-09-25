# CubeScope

**A modern workbench for SSAS Multidimensional developers.** Write, understand,
measure and maintain MDX against existing cubes — with a built-in AI expert.

CubeScope is the spiritual successor to MDX Studio: the publish step produces
a single executable, `cubescope.exe`, with no installer. It opens in a native
window backed by the WebView2 runtime, falling back to your default browser
if that runtime isn't present — no server component to deploy, no cloud.

![CubeScope in action](docs/screenshots/demo.gif)

> **Scope.** CubeScope targets **SSAS Multidimensional** only. Tabular, Power BI
> and DAX are permanently out of scope by design — there is no multi-engine
> abstraction and none is planned.

---

## Features

- **MDX editor** — Monaco with a hand-written MDX grammar (syntax highlighting,
  reference detection), autocompletion of measures, hierarchies and members
  (lazy-loaded after `.`), structural folding of `{ }` / `( )` / `SCOPE` blocks
  and `// #region` sections (with fold-all / unfold-all), function signatures and
  measure/member captions on hover, execute with `F5` / `Ctrl+Enter` (the whole
  editor or just the selected text), cancel in flight.
- **Results grid** — virtualized grid handling wide crossjoins; export to CSV
  or copy to the clipboard (Excel-friendly); recent results kept in closeable
  tabs, and `DRILLTHROUGH` a query to view its source rows.
- **Productivity helpers** — a reusable MDX **snippets** library (save / insert /
  delete) and a **calculated-member scaffold** (WITH MEMBER / CREATE MEMBER).
- **Regression harness** — capture queries with their current results as a
  baseline, then re-run them after a script change and flag any changed value
  (per-cell diff) — "did my calculation change silently break a value?".
- **Metadata explorer** — filterable tree of measures and dimensions (DMV-backed);
  double-click inserts at the cursor. Measure descriptions surface as hover
  tooltips and in the MDX autocomplete documentation.
- **Query profiler** — per-query **Formula Engine vs Storage Engine** split from
  SSAS traces, `Query Subcube` breakdown (readable text), cache and aggregation
  hits, plus a persisted run history with before/after comparison of two runs.
  *(Requires SSAS admin rights — see Prerequisites.)*
- **Perfmon stats** — per-query perfmon counter deltas (MDX / cache / storage
  engine), streamed live over SignalR.
- **MDX Script & dependencies** — read the cube's MDX Script, browse calculated
  members / named sets / SCOPEs and their dependency graph, full-text search and
  find-references across the script, and an AI "explain this calculation" tracer
  that walks a member's dependency chain; export a Markdown doc of the cube.
- **SSDT project mode** — open the `.cube` file of an SSDT Multidimensional project
  (type a path or use the built-in file browser), edit the MDX Script with
  `// #region` grouping and folding, save round-trips into the `.cube` (plus a
  plain-text `.mdxscript.mdx` export for readable Git diffs), and deploy the script
  alone to a dev cube (BIDS Helper style, with a divergence guard and dev-catalog
  warning) without a full project deploy. On divergence a side-by-side Monaco diff
  shows server vs project before you overwrite, with a change-impact analysis
  listing which downstream members a change affects; you can also edit a calculated
  member's properties (format string, display folder, description) with round-trip
  writeback into the `.cube`, safely rename a calculated member across the whole
  script (definition + references), and every deploy is kept in an audit log.
- **AI assistant** — Explain / Optimize / Detect anti-patterns / Format, a
  **profiler-grounded optimization** action that feeds the AI the real execution
  profile (Formula/Storage Engine split, subcubes, cache/aggregation hits) for
  concrete, numbers-justified advice, and **natural-language → MDX** (describe what
  you want, the AI writes the query from the cube's metadata). Powered by the
  Anthropic API (`claude-opus-4-8`) by default, with the relevant cube metadata
  injected into the context. Can also target any **OpenAI-compatible** endpoint
  (local Ollama / LM Studio for on-prem confidentiality, OpenAI, Mistral,
  OpenRouter, Groq…) — see Prerequisites. *(Requires an API key — see Prerequisites.)*
- **Cache management** — clear the SSAS cache of a catalog (explicit confirmation).
- **History** — every query stored locally (SQLite), filterable, reloadable.
- **Bilingual UI** — French (default) and English, switchable at runtime.

| Metadata explorer & MDX Script | Query profiler |
|---|---|
| ![Script and dependencies](docs/screenshots/03-script.png) | ![Profiler](docs/screenshots/04-profiler.png) |

---

## Prerequisites

**To run the published executable:**

- **Windows** (x64). CubeScope uses Windows-only perfmon APIs and Integrated
  Security — it is a Windows tool by design.
- **WebView2 Runtime** — pre-installed on Windows 11 and on up-to-date
  Windows 10 (1803+). It may be missing on Windows Server and LTSC editions;
  CubeScope then falls back to your default browser instead of refusing to
  start. Pass `--force-browser` to take that path on purpose.
- **Network access** to an SSAS **Multidimensional** instance. All connections
  use **Windows Integrated Security** — no credentials are stored anywhere.
- **SSAS administrator rights** on the target instance are required for the
  **Query Profiler** (it creates a server-side trace). The rest of the app works
  without them.
- **"Performance Monitor Users" group** membership on the SSAS server, plus the
  **Remote Registry** service running, are required for the **Perfmon stats**
  panel when profiling a remote server.
- **`ANTHROPIC_API_KEY`** environment variable for the **AI assistant**. If it
  is absent, the AI panel degrades gracefully with a clear message; everything
  else keeps working. The key is read from the environment only — it is never
  stored locally. The Anthropic model defaults to `claude-opus-4-8`; set
  `CUBESCOPE_ANTHROPIC_MODEL` to use a different Claude model.
  - **Alternative LLM providers (optional).** To use any OpenAI-compatible
    endpoint instead of Anthropic — a **local** model (Ollama, LM Studio) so no
    data leaves your network, or OpenAI / Mistral / OpenRouter / Groq — set
    `CUBESCOPE_LLM_BASEURL` (e.g. `http://localhost:11434/v1`) and
    `CUBESCOPE_LLM_MODEL` (e.g. `qwen2.5-coder`), plus `CUBESCOPE_LLM_KEY` if the
    endpoint needs a bearer token. When both base URL and model are set they take
    precedence over Anthropic. The active model name is shown in the AI panel.
    *(Azure OpenAI's non-standard deployment URL and `api-key` header are not
    covered.)* **Ollama users:** CubeScope doesn't send a context-length
    parameter, so your model runs at whatever context size it was loaded with
    (often 2048–4096 by default) — too small for the natural-language → MDX
    metadata dump on a cube with hundreds of measures. Create a variant with a
    larger context first: `ollama create mymodel-32k -f Modelfile` with
    `PARAMETER num_ctx 32768` in the Modelfile, and point
    `CUBESCOPE_LLM_MODEL` at that. Expect noticeably lower MDX quality than
    Claude for this — MDX is a niche language most local models barely saw
    in training.

**To build from source, additionally:**

- **.NET 10 SDK** (10.0.401 or a later feature band, see `global.json`) —
  target framework `net10.0-windows`.
- **Node.js LTS** — builds the Vue SPA that gets embedded into the executable.

---

## Run

Download `cubescope.exe` from the [latest release](../../releases/latest),
then:

```powershell
.\cubescope.exe
```

It starts Kestrel on a free localhost port and opens in a native window
(WebView2) by default — falling back to your default browser if the WebView2
runtime isn't available, or when passed `--force-browser`. Connect to your
SSAS server (a hostname, or `host:port` for a named instance on a fixed port),
pick a catalog, and start writing MDX.

---

## Build from source

```powershell
# Restore + build + run the .NET unit tests (excludes live-SSAS integration tests)
dotnet build CubeScope.slnx -c Release
dotnet test  CubeScope.Core.Tests -c Release --filter "Category!=Integration"

# Front-end (optional in dev — the publish step does this automatically)
cd CubeScope.Web
npm ci
npm run build
```

### Development loop

Run the server and the Vite dev server side by side (Vite proxies `/api` and
`/hubs` to Kestrel):

```powershell
# Terminal 1 — API on a fixed port, no auto-open (CubeScope.Server.Cli is the
# headless console host used for the dev loop; CubeScope.Server itself is a
# library and cannot be run directly)
dotnet run --project CubeScope.Server.Cli -- --port 5199 --no-browser

# Terminal 2 — Vue dev server with HMR
cd CubeScope.Web
npm run dev
```

### Publish the single executable

```powershell
dotnet publish CubeScope.Shell -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EmbedSpa=true -o publish
```

`CubeScope.Shell` is the WPF host that produces `cubescope.exe` — publishing
`CubeScope.Server` (a library) won't work. `-p:EmbedSpa=true` is explicit
because the `EmbedSpa` MSBuild target lives in `CubeScope.Server.csproj`,
which is no longer the published project; it builds the Vue SPA and embeds
`CubeScope.Web/dist` into the `CubeScope.Server` assembly as an
`EmbeddedResource` under the `spa/` prefix, served at runtime by
`EmbeddedSpaFileProvider` (not extracted to a `wwwroot` folder on disk).
This produces a single self-contained `publish/cubescope.exe`
(~200 MB — it bundles the .NET runtime). Each GitHub Release also ships
`cubescope.exe.sha256` to verify the download (`Get-FileHash cubescope.exe`).

---

## Architecture

A single executable, `cubescope.exe`: a WPF shell (`CubeScope.Shell`) that
starts Kestrel in-process (ASP.NET Core 10, on a free localhost port) and
displays the Vue 3 SPA in a native WebView2 window — falling back to your
default browser when the WebView2 runtime is unavailable, or when passed
`--force-browser`.

| Project | Role |
|---|---|
| **CubeScope.Core** | Business services — SSAS connectivity, DMV/metadata, cell-set mapping, profiler aggregation, MDX tokenizer, AI service. No web dependency. |
| **CubeScope.Server** | Minimal API + SignalR hubs + SPA embedding (`EmbedSpa` MSBuild target). A library, referenced by both hosts below — it produces nothing on its own. |
| **CubeScope.Shell** | WPF window hosting Kestrel and a WebView2 control. Produces `cubescope.exe`, the published executable. |
| **CubeScope.Server.Cli** | Headless console host, no window — used for the local dev loop; the same entry point (`ServerHost`) is also what `CubeScope.Core.Tests` (e.g. `ServerHostTests`) exercises directly, not this Cli project. Not published (`IsPublishable=false`). |
| **CubeScope.Web** | Vue 3 + TypeScript (strict) + Vite. Monaco editor, dockview layout, PrimeVue components. |
| **CubeScope.Spike** | SSAS server-behaviour harness kept as a non-regression tool. Read-only by default (`--discover`, see below). |

Key technical choices:

- **SSAS connectivity** via `Microsoft.AnalysisServices.AdomdClient.NetCore`
  (ADOMD.NET Core) for queries and DMVs, and AMO (`.NetCore` variant) only to
  read the MDX Script and resolve object IDs.
- **Metadata** from `$SYSTEM.MDSCHEMA_*` DMVs, members lazy-loaded and cached.
- **Profiler** built on SSAS traces (`QueryEnd`, `QuerySubcube` /
  `QuerySubcubeVerbose`, cache/aggregation events), streamed to the UI over
  SignalR.
- **Local state** in a single SQLite file (history, recent connections, layouts).
- **MDX parsing** is a pragmatic tokenizer (no full AST) — it powers highlighting,
  reference detection and the dependency graph.

### Server harness (`CubeScope.Spike`)

```powershell
# Read-only (default, same as --discover): version, catalogs, cubes, LAST_DATA_UPDATE
dotnet run --project CubeScope.Spike -- <server>

# Full go/no-go run — clears the SSAS cache of a catalog, so it is refused unless the
# server is listed (exact name, case-insensitive, ';'-separated) in this variable.
# Empty or missing list = refused (exit code 2).
$env:CUBESCOPE_SPIKE_DEV_SERVERS = 'my-dev-ssas'
dotnet run --project CubeScope.Spike -- my-dev-ssas --clear-cache [--catalog <catalog>]

# Profiler spike — creates (then drops) a server-side trace; SSAS admin rights required
dotnet run --project CubeScope.Spike -- <server> --profile [--catalog <catalog>]
```

`<server>` defaults to `CUBESCOPE_SPIKE_SERVER`, then `localhost`.

---

## Security notes

- All SSAS connections use **Windows Integrated Security**. No credentials are
  read from, or written to, disk or config.
- The AI API key is read from the environment only (`ANTHROPIC_API_KEY`, or
  `CUBESCOPE_LLM_KEY` for an OpenAI-compatible provider) — never from, or written
  to, disk or config.
- **Transitive advisories (resolved):** the ADOMD.NET Core client used to pull in
  `Microsoft.Identity.Client` 4.56.0, flagged by NU1901/NU1902 (low/moderate).
  CubeScope now pins `Microsoft.Identity.Client` 4.86.1 directly, forcing the
  transitive up to a patched version — `dotnet list --vulnerable` is clean.
  (CubeScope does not use Entra ID authentication — Integrated Security only — so
  the affected code path was never exercised anyway.)

---

## License

[MIT](LICENSE) © 2026 David Simon — Financière de la Cité.
