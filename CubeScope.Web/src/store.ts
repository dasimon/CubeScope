// Shared application state (a single user, a single SSAS session):
// a reactive module is enough — no Pinia for so little.
import { reactive, watch } from 'vue'
import { currentLocale, t } from './i18n'
import {
  api,
  type AiAction,
  type CounterDelta,
  type CubeMeta,
  type HistoryEntry,
  type ProfileRun,
  type QueryProfile,
  type QueryResult,
  type RecentConnection,
  type StatsStatus,
} from './api'

const DEFAULT_MDX = `-- ${t('editor.defaultComment')}
SELECT
    { } ON COLUMNS
FROM [ ]
`

// The editor content survives a restart (per-machine convenience, not a document store).
const MDX_STORAGE_KEY = 'cubescope.mdx'

function loadSavedMdx(): string | null {
  try {
    return localStorage.getItem(MDX_STORAGE_KEY)
  } catch {
    return null // storage unavailable (blocked, private mode): start from the default
  }
}

export interface ResultTab {
  id: number
  label: string
  result: QueryResult
  /** MDX actually executed for this tab (the selection if there was one). */
  mdx: string
  /** A drillthrough cannot be saved as a regression baseline. */
  kind: 'query' | 'drillthrough'
}

const MAX_RESULT_TABS = 8

export const store = reactive({
  // Connection
  server: '',
  catalog: '' as string | null,
  catalogs: [] as string[],
  /** Servers declared as development: only these accept a script deployment. */
  devServers: [] as string[],
  connected: false,
  connecting: false,
  connectError: '',
  recent: [] as RecentConnection[],
  showConnect: true,

  // Metadata of the current cube
  cubes: [] as string[],
  cube: '' as string | null,
  cubeMeta: null as CubeMeta | null,
  metaLoading: false,
  /** Incremented whenever the server / catalog / cube in use changes: views holding
   *  cube-dependent state watch it (the cube NAME alone may not change — prod and dev
   *  carry the same catalog and cube names). */
  contextRevision: 0,

  // Editor / execution
  mdx: loadSavedMdx() ?? DEFAULT_MDX,
  mdxRevision: 0, // incremented when the MDX is replaced from outside (history)
  selectedMdx: '', // current selection in Monaco; non-empty → executed in preference to store.mdx
  insertText: '', // text to insert at the cursor (explorer)
  insertRevision: 0,
  // "Go to definition": meeting point between the MDX editor and the Script panel,
  // which live in two separate dockview roots. The revision allows requesting the SAME
  // definition twice in a row (otherwise the watch would not fire again).
  gotoDefinition: '' as string,
  gotoDefinitionRevision: 0,
  running: false,
  result: null as QueryResult | null,
  queryError: '',

  // Result tabs (latest runs, closable)
  results: [] as ResultTab[],
  activeResultId: 0,

  // History
  history: [] as HistoryEntry[],

  // Perfmon stats (pushed by SignalR after each query)
  stats: [] as CounterDelta[],
  statsQueryDurationMs: 0,
  statsStatus: null as StatsStatus | null,

  // Profiler (SSAS trace, pushed by SignalR after each query)
  profile: null as QueryProfile | null,
  /** MDX of the query that produced `profile` (sent to "Optimize (profile)"). */
  profileMdx: null as string | null,
  profilerStatus: null as StatsStatus | null,
  profilerHistory: [] as ProfileRun[],

  // AI panel
  aiConfigured: null as boolean | null,
  aiModel: 'claude-opus-4-8', // active model (Anthropic by default, or a configured OpenAI-compatible LLM)
  aiRunning: false,
  aiAction: null as AiAction | null,
  aiResult: '',
  aiError: '',
  aiDurationMs: 0,
})

/** Same rule as the server's DevServerGuard: exact match of the trimmed name, case-insensitive. */
export function isDevServer(server: string | null | undefined): boolean {
  const target = (server ?? '').trim().toLowerCase()
  return target.length > 0 && store.devServers.some((s) => s.trim().toLowerCase() === target)
}

