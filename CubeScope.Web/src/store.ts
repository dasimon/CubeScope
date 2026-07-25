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
  selectedMdx: '', // sélection courante dans Monaco ; non vide → exécutée en priorité sur store.mdx
  insertText: '', // texte à insérer au curseur (explorateur)
  insertRevision: 0,
  running: false,
  result: null as QueryResult | null,
  queryError: '',

  // Onglets de résultats (derniers runs, fermables)
  results: [] as ResultTab[],
  activeResultId: 0,

  // Historique
  history: [] as HistoryEntry[],

  // Stats perfmon (poussées par SignalR après chaque requête)
  stats: [] as CounterDelta[],
  statsQueryDurationMs: 0,
  statsStatus: null as StatsStatus | null,

  // Profiler (trace SSAS, poussé par SignalR après chaque requête)
  profile: null as QueryProfile | null,
  profilerStatus: null as StatsStatus | null,
  profilerHistory: [] as ProfileRun[],

  // Panneau IA
  aiConfigured: null as boolean | null,
  aiModel: 'claude-opus-4-8', // modèle actif (Anthropic par défaut, ou LLM compatible OpenAI configuré)
  aiRunning: false,
  aiAction: null as AiAction | null,
  aiResult: '',
  aiError: '',
  aiDurationMs: 0,
})

let abort: AbortController | null = null
let aiAbort: AbortController | null = null
let resultSeq = 0 // compteur monotone d'onglets de résultats (pas de Date.now/Math.random)

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
    void actions.loadProfilerHistory()
  },

  async loadProfilerHistory(): Promise<void> {
    try {
      store.profilerHistory = await api.profilerHistory()
    } catch {
      /* non bloquant */
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

  /** Génère du MDX depuis une demande en langage naturel + les métadonnées du cube. */
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

  /** Optimisation IA adossée au profil d'exécution réel (nécessite un profil capturé). */
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

  /** Applique le premier bloc ```mdx de la réponse IA à l'éditeur (Formater/Optimiser). */
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
    store.stats = [] // les deltas de la nouvelle requête arriveront par SignalR
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
   * Enveloppe la requête courante dans DRILLTHROUGH et affiche les lignes sources dans un
   * nouvel onglet de résultats. Limitation connue : pas de drillthrough précis par cellule
   * (clic droit) — la requête ENTIÈRE est enveloppée, ce qui n'est « drillthroughable » côté
   * serveur que pour une requête à une cellule.
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

  /** Active un onglet de résultats existant (grille = son résultat). */
  selectResult(id: number): void {
    const tab = store.results.find((r) => r.id === id)
    if (!tab) return
    store.activeResultId = id
    store.result = tab.result
  },

  /** Ferme un onglet de résultats ; réactive le plus récent restant si c'était l'actif. */
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
      /* non bloquant */
    }
  },

  loadFromHistory(entry: HistoryEntry): void {
    store.mdx = entry.mdx
    store.mdxRevision++
  },
}
