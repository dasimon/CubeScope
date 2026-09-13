// Shared application state (a single user, a single SSAS session):
// a reactive module is enough — no Pinia for so little.
import { reactive } from 'vue'
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

export interface ResultTab {
  id: number
  label: string
  result: QueryResult
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

  // Editor / execution
  mdx: DEFAULT_MDX,
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

let abort: AbortController | null = null
let aiAbort: AbortController | null = null
let resultSeq = 0 // monotonic counter of result tabs (no Date.now/Math.random)

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

  async connect(server: string): Promise<boolean> {
    store.connecting = true
    store.connectError = ''
    try {
      const r = await api.connect(server)
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

  async setCatalog(catalog: string): Promise<void> {
    await api.setCatalog(catalog)
    store.catalog = catalog
    void actions.loadMetadata()
  },

  async loadMetadata(refresh = false): Promise<void> {
    store.metaLoading = true
    try {
      const { resetCompletionCache } = await import('./mdx-completion')
      resetCompletionCache()
      store.cubes = await api.cubes()
      store.cube = store.cubes[0] ?? null
      store.cubeMeta = store.cube ? await api.cubeMeta(store.cube, refresh) : null
    } catch {
      store.cubeMeta = null
    } finally {
      store.metaLoading = false
    }
  },

  async selectCube(cube: string, refresh = false): Promise<void> {
    store.cube = cube
    store.metaLoading = true
    try {
      store.cubeMeta = await api.cubeMeta(cube, refresh)
    } finally {
      store.metaLoading = false
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
    if (!store.profile) {
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
      const r = await api.aiOptimizeProfile(store.mdx, store.profile, currentLocale(), aiAbort.signal)
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
    abort = new AbortController()
    try {
      const result = await api.query(mdx, abort.signal)
      const id = ++resultSeq
      const label = `#${id} · ${result.cellCount} ${t('history.cells')} · ${result.durationMs} ${t('history.ms')}`
      store.results.unshift({ id, label, result })
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
    abort = new AbortController()
    try {
      const result = await api.drillthrough(mdx, maxRows, abort.signal)
      const id = ++resultSeq
      const label = `⤵ ${t('results.drillthrough')} · ${result.rows.length} ${t('history.cells')} · ${result.durationMs} ${t('history.ms')}`
      store.results.unshift({ id, label, result })
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
