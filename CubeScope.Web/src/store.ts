// État partagé de l'application (un seul utilisateur, une seule session SSAS) :
// un module réactif suffit — pas de Pinia pour si peu.
import { reactive } from 'vue'
import { currentLocale, t } from './i18n'
import {
  api,
  type AiAction,
  type CounterDelta,
  type CubeMeta,
  type HistoryEntry,
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

export const store = reactive({
  // Connexion
  server: '',
  catalog: '' as string | null,
  catalogs: [] as string[],
  connected: false,
  connecting: false,
  connectError: '',
  recent: [] as RecentConnection[],
  showConnect: true,

  // Métadonnées du cube courant
  cubes: [] as string[],
  cube: '' as string | null,
  cubeMeta: null as CubeMeta | null,
  metaLoading: false,

  // Éditeur / exécution
  mdx: DEFAULT_MDX,
  mdxRevision: 0, // incrémenté quand le MDX est remplacé de l'extérieur (historique)
  insertText: '', // texte à insérer au curseur (explorateur)
  insertRevision: 0,
  running: false,
  result: null as QueryResult | null,
  queryError: '',

  // Historique
  history: [] as HistoryEntry[],

  // Stats perfmon (poussées par SignalR après chaque requête)
  stats: [] as CounterDelta[],
  statsQueryDurationMs: 0,
  statsStatus: null as StatsStatus | null,

  // Profiler (trace SSAS, poussé par SignalR après chaque requête)
  profile: null as QueryProfile | null,
  profilerStatus: null as StatsStatus | null,

  // Panneau IA
  aiConfigured: null as boolean | null,
  aiRunning: false,
  aiAction: null as AiAction | null,
  aiResult: '',
  aiError: '',
  aiDurationMs: 0,
})

let abort: AbortController | null = null
let aiAbort: AbortController | null = null

export const actions = {
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
      // La découverte perfmon côté serveur est asynchrone (~secondes) : statut différé
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

  /** Demande d'insertion au curseur de l'éditeur (explorateur → Monaco). */
  requestInsert(text: string): void {
    store.insertText = text
    store.insertRevision++
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
  },

  async loadAiStatus(): Promise<void> {
    try {
      store.aiConfigured = (await api.aiStatus()).configured
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

  cancelAi(): void {
    aiAbort?.abort()
  },

  /** Applique le premier bloc ```mdx de la réponse IA à l'éditeur (Formater/Optimiser). */
  applyAiMdx(): void {
    const match = store.aiResult.match(/```mdx\s*\n([\s\S]*?)```/i) ?? store.aiResult.match(/```\s*\n([\s\S]*?)```/)
    if (!match) return
    store.mdx = match[1].trimEnd() + '\n'
    store.mdxRevision++
  },

  async run(): Promise<void> {
    if (store.running || !store.connected || !store.catalog) return
    store.running = true
    store.queryError = ''
    store.stats = [] // les deltas de la nouvelle requête arriveront par SignalR
    abort = new AbortController()
    try {
      store.result = await api.query(store.mdx, abort.signal)
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

  async loadHistory(): Promise<void> {
    try {
      store.history = await api.history()
    } catch {
      /* non bloquant */
    }
  },

  loadFromHistory(entry: HistoryEntry): void {
    store.mdx = entry.mdx
    store.mdxRevision++
  },
}