let abort: AbortController | null = null
let aiAbort: AbortController | null = null
let resultSeq = 0 // monotonic counter of result tabs (no Date.now/Math.random)
let metaSeq = 0 // latest metadata request: older responses are ignored
let pendingProfileMdx: string | null = null // MDX of the last query sent (its profile comes by SignalR)

let saveMdxTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => store.mdx,
  (mdx) => {
    clearTimeout(saveMdxTimer)
    saveMdxTimer = setTimeout(() => {
      try {
        localStorage.setItem(MDX_STORAGE_KEY, mdx)
      } catch {
        /* storage unavailable or full: the editor keeps working */
      }
    }, 500)
  },
)

export const actions = {
  /** Asks the Script panel to move to the definition of a calculated member/set. */
  requestDefinition(name: string): void {
    store.gotoDefinition = name
    store.gotoDefinitionRevision++
  },

  async loadRecent(): Promise<void> {
    try {
      store.recent = await api.recent()
    } catch {
      store.recent = []
    }
  },

  /**
   * Forgets everything tied to the previous server / catalog / cube. `cube`: same catalog,
   * another cube (results stay valid); `catalog` and `server`: the results, profile and
   * error belonged to another database.
   */
  resetContext(scope: 'server' | 'catalog' | 'cube'): void {
    metaSeq++ // a metadata response still in flight belongs to the old context
    store.contextRevision++
    store.cubeMeta = null
    store.metaLoading = false
    if (scope !== 'cube') {
      store.cubes = []
      store.cube = null
      store.results = []
      store.activeResultId = 0
      store.result = null
      store.queryError = ''
      store.stats = []
      store.profile = null
      store.profileMdx = null
    }
    void import('./mdx-completion').then((m) => m.resetCompletionCache())
  },

  async connect(server: string): Promise<boolean> {
    store.connecting = true
    store.connectError = ''
    try {
      const r = await api.connect(server)
      actions.resetContext('server')
      store.server = r.server
      store.catalogs = r.catalogs
      store.catalog = null
      store.connected = true
      void actions.loadDevServers()
      // Perfmon discovery on the server side is asynchronous (~seconds): deferred status
      setTimeout(() => void actions.loadStatsStatus(), 5000)
      return true
    } catch (e) {
      store.connectError = e instanceof Error ? e.message : String(e)
      return false
    } finally {
      store.connecting = false
    }
  },

  /** Throws if the server refuses the catalog: callers show the error. */
  async setCatalog(catalog: string): Promise<void> {
    await api.setCatalog(catalog)
    actions.resetContext('catalog')
    store.catalog = catalog
    void actions.loadMetadata()
  },

  async loadMetadata(refresh = false): Promise<void> {
    const seq = ++metaSeq
    store.metaLoading = true
    try {
      const { resetCompletionCache } = await import('./mdx-completion')
      resetCompletionCache()
      const cubes = await api.cubes()
      if (seq !== metaSeq) return
      store.cubes = cubes
      store.cube = cubes[0] ?? null
      const meta = store.cube ? await api.cubeMeta(store.cube, refresh) : null
      if (seq !== metaSeq) return
      store.cubeMeta = meta
    } catch {
      if (seq === metaSeq) store.cubeMeta = null
    } finally {
      if (seq === metaSeq) store.metaLoading = false
    }
  },

  /** Throws if the metadata cannot be read: callers show the error. */
  async selectCube(cube: string, refresh = false): Promise<void> {
    if (cube !== store.cube) actions.resetContext('cube')
    const seq = ++metaSeq
    store.cube = cube
    store.metaLoading = true
    try {
      const meta = await api.cubeMeta(cube, refresh)
      if (seq === metaSeq) store.cubeMeta = meta
    } finally {
      if (seq === metaSeq) store.metaLoading = false
    }
  },

  /** Request to insert at the editor cursor (explorer → Monaco). */
  requestInsert(text: string): void {
    store.insertText = text
    store.insertRevision++
  },

  async loadDevServers(): Promise<void> {
    try {
      store.devServers = await api.devServers()
    } catch {
      // Unreadable list: leave it empty. Fail-closed — any deployment will be warned about
      // then refused by the server, rather than allowed on missing information.
      store.devServers = []
    }
  },

  async setDevServer(server: string, isDev: boolean): Promise<void> {
    store.devServers = await api.setDevServer(server, isDev)
  },

  async loadStatsStatus(): Promise<void> {
    try {
      store.statsStatus = await api.statsStatus()
    } catch {
      store.statsStatus = null
    }
  },

  async loadProfilerStatus(): Promise<void> {
    try {
      store.profilerStatus = await api.profilerStatus()
    } catch {
      store.profilerStatus = null
    }
  },

  setProfile(p: QueryProfile): void {
    store.profile = p
    store.profileMdx = pendingProfileMdx
    if (store.profilerStatus?.status !== 'Ready') {
      store.profilerStatus = { status: 'Ready', detail: store.profilerStatus?.detail ?? null }
    }
    void actions.loadProfilerHistory()
  },

  async loadProfilerHistory(): Promise<void> {
    try {
      store.profilerHistory = await api.profilerHistory()
    } catch {
      /* non-blocking */
    }
  },

  async loadAiStatus(): Promise<void> {
    try {
      const s = await api.aiStatus()
      store.aiConfigured = s.configured
      if (s.model) store.aiModel = s.model
    } catch {
      store.aiConfigured = null
    }
  },

  async runAi(action: AiAction): Promise<void> {
    if (store.aiRunning) return
    store.aiRunning = true
    store.aiAction = action
    store.aiResult = ''
    store.aiError = ''
    aiAbort = new AbortController()
    try {
      const r = await api.ai(action, store.mdx, currentLocale(), aiAbort.signal)
      store.aiResult = r.text
      store.aiDurationMs = r.durationMs
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        store.aiError = t('errors.aiCanceled')
      } else {
        store.aiError = e instanceof Error ? e.message : String(e)
      }
    } finally {
      store.aiRunning = false
      aiAbort = null
    }
  },

  /** Generates MDX from a natural-language request + the cube metadata. */
  async generateMdx(question: string): Promise<void> {
    if (store.aiRunning) return
    if (!store.cube || !question.trim()) return
    store.aiRunning = true
    store.aiAction = 'generate-mdx'
    store.aiResult = ''
    store.aiError = ''
    aiAbort = new AbortController()
    try {
      const r = await api.generateMdx(store.cube, question.trim(), currentLocale(), aiAbort.signal)
      store.aiResult = r.text
      store.aiDurationMs = r.durationMs
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        store.aiError = t('errors.aiCanceled')
      } else {
        store.aiError = e instanceof Error ? e.message : String(e)
      }
    } finally {
      store.aiRunning = false
      aiAbort = null
    }
  },

  /** AI optimization backed by the actual execution profile (requires a captured profile). */
  async runAiOptimizeProfile(): Promise<void> {
    if (store.aiRunning) return
    if (!store.profile || !store.profileMdx) {
      store.aiAction = 'optimize-profile'
      store.aiResult = ''
      store.aiError = t('ai.needProfile')
      return
    }
    store.aiRunning = true
    store.aiAction = 'optimize-profile'
    store.aiResult = ''
    store.aiError = ''
    aiAbort = new AbortController()
    try {
      // The MDX that produced the profile, not the editor's (it may have changed since).
      const r = await api.aiOptimizeProfile(store.profileMdx, store.profile, currentLocale(), aiAbort.signal)
      store.aiResult = r.text
      store.aiDurationMs = r.durationMs
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        store.aiError = t('errors.aiCanceled')
      } else {
        store.aiError = e instanceof Error ? e.message : String(e)
      }
    } finally {
      store.aiRunning = false
      aiAbort = null
    }
  },

  cancelAi(): void {
    aiAbort?.abort()
  },

  /** Applies the first ```mdx block of the AI response to the editor (Format/Optimize). */
  applyAiMdx(): void {
    const match = store.aiResult.match(/```mdx\s*\n([\s\S]*?)```/i) ?? store.aiResult.match(/```\s*\n([\s\S]*?)```/)
    if (!match) return
    store.mdx = match[1].trimEnd() + '\n'
    store.mdxRevision++
  },

  async run(): Promise<void> {
    if (store.running || !store.connected || !store.catalog) return
    const mdx = store.selectedMdx.trim() ? store.selectedMdx : store.mdx
    store.running = true
    store.queryError = ''
    store.stats = [] // the new query's deltas will arrive through SignalR
    store.profile = null // same for its profile: never pair an older profile with this query
    store.profileMdx = null
    pendingProfileMdx = mdx
    abort = new AbortController()
    try {
      const result = await api.query(mdx, abort.signal)
      const id = ++resultSeq
      const label = `#${id} · ${result.cellCount} ${t('history.cells')} · ${result.durationMs} ${t('history.ms')}`
      store.results.unshift({ id, label, result, mdx, kind: 'query' })
      if (store.results.length > MAX_RESULT_TABS) store.results.length = MAX_RESULT_TABS
      store.activeResultId = id
      store.result = result
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        store.queryError = t('errors.queryCanceled')
      } else {
        store.queryError = e instanceof Error ? e.message : String(e)
      }
    } finally {
      store.running = false
      abort = null
      void actions.loadHistory()
    }
  },

  cancel(): void {
    abort?.abort()
  },

  /**
   * Wraps the current query in DRILLTHROUGH and shows the source rows in a new
   * result tab. Known limitation: no precise per-cell drillthrough (right-click) —
   * the ENTIRE query is wrapped, which the server only accepts as "drillthroughable"
   * for a single-cell query.
   */
  async runDrillthrough(maxRows = 1000): Promise<void> {
    if (store.running || !store.connected || !store.catalog) return
    const mdx = store.selectedMdx.trim() ? store.selectedMdx : store.mdx
    store.running = true
    store.queryError = ''
    store.profile = null
    store.profileMdx = null
    pendingProfileMdx = null // the server wraps the query: no editor MDX matches its profile
    abort = new AbortController()
    try {
      const result = await api.drillthrough(mdx, maxRows, abort.signal)
      const id = ++resultSeq
      const label = `⤵ ${t('results.drillthrough')} · ${result.rows.length} ${t('results.rows')} · ${result.durationMs} ${t('history.ms')}`
      store.results.unshift({ id, label, result, mdx, kind: 'drillthrough' })
      if (store.results.length > MAX_RESULT_TABS) store.results.length = MAX_RESULT_TABS
      store.activeResultId = id
      store.result = result
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        store.queryError = t('errors.queryCanceled')
      } else {
        store.queryError = e instanceof Error ? e.message : String(e)
      }
    } finally {
      store.running = false
      abort = null
    }
  },

  /** Activates an existing result tab (grid = its result). */
  selectResult(id: number): void {
    const tab = store.results.find((r) => r.id === id)
    if (!tab) return
    store.activeResultId = id
    store.result = tab.result
    store.queryError = '' // otherwise the last error keeps hiding the selected tab
  },

  /** Closes a result tab; re-activates the most recent remaining one if it was the active tab. */
  closeResult(id: number): void {
    const idx = store.results.findIndex((r) => r.id === id)
    if (idx === -1) return
    store.results.splice(idx, 1)
    if (store.activeResultId === id) {
      const next = store.results[0]
      if (next) {
        store.activeResultId = next.id
        store.result = next.result
      } else {
        store.activeResultId = 0
        store.result = null
      }
    }
  },

  async loadHistory(): Promise<void> {
    try {
      store.history = await api.history()
    } catch {
      /* non-blocking */
    }
  },

  loadFromHistory(entry: HistoryEntry): void {
    store.mdx = entry.mdx
    store.mdxRevision++
  },
}
